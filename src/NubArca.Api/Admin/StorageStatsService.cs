using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;

namespace NubArca.Api.Admin;

public sealed class StorageStatsService : IStorageStatsService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<FileItemSweeperOptions> _sweeperOptions;
    private readonly IOptionsMonitor<BlobJanitorOptions> _janitorOptions;
    private readonly IOptionsMonitor<BlobStorageOptions> _storageOptions;
    // Slice 78: optional — not all environments (e.g. SQLite test harness
    // without a real storage root) inject these. When null, physical-blob
    // counts are reported as -1.
    private readonly IBlobStorage? _storage;
    private readonly IDerivedBlobStorage? _derivedStorage;
    // Slice 78.1: optional logger so a failing non-core diagnostic sub-query
    // can be recorded (metric name + exception type only — never values) while
    // the endpoint degrades gracefully instead of returning 500.
    private readonly ILogger<StorageStatsService>? _logger;
    // Slice 84: optional short-lived cache (singleton). Null in direct-
    // construction test sites → every call recomputes (no caching).
    private readonly StorageStatsCache? _cache;
    // Slice 97: optional logical reference-integrity audit, reusing the exact
    // computation behind `storage blobs audit-references`. Null in direct-
    // construction test sites → the section is omitted.
    private readonly BlobReferenceAuditService? _referenceAudit;
    // Slice 99: optional derivative-diagnostics aggregation. Null → the
    // DerivativeDiagnostics section is omitted.
    private readonly DerivativeDiagnosticsService? _derivativeDiagnostics;

    // The whole response is cached for this window so repeated admin loads
    // don't re-run the heavy filesystem scan + aggregates. `?refresh=true`
    // forces a recompute.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public StorageStatsService(
        AppDbContext db,
        TimeProvider clock,
        IOptionsMonitor<FileItemSweeperOptions> sweeperOptions,
        IOptionsMonitor<BlobJanitorOptions> janitorOptions,
        IOptionsMonitor<BlobStorageOptions> storageOptions,
        IBlobStorage? storage = null,
        IDerivedBlobStorage? derivedStorage = null,
        ILogger<StorageStatsService>? logger = null,
        StorageStatsCache? cache = null,
        BlobReferenceAuditService? referenceAudit = null,
        DerivativeDiagnosticsService? derivativeDiagnostics = null)
    {
        _db = db;
        _clock = clock;
        _sweeperOptions = sweeperOptions;
        _janitorOptions = janitorOptions;
        _storageOptions = storageOptions;
        _storage = storage;
        _derivedStorage = derivedStorage;
        _logger = logger;
        _cache = cache;
        _referenceAudit = referenceAudit;
        _derivativeDiagnostics = derivativeDiagnostics;
    }

    // Slice 78.1: run a NON-CORE diagnostic sub-query, returning `fallback` if
    // it throws (e.g. a provider-specific quirk). Logs the metric name + the
    // exception type ONLY — never any row value — so a single broken metric
    // degrades gracefully instead of failing the whole Storage Stats page.
    private async Task<T> SafeDiagnosticAsync<T>(
        string metric, Func<Task<T>> query, T fallback)
    {
        try
        {
            return await query();
        }
        catch (OperationCanceledException)
        {
            throw; // honour cancellation — not a metric failure
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "Storage stats diagnostic '{Metric}' failed ({ExceptionType}); returning a safe fallback.",
                metric, ex.GetType().Name);
            return fallback;
        }
    }

    public async Task<StorageStatsResponse> GetAsync(
        bool refresh = false,
        bool includePhysicalScan = true,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // Serve from the short-lived cache unless a refresh was requested. A
        // scan-less cached value can't satisfy a request that needs the scan,
        // so only reuse the cache when it already includes the scan or the
        // caller doesn't need it.
        if (!refresh && _cache?.TryGet(CacheTtl, now) is { } hit
            && (hit.IncludedPhysical || !includePhysicalScan))
        {
            var age = (int)Math.Max(0, (now - hit.ComputedAtUtc).TotalSeconds);
            return hit.Value with
            {
                Diagnostics = hit.Value.Diagnostics with { Cached = true, AgeSeconds = age },
            };
        }

        var swTotal = Stopwatch.StartNew();
        long physicalScanMs = 0, derivativeScanMs = 0, metadataAggMs = 0;

        var janitor = _janitorOptions.CurrentValue;
        var sweeper = _sweeperOptions.CurrentValue;
        var janitorCutoff = now.AddMinutes(-janitor.GraceMinutes);

        // Users
        var users = await _db.Users.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Disabled = g.Count(u => u.DisabledAt != null),
            })
            .FirstOrDefaultAsync(cancellationToken);
        var usersTotal = users?.Total ?? 0;
        var usersDisabled = users?.Disabled ?? 0;
        var usersStats = new UserStats(usersTotal, usersTotal - usersDisabled, usersDisabled);

        // Folders
        var folders = await _db.Folders.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                SoftDeleted = g.Count(f => f.DeletedAt != null),
            })
            .FirstOrDefaultAsync(cancellationToken);
        var foldersTotal = folders?.Total ?? 0;
        var foldersDeleted = folders?.SoftDeleted ?? 0;
        var foldersStats = new FolderStats(foldersTotal, foldersTotal - foldersDeleted, foldersDeleted);

        // Files + byte totals (active vs. including-trash). Coalesce to 0 so
        // an empty table returns 0L, not null.
        var files = await _db.FileItems.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                SoftDeleted = g.Count(f => f.DeletedAt != null),
                LogicalActive = g.Where(f => f.DeletedAt == null).Sum(f => (long?)f.SizeBytes) ?? 0L,
                LogicalAll = g.Sum(f => (long?)f.SizeBytes) ?? 0L,
            })
            .FirstOrDefaultAsync(cancellationToken);
        var filesTotal = files?.Total ?? 0;
        var filesDeleted = files?.SoftDeleted ?? 0;
        var filesStats = new FileStats(
            filesTotal,
            filesTotal - filesDeleted,
            filesDeleted,
            files?.LogicalActive ?? 0L,
            files?.LogicalAll ?? 0L);

        // Blobs
        var blobs = await _db.BlobObjects.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                ZeroRef = g.Count(b => b.ReferenceCount == 0),
                ZeroRefBeyondGrace = g.Count(b => b.ReferenceCount == 0
                    && b.PurgeEligibleAt != null
                    && b.PurgeEligibleAt < janitorCutoff),
                PhysicalBytes = g.Sum(b => (long?)b.SizeBytes) ?? 0L,
            })
            .FirstOrDefaultAsync(cancellationToken);
        // Slice 78: admin storage diagnostics — cross-check DB rows vs disk.
        var activeFileItemCount = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null, cancellationToken);
        var uniqueReferencedBlobCount = await _db.FileItems.AsNoTracking()
            .Where(f => f.DeletedAt == null)
            .Select(f => f.BlobObjectId)
            .Distinct()
            .CountAsync(cancellationToken);

        // Physical-blob counts from IBlobStorage. When storage is not
        // injected (test harness without a real root) we report -1.
        // -1 means "not computed": either storage isn't injected, or the
        // expensive scan was skipped (the default for the admin dashboard).
        int physicalBlobCount = -1;
        int missingPhysicalBlobCount = -1;
        int unreferencedPhysicalBlobCount = -1;
        DerivedReadinessStats? derivedReadiness = null;
        if (_storage is not null && includePhysicalScan)
        {
            // This is the heaviest, most failure-prone phase (filesystem walk +
            // loading every DB storage key). Time it, and degrade gracefully to
            // -1 for these three fields only — never fail the whole page.
            var swPhysical = Stopwatch.GetTimestamp();
            try
            {
                var splitRoots = _derivedStorage is not null
                    && !ReferenceEquals(_derivedStorage, _storage);

                // Collect the two roots' keys SEPARATELY: the union keeps the
                // historical missing/unreferenced semantics (bytes anywhere
                // count as present — a pre-split artifact is not data loss),
                // while the per-root sets feed the slice-96 derived-readiness
                // section, which asks the question the union deliberately
                // cannot answer: are the derivative bytes where the SERVING
                // endpoints actually read them?
                var originalKeys = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var key in _storage.EnumerateStorageKeysAsync(cancellationToken))
                {
                    originalKeys.Add(key);
                }
                var derivedKeys = originalKeys;
                if (splitRoots)
                {
                    derivedKeys = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var key in _derivedStorage!.EnumerateStorageKeysAsync(cancellationToken))
                    {
                        derivedKeys.Add(key);
                    }
                }

                // All storage keys from the DB.
                var dbKeys = await _db.BlobObjects.AsNoTracking()
                    .Select(b => b.StorageKey)
                    .ToListAsync(cancellationToken);
                var dbKeySet = new HashSet<string>(dbKeys, StringComparer.Ordinal);

                physicalBlobCount = splitRoots
                    ? originalKeys.Concat(derivedKeys).Distinct(StringComparer.Ordinal).Count()
                    : originalKeys.Count;
                missingPhysicalBlobCount = dbKeys.Count(
                    k => !originalKeys.Contains(k) && !derivedKeys.Contains(k));
                unreferencedPhysicalBlobCount =
                    originalKeys.Count(k => !dbKeySet.Contains(k))
                    + (splitRoots
                        ? derivedKeys.Count(k => !dbKeySet.Contains(k) && !originalKeys.Contains(k))
                        : 0);

                // Slice 96: derived readiness — classify every FileThumbnail
                // row's bytes by placement, against the sets already in hand
                // (no extra filesystem walk). Counts only.
                var thumbKeys = await _db.FileThumbnails.AsNoTracking()
                    .Join(
                        _db.BlobObjects.AsNoTracking(),
                        t => t.BlobObjectId,
                        b => b.Id,
                        (t, b) => new { t.Size, b.StorageKey })
                    .ToListAsync(cancellationToken);

                var bySize = new Dictionary<string, int[]>(StringComparer.Ordinal);
                int[] SizeBucket(string size)
                {
                    if (!bySize.TryGetValue(size, out var b))
                    {
                        b = new int[4];
                        bySize[size] = b;
                    }
                    return b;
                }
                foreach (var row in thumbKeys)
                {
                    var bucket = SizeBucket(row.Size);
                    bucket[0]++;
                    if (derivedKeys.Contains(row.StorageKey))
                    {
                        bucket[1]++;
                    }
                    else if (originalKeys.Contains(row.StorageKey))
                    {
                        bucket[2]++;
                    }
                    else
                    {
                        bucket[3]++;
                    }
                }
                DerivedReadinessSizeStats SizeStats(string size)
                {
                    var b = SizeBucket(size);
                    return new DerivedReadinessSizeStats(b[0], b[1], b[2], b[3]);
                }
                var totals = bySize.Values.Aggregate(new int[4], (acc, b) =>
                {
                    for (var i = 0; i < 4; i++) acc[i] += b[i];
                    return acc;
                });
                derivedReadiness = new DerivedReadinessStats(
                    ThumbnailRowsTotal: totals[0],
                    PresentInDerivedRoot: totals[1],
                    OnlyInOriginalRoot: totals[2],
                    MissingFromBoth: totals[3],
                    SplitRoots: splitRoots,
                    Small: SizeStats(ThumbnailSizes.Small),
                    Medium: SizeStats(ThumbnailSizes.Medium),
                    Poster: SizeStats(ThumbnailSizes.Poster));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    "Storage stats physical-blob scan failed ({ExceptionType}); reporting -1 for physical counts.",
                    ex.GetType().Name);
            }
            physicalScanMs = (long)Stopwatch.GetElapsedTime(swPhysical).TotalMilliseconds;
        }

        // Slice 97: LOGICAL reference integrity — DB-only (no filesystem
        // walk), but full-table aggregation, so it rides the same on-demand
        // gate as the physical scan ("Run integrity check"). Degrades to null
        // on failure like every non-core diagnostic.
        ReferenceIntegrityStats? referenceIntegrity = null;
        if (_referenceAudit is not null && includePhysicalScan)
        {
            referenceIntegrity = await SafeDiagnosticAsync<ReferenceIntegrityStats?>(
                "ReferenceIntegrity",
                async () =>
                {
                    var audit = await _referenceAudit.AuditAsync(cancellationToken);
                    return new ReferenceIntegrityStats(
                        TotalBlobs: audit.TotalBlobs,
                        RefcountMismatchCount: audit.DbRefcountTooHigh + audit.DbRefcountTooLow,
                        OrphanedNonzeroRefcountCount: audit.OrphanedNonzeroRefcount,
                        ZeroRefWithRealReferencesCount: audit.ZeroRefWithRealReferences);
                },
                fallback: null);
        }

        var blobsStats = new BlobStats(
            blobs?.Total ?? 0,
            blobs?.ZeroRef ?? 0,
            blobs?.ZeroRefBeyondGrace ?? 0,
            blobs?.PhysicalBytes ?? 0L,
            activeFileItemCount,
            uniqueReferencedBlobCount,
            physicalBlobCount,
            missingPhysicalBlobCount,
            unreferencedPhysicalBlobCount);

        // Images. The image-files count is bounded to active rows so a
        // soft-deleted but not-yet-purged image doesn't double-count toward
        // "what the gallery would show". MimeType starts with image/.
        var imageFilesCount = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null && f.MimeType.StartsWith("image/"), cancellationToken);
        var filesWithDimensionsCount = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null && f.Width != null && f.Height != null, cancellationToken);

        var thumbnailCount = await _db.FileThumbnails.AsNoTracking()
            .CountAsync(cancellationToken);
        // Bytes of every blob that backs at least one thumbnail. The same
        // blob can back multiple thumbnails via dedup; using DISTINCT keeps
        // the byte count honest.
        var thumbnailBlobBytes = await _db.FileThumbnails.AsNoTracking()
            .Select(t => t.BlobObjectId)
            .Distinct()
            .Join(
                _db.BlobObjects.AsNoTracking(),
                id => id,
                b => b.Id,
                (_, b) => (long?)b.SizeBytes)
            .SumAsync(cancellationToken) ?? 0L;
        var imagesStats = new ImageStats(
            imageFilesCount,
            filesWithDimensionsCount,
            thumbnailCount,
            thumbnailBlobBytes);

        // Share links. Status precedence matches ShareLinkSummary: revoked >
        // expired > exhausted > active, with revoked excluded from expired /
        // exhausted counts so the four buckets sum to Total.
        var shareLinks = await _db.ShareLinks.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Revoked = g.Count(s => s.RevokedAt != null),
                Expired = g.Count(s =>
                    s.RevokedAt == null
                    && s.ExpiresAt != null
                    && s.ExpiresAt <= now),
                Exhausted = g.Count(s =>
                    s.RevokedAt == null
                    && (s.ExpiresAt == null || s.ExpiresAt > now)
                    && s.MaxDownloads != null
                    && s.DownloadCount >= s.MaxDownloads),
            })
            .FirstOrDefaultAsync(cancellationToken);
        var slTotal = shareLinks?.Total ?? 0;
        var slRevoked = shareLinks?.Revoked ?? 0;
        var slExpired = shareLinks?.Expired ?? 0;
        var slExhausted = shareLinks?.Exhausted ?? 0;
        var slActive = slTotal - slRevoked - slExpired - slExhausted;
        var shareLinksStats = new ShareLinkStats(slTotal, slActive, slRevoked, slExpired, slExhausted);

        // Audit
        var auditStats = new AuditStats(
            await _db.AuditLogs.AsNoTracking().CountAsync(cancellationToken));

        // Cleanup config snapshot — straight from the options snapshot, no
        // secrets, no paths.
        var cleanup = new CleanupConfig(
            new SweeperConfig(sweeper.Enabled, sweeper.IntervalMinutes, sweeper.GraceMinutes),
            new SweeperConfig(janitor.Enabled, janitor.IntervalMinutes, janitor.GraceMinutes));

        // ---- Slice 64: aggregate diagnostics ------------------------------
        // Slice 84: time the heavy metadata/derivative aggregate phase.
        var swMeta0 = Stopwatch.GetTimestamp();

        // Media counts by SERVER-DETECTED category, restricted to active
        // FileItems so soft-deleted rows don't double-count. The category
        // comes from BlobMetadata; pre-metadata files fall into Other via
        // the "no metadata row" branch.
        var mediaByCategory = await (
            from f in _db.FileItems.AsNoTracking()
            where f.DeletedAt == null
            join m in _db.BlobMetadata.AsNoTracking()
                on f.BlobObjectId equals m.BlobObjectId into mJoin
            from m in mJoin.DefaultIfEmpty()
            group f by m == null ? MediaCategories.Other : m.MediaCategory into g
            select new { Category = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(string c) => mediaByCategory.FirstOrDefault(x => x.Category == c)?.Count ?? 0;
        var otherTotal = mediaByCategory
            .Where(x => x.Category != MediaCategories.Image
                && x.Category != MediaCategories.Video
                && x.Category != MediaCategories.Audio
                && x.Category != MediaCategories.Document)
            .Sum(x => x.Count);
        var mediaStats = new MediaStats(
            ImagesCount: CountFor(MediaCategories.Image),
            VideosCount: CountFor(MediaCategories.Video),
            AudioCount: CountFor(MediaCategories.Audio),
            DocumentsCount: CountFor(MediaCategories.Document),
            OtherCount: otherTotal);

        // Extraction status / error / version distribution over BlobMetadata.
        // One row per blob — counts are blob-level, so dedup doesn't inflate.
        var extractionAggregate = await _db.BlobMetadata.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Pending = g.Count(m => m.ExtractionStatus == MetadataStatuses.Pending),
                Completed = g.Count(m => m.ExtractionStatus == MetadataStatuses.Completed),
                Skipped = g.Count(m => m.ExtractionStatus == MetadataStatuses.Skipped),
                Failed = g.Count(m => m.ExtractionStatus == MetadataStatuses.Failed),
                AtCurrentVersion = g.Count(m =>
                    m.ExtractionVersion != null
                    && m.ExtractionVersion == EmbeddedImageMetadataExtractor.Version),
                BelowCurrentVersion = g.Count(m =>
                    m.ExtractionVersion != null
                    && m.ExtractionVersion < EmbeddedImageMetadataExtractor.Version),
                UnsupportedFormatErrors = g.Count(m =>
                    m.ExtractionErrorCode == MetadataErrorCodes.UnsupportedFormat),
                IoErrors = g.Count(m =>
                    m.ExtractionErrorCode == MetadataErrorCodes.IoError),
                UnexpectedErrors = g.Count(m =>
                    m.ExtractionErrorCode == MetadataErrorCodes.Unexpected),
                RawTruncatedErrors = g.Count(m =>
                    m.ExtractionErrorCode == MetadataErrorCodes.RawTruncated),
            })
            .FirstOrDefaultAsync(cancellationToken);
        var extractionStats = new ExtractionStats(
            Pending: extractionAggregate?.Pending ?? 0,
            Completed: extractionAggregate?.Completed ?? 0,
            Skipped: extractionAggregate?.Skipped ?? 0,
            Failed: extractionAggregate?.Failed ?? 0,
            CurrentVersion: EmbeddedImageMetadataExtractor.Version,
            AtCurrentVersion: extractionAggregate?.AtCurrentVersion ?? 0,
            BelowCurrentVersion: extractionAggregate?.BelowCurrentVersion ?? 0,
            UnsupportedFormatErrors: extractionAggregate?.UnsupportedFormatErrors ?? 0,
            IoErrors: extractionAggregate?.IoErrors ?? 0,
            UnexpectedErrors: extractionAggregate?.UnexpectedErrors ?? 0,
            RawTruncatedErrors: extractionAggregate?.RawTruncatedErrors ?? 0);

        // Derivative counts by Size, plus the same "missing" buckets the
        // slice-63 prewarm CLI targets — owner-active images / videos with
        // no FileThumbnail row of the relevant size.
        var thumbnailCountsBySize = await _db.FileThumbnails.AsNoTracking()
            .GroupBy(t => t.Size)
            .Select(g => new { Size = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int DerivativeCount(string size) =>
            thumbnailCountsBySize.FirstOrDefault(x => x.Size == size)?.Count ?? 0;

        // Slice 84: the 3 "missing derivative" counts are correlated-subquery
        // scans over active FileItems — the heaviest aggregate sub-phase. Time
        // them so the operator can see when they dominate.
        var swDeriv = Stopwatch.GetTimestamp();
        var imagesMissingSmall = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null)
                && !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small),
                cancellationToken);

        var imagesMissingMedium = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null)
                && !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Medium),
                cancellationToken);

        var videosMissingPoster = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Video
                    && m.DetectedContentType != null)
                && !_db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster),
                cancellationToken);

        derivativeScanMs = (long)Stopwatch.GetElapsedTime(swDeriv).TotalMilliseconds;

        var derivativeStats = new DerivativeStats(
            SmallThumbnailCount: DerivativeCount(ThumbnailSizes.Small),
            MediumPreviewCount: DerivativeCount(ThumbnailSizes.Medium),
            VideoPosterCount: DerivativeCount(ThumbnailSizes.Poster),
            ImagesMissingSmall: imagesMissingSmall,
            ImagesMissingMedium: imagesMissingMedium,
            VideosMissingPoster: videosMissingPoster);

        // Slice 99: explain the missing counts above. Cheap GROUP BYs over the
        // (small) diagnostics table; never_attempted is derived per size as
        // missing − recorded so the buckets partition cleanly. Degrades to null
        // like every other non-core diagnostic.
        DerivativeDiagnosticsStats? derivativeDiagnostics = null;
        if (_derivativeDiagnostics is not null)
        {
            derivativeDiagnostics = await SafeDiagnosticAsync<DerivativeDiagnosticsStats?>(
                "DerivativeDiagnostics",
                async () =>
                {
                    var summary = await _derivativeDiagnostics.SummariseAsync(cancellationToken);

                    DerivativeDiagnosticSizeStats SizeStats(string size, int missing)
                    {
                        var s = summary.Sizes.FirstOrDefault(x => x.Size == size);
                        var recorded = s?.Total ?? 0;
                        return new DerivativeDiagnosticSizeStats(
                            NeverAttempted: Math.Max(0, missing - recorded),
                            Recorded: recorded,
                            FailedPermanent: s?.FailedPermanent ?? 0,
                            FailedTransient: s?.FailedTransient ?? 0,
                            NotEligible: s?.NotEligible ?? 0,
                            Skipped: s?.Skipped ?? 0,
                            Pending: s?.Pending ?? 0,
                            RetryableNow: s?.RetryableNow ?? 0,
                            LastFailureAt: s?.LastFailureAt,
                            ByErrorCode: s?.ByErrorCode
                                .Select(c => new DerivativeErrorCodeStat(c.ErrorCode, c.Count)).ToList()
                                ?? (IReadOnlyList<DerivativeErrorCodeStat>)Array.Empty<DerivativeErrorCodeStat>(),
                            TopFormats: s?.TopFormats
                                .Select(f => new DerivativeFormatStat(f.DetectedContentType, f.Count)).ToList()
                                ?? (IReadOnlyList<DerivativeFormatStat>)Array.Empty<DerivativeFormatStat>());
                    }

                    return new DerivativeDiagnosticsStats(
                        SizeStats(ThumbnailSizes.Small, imagesMissingSmall),
                        SizeStats(ThumbnailSizes.Medium, imagesMissingMedium),
                        SizeStats(ThumbnailSizes.Poster, videosMissingPoster),
                        summary.LastFailureAt);
                },
                fallback: null);
        }

        // User-metadata aggregates. Counts only; no titles / descriptions /
        // tag content / location strings ever cross this boundary.
        var userMetaAggregate = await _db.FileItemUserMetadata.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalRows = g.Count(),
                WithTitle = g.Count(u => u.Title != null && u.Title != ""),
                WithDescription = g.Count(u => u.Description != null && u.Description != ""),
                WithTags = g.Count(u => u.TagsJson != null && u.TagsJson != ""),
                WithRating = g.Count(u => u.Rating != null),
                Favorites = g.Count(u => u.IsFavorite),
                WithDateTakenOverride = g.Count(u => u.DateTakenOverride != null),
                WithLocationOverride = g.Count(u =>
                    u.LocationOverride != null && u.LocationOverride != ""),
            })
            .FirstOrDefaultAsync(cancellationToken);
        var userMetaStats = new UserMetadataStats(
            TotalRows: userMetaAggregate?.TotalRows ?? 0,
            WithTitle: userMetaAggregate?.WithTitle ?? 0,
            WithDescription: userMetaAggregate?.WithDescription ?? 0,
            WithTags: userMetaAggregate?.WithTags ?? 0,
            WithRating: userMetaAggregate?.WithRating ?? 0,
            Favorites: userMetaAggregate?.Favorites ?? 0,
            WithDateTakenOverride: userMetaAggregate?.WithDateTakenOverride ?? 0,
            WithLocationOverride: userMetaAggregate?.WithLocationOverride ?? 0);

        // Sensitive aggregates: presence counts only. GPS / serials / raw
        // doc are NEVER read here — just `IS NOT NULL` predicates.
        //
        // Root-cause fix (22P02): RawMetadataJson is `jsonb` on PostgreSQL.
        // The previous `m.RawMetadataJson != ""` compiled to `... <> ''`, and
        // `''` is invalid JSON, so PostgreSQL raised
        // `22P02: invalid input syntax for type json` and the whole endpoint
        // 500'd. Presence is correctly expressed with `!= null` alone — a
        // jsonb column cannot hold the empty string anyway. (SQLite stores
        // jsonb as dynamic text, which is why the bug never surfaced in tests.)
        // The block is also wrapped so any future provider quirk degrades to
        // zeros instead of failing the page.
        var sensitiveAggregate = await SafeDiagnosticAsync(
            "SensitiveAggregates",
            async () => await _db.BlobMetadata.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    BlobsWithGps = g.Count(m =>
                        m.GpsLatitude != null && m.GpsLongitude != null),
                    BlobsWithRawDocument = g.Count(m =>
                        m.RawMetadataJson != null),
                    BlobsWithBodySerial = g.Count(m =>
                        m.BodySerialNumber != null && m.BodySerialNumber != ""),
                    BlobsWithLensSerial = g.Count(m =>
                        m.LensSerialNumber != null && m.LensSerialNumber != ""),
                })
                .FirstOrDefaultAsync(cancellationToken),
            fallback: null);

        // Audit-action counters for the metadata-mutation events.
        var metadataUpdates = await _db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == AuditActions.FileMetadataUpdate, cancellationToken);
        var stripEvents = await _db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == AuditActions.FileMetadataStripEmbedded, cancellationToken);

        var sensitiveStats = new SensitiveAggregateStats(
            BlobsWithGps: sensitiveAggregate?.BlobsWithGps ?? 0,
            BlobsWithRawDocument: sensitiveAggregate?.BlobsWithRawDocument ?? 0,
            BlobsWithBodySerial: sensitiveAggregate?.BlobsWithBodySerial ?? 0,
            BlobsWithLensSerial: sensitiveAggregate?.BlobsWithLensSerial ?? 0,
            MetadataUpdates: metadataUpdates,
            MetadataStripEvents: stripEvents);

        // Slice 65: aggregate quota posture. Slice 84: the per-user group-by is
        // only needed to count users OVER quota — when no quota is configured
        // (the default) skip that full group-by scan entirely and reuse the
        // files aggregate's LogicalAll for the total (identical sum).
        var defaultQuota = _storageOptions.CurrentValue.DefaultUserQuotaBytes;
        long totalLogicalBytes;
        int usersOverQuota;
        if (defaultQuota > 0)
        {
            var perUserLogical = await _db.FileItems.AsNoTracking()
                .GroupBy(f => f.OwnerUserId)
                .Select(g => g.Sum(f => (long?)f.SizeBytes) ?? 0L)
                .ToListAsync(cancellationToken);
            totalLogicalBytes = perUserLogical.Sum();
            usersOverQuota = perUserLogical.Count(used => used > defaultQuota);
        }
        else
        {
            totalLogicalBytes = filesStats.LogicalBytesIncludingTrash;
            usersOverQuota = 0;
        }
        var quotaStats = new QuotaStats(
            DefaultQuotaBytes: defaultQuota > 0 ? defaultQuota : null,
            UsersOverQuota: usersOverQuota,
            TotalLogicalBytes: totalLogicalBytes);

        metadataAggMs = (long)Stopwatch.GetElapsedTime(swMeta0).TotalMilliseconds - derivativeScanMs;
        swTotal.Stop();
        var totalMs = swTotal.ElapsedMilliseconds;
        var diagnostics = new StorageStatsDiagnostics(
            TotalMillis: totalMs,
            CoreMillis: Math.Max(0, totalMs - physicalScanMs - derivativeScanMs - metadataAggMs),
            PhysicalScanMillis: physicalScanMs,
            DerivativeScanMillis: derivativeScanMs,
            MetadataAggregateMillis: Math.Max(0, metadataAggMs),
            Cached: false,
            ComputedAt: now,
            AgeSeconds: 0,
            PhysicalScanIncluded: _storage is not null && includePhysicalScan);

        var response = new StorageStatsResponse(
            usersStats,
            foldersStats,
            filesStats,
            blobsStats,
            imagesStats,
            shareLinksStats,
            auditStats,
            cleanup,
            mediaStats,
            extractionStats,
            derivativeStats,
            userMetaStats,
            sensitiveStats,
            quotaStats,
            diagnostics,
            derivedReadiness,
            referenceIntegrity,
            derivativeDiagnostics);

        _cache?.Set(response, now, includePhysicalScan && _storage is not null);
        return response;
    }
}

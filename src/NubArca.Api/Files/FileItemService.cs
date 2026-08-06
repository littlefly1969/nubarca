using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Npgsql;
using SixLabors.ImageSharp;

namespace NubArca.Api.Files;

public sealed class FileItemService : IFileItemService
{
    private const int MaxNameLength = 255;
    private const int MaxMimeTypeLength = 255;
    private const string DefaultMimeType = "application/octet-stream";
    private const string SiblingNameUniqueIndex = "ux_file_items_active_sibling_name";
    private const string BlobMetadataUniqueIndex = "ux_blob_metadata_blob_object";

    private readonly AppDbContext _db;
    private readonly IBlobService _blobService;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IEmbeddedMetadataExtractor _embeddedExtractor;
    private readonly IImageMetadataStripper? _stripper;
    private readonly IImageMetadataWriter? _metadataWriter;
    private readonly IVideoSignatureDetector _videoDetector;
    private readonly IVideoMetadataExtractor _videoMetadataExtractor;
    private readonly NubArca.Api.MediaLibrary.IMediaLibraryService? _mediaLibrary;
    // deleted-content-import-skip: records a per-owner tombstone when a user
    // explicitly deletes the final active occurrence of exact content. Optional
    // for direct-construction test sites; null = no ledger writes.
    private readonly IDeletedContentTombstoneService? _tombstones;
    private readonly long _defaultUserQuotaBytes;
    private readonly TimeProvider _clock;

    public FileItemService(
        AppDbContext db,
        IBlobService blobService,
        IFileThumbnailService thumbnails,
        TimeProvider clock,
        IEmbeddedMetadataExtractor? embeddedExtractor = null,
        IImageMetadataStripper? stripper = null,
        IVideoSignatureDetector? videoDetector = null,
        Microsoft.Extensions.Options.IOptions<BlobStorageOptions>? storageOptions = null,
        IImageMetadataWriter? metadataWriter = null,
        // Slice 94: media-library eligibility (single source of truth) applied
        // to the gallery queries. Optional for direct-construction test sites;
        // null = no eligibility filter (legacy behaviour: everything included).
        NubArca.Api.MediaLibrary.IMediaLibraryService? mediaLibrary = null,
        // deleted-content-import-skip: optional; null = ledger writes are a
        // no-op (safe for tests that don't exercise tombstone recording).
        IDeletedContentTombstoneService? tombstones = null,
        // Video metadata probe (ffprobe). Optional for direct-construction test
        // sites; null defaults to the no-op extractor (provider disabled).
        IVideoMetadataExtractor? videoMetadataExtractor = null)
    {
        _db = db;
        _blobService = blobService;
        _thumbnails = thumbnails;
        _clock = clock;
        _mediaLibrary = mediaLibrary;
        _tombstones = tombstones;
        // Slice 65: per-user logical quota. Null options (legacy test call
        // sites) ⇒ 0 ⇒ unlimited, preserving pre-quota behaviour.
        _defaultUserQuotaBytes = storageOptions?.Value.DefaultUserQuotaBytes ?? 0;
        // The extractor is a stateless, dependency-free component. Defaulting
        // it keeps the many direct `new FileItemService(...)` test call sites
        // compiling unchanged; production injects the registered singleton.
        _embeddedExtractor = embeddedExtractor ?? new EmbeddedImageMetadataExtractor();
        // Slice 62: header-only video sniffer. Same defaulting pattern as the
        // extractor — stateless, dependency-free, safe to construct inline.
        _videoDetector = videoDetector ?? new VideoSignatureDetector();
        // Video metadata probe. Default no-op keeps direct-construction test
        // sites compiling; production injects the configured (ffprobe or no-op)
        // singleton per Media:VideoMetadataProvider.
        _videoMetadataExtractor = videoMetadataExtractor ?? new NoopVideoMetadataExtractor();
        // The stripper depends on `IOptions<ImageProcessingOptions>` and is
        // therefore not constructible without DI. Tests that don't exercise
        // StripEmbeddedMetadataAsync can omit it; the method itself throws
        // InvalidOperationException if it's called without one wired up.
        _stripper = stripper;
        // Slice 66: DateTaken writeback. Same optional-injection pattern as
        // the stripper; WriteDateTakenAsync throws if it's missing.
        _metadataWriter = metadataWriter;
    }

    public async Task<FileItem> CreateAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        string name,
        string? mimeType,
        Stream content,
        CancellationToken cancellationToken = default,
        FileCreateTimings? timings = null,
        bool generateSmallThumbnail = true,
        bool extractEmbeddedMetadata = true)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedName = ValidateAndTrimName(name);
        var normalizedMime = NormalizeMimeType(mimeType);

        if (parentFolderId is Guid parentId)
        {
            var parent = await _db.Folders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == parentId, cancellationToken);

            if (parent is null || parent.OwnerUserId != ownerUserId || parent.DeletedAt is not null)
            {
                throw new FolderNotFoundException(parentId);
            }
        }

        var siblingExists = await _db.FileItems
            .AsNoTracking()
            .AnyAsync(
                f => f.OwnerUserId == ownerUserId
                    && f.ParentFolderId == parentFolderId
                    && f.DeletedAt == null
                    && f.Name == validatedName,
                cancellationToken);

        if (siblingExists)
        {
            throw new DuplicateFileNameException(ownerUserId, parentFolderId, validatedName);
        }

        // Persist physical content + BlobObject row first. StoreAsync increments
        // BlobObject.ReferenceCount; if anything below fails before the FileItem
        // row commits, the catch blocks release that increment so the blob row
        // becomes eligible for BlobJanitor reclamation. The physical blob itself
        // may stay on disk until the janitor's grace window expires.
        BlobObject blob;
        if (timings is null)
        {
            blob = await _blobService.StoreAsync(content, cancellationToken);
        }
        else
        {
            var stored = await _blobService.StoreMeasuredAsync(content, cancellationToken);
            blob = stored.Blob;
            timings.ReadMillis += stored.Timings.ReadMillis;
            timings.HashMillis += stored.Timings.HashMillis;
            timings.WriteMillis += stored.Timings.WriteMillis;
            timings.BlobDbMillis += stored.Timings.BlobDbMillis;
        }

        FileItem? tracked = null;
        BlobMetadata? pendingMeta = null;
        try
        {
            // Minimal media detection required for gallery safety (ImageSharp
            // header identify + video signature sniff). Slice 95: timed as
            // DETECT so the Metadata phase measures FULL embedded extraction
            // only — 0 on the deferred import path by construction.
            var detectStart = timings is null ? 0L : Stopwatch.GetTimestamp();
            var facts = await TryDetectImageFactsAsync(blob.Id, cancellationToken);
            if (timings is not null)
            {
                timings.DetectMillis += (long)Stopwatch.GetElapsedTime(detectStart).TotalMilliseconds;
            }
            var width = facts.Width;
            var height = facts.Height;

            // ONE read decides dedup-vs-new metadata AND carries the facts the
            // effective-date seed + GPS projection need (slice 95: previously
            // a separate existence check plus a second facts read per file).
            var existingMeta = await _db.BlobMetadata
                .AsNoTracking()
                .Where(m => m.BlobObjectId == blob.Id)
                .Select(m => new { m.Id, m.DateTaken, m.GpsLatitude, m.GpsLongitude, m.GpsAltitude })
                .FirstOrDefaultAsync(cancellationToken);

            if (existingMeta is null)
            {
                // Blob-derived metadata is shared across every reference to
                // this content-addressed blob, so it is created exactly once —
                // the first time these bytes are ingested. Slice 94: bulk
                // callers (admin/staging import) pass
                // extractEmbeddedMetadata=false so only the cheap detection
                // facts are recorded and the row stays pending for the async
                // backfill. Slice 95: the row is BUILT here but persisted in
                // the SAME commit as the FileItem below (one fsync, not two).
                var metadataStart = timings is null ? 0L : Stopwatch.GetTimestamp();
                pendingMeta = await BuildBlobMetadataAsync(
                    blob, facts, normalizedMime, extractEmbeddedMetadata, cancellationToken);
                if (timings is not null)
                {
                    timings.MetadataMillis += (long)Stopwatch.GetElapsedTime(metadataStart).TotalMilliseconds;
                }
            }

            var createdAt = _clock.GetUtcNow().UtcDateTime;

            // Seed the denormalized effective capture date. A new file has no
            // user override yet, so it layers embedded blob DateTaken (just
            // extracted, or carried by an existing dedup row) over CreatedAt.
            var blobDateTaken = existingMeta?.DateTaken ?? pendingMeta?.DateTaken;
            var (effectiveDate, effectiveSource) =
                EffectiveDateTakenSources.Compute(null, blobDateTaken, createdAt);

            var file = new FileItem
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ParentFolderId = parentFolderId,
                BlobObjectId = blob.Id,
                Name = validatedName,
                MimeType = normalizedMime,
                SizeBytes = blob.SizeBytes,
                CreatedAt = createdAt,
                UpdatedAt = null,
                DeletedAt = null,
                Width = width,
                Height = height,
                EffectiveDateTaken = effectiveDate,
                EffectiveDateTakenSource = effectiveSource,
            };

            // Locked re-validation + insert. The parent + sibling pre-checks
            // above are best-effort fast-fails; the authoritative checks
            // happen here so a concurrent FolderService.SoftDeleteAsync on
            // the parent cannot slip between the check and the insert.
            // Slice 95: the new BlobMetadata row (when this upload created the
            // bytes) commits in the SAME transaction as the FileItem; a lost
            // race on its unique index (concurrent identical-bytes upload)
            // aborts the PostgreSQL transaction, so the loop adopts the
            // winner's row and retries once without our metadata.
            var fileItemStart = timings is null ? 0L : Stopwatch.GetTimestamp();
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                while (true)
                {
                    await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                    await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

                    // Slice 65: authoritative per-user quota check, INSIDE the
                    // per-owner advisory lock so two concurrent uploads cannot both
                    // observe "under quota" and jointly overshoot. Logical bytes =
                    // sum of this owner's FileItem.SizeBytes (active + trashed;
                    // purged rows are gone). The blob is already on disk at this
                    // point — the catch path releases its ReferenceCount so the
                    // BlobJanitor reclaims it after the grace window.
                    if (_defaultUserQuotaBytes > 0)
                    {
                        var usedBytes = await _db.FileItems
                            .Where(f => f.OwnerUserId == ownerUserId)
                            .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0L;
                        if (usedBytes + blob.SizeBytes > _defaultUserQuotaBytes)
                        {
                            throw new QuotaExceededException(
                                _defaultUserQuotaBytes, usedBytes, blob.SizeBytes);
                        }
                    }

                    if (parentFolderId is Guid pid)
                    {
                        var parentStillValid = await _db.Folders
                            .AsNoTracking()
                            .AnyAsync(
                                f => f.Id == pid
                                    && f.OwnerUserId == ownerUserId
                                    && f.DeletedAt == null,
                                cancellationToken);
                        if (!parentStillValid)
                        {
                            throw new FolderNotFoundException(pid);
                        }
                    }

                    if (pendingMeta is not null
                        && _db.Entry(pendingMeta).State == EntityState.Detached)
                    {
                        _db.BlobMetadata.Add(pendingMeta);
                    }
                    if (_db.Entry(file).State == EntityState.Detached)
                    {
                        _db.FileItems.Add(file);
                    }
                    tracked = file;
                    try
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                        await tx.CommitAsync(cancellationToken);
                        return;
                    }
                    catch (DbUpdateException ex)
                        when (pendingMeta is not null && IsBlobMetadataUniqueViolation(ex))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _db.Entry(pendingMeta).State = EntityState.Detached;
                        pendingMeta = null;
                        // Retry: the concurrent writer's metadata row serves us.
                    }
                }
            });
            var createdBlobMetadata = pendingMeta is not null;
            if (timings is not null)
            {
                timings.FileItemMillis += (long)Stopwatch.GetElapsedTime(fileItemStart).TotalMilliseconds;
            }

            // Image upload + dimensions detected => generate the small thumbnail.
            // Best-effort: thumbnail failures never break the upload. Runs
            // OUTSIDE the lock so a slow encode doesn't block other tree
            // mutations on the same owner. Slice 92: callers on a bulk path
            // (admin import) pass generateSmallThumbnail=false — the derivative
            // is produced by a background job or lazily on first request, and
            // the BlobMetadata ThumbnailStatus stays `pending`.
            if (facts.IsImage && generateSmallThumbnail)
            {
                var thumbnailStart = timings is null ? 0L : Stopwatch.GetTimestamp();
                var generated = await _thumbnails.TryGenerateSmallAsync(file.Id, blob.Id, cancellationToken);
                // The thumbnail outcome is a blob-derived fact. Record it on the
                // BlobMetadata row we just created; skip when a prior upload of
                // the same bytes already owns the row.
                if (createdBlobMetadata)
                {
                    await SetBlobThumbnailStatusAsync(
                        blob.Id,
                        generated ? MetadataStatuses.Generated : MetadataStatuses.Skipped,
                        cancellationToken);
                }
                if (timings is not null)
                {
                    timings.ThumbnailMillis += (long)Stopwatch.GetElapsedTime(thumbnailStart).TotalMilliseconds;
                }
            }

            // Slice 94: owner/file-scoped GPS projection (map preparation).
            // Seeded when the blob's metadata already carries coordinates
            // (inline extraction or dedup of an extracted blob); the async
            // metadata backfill populates it for deferred extractions.
            // Best-effort: a projection failure never breaks the upload.
            var gpsLatitude = existingMeta?.GpsLatitude ?? pendingMeta?.GpsLatitude;
            var gpsLongitude = existingMeta?.GpsLongitude ?? pendingMeta?.GpsLongitude;
            if (gpsLatitude is double lat && gpsLongitude is double lon)
            {
                try
                {
                    _db.FileItemLocations.Add(new FileItemLocation
                    {
                        FileItemId = file.Id,
                        OwnerUserId = ownerUserId,
                        Latitude = lat,
                        Longitude = lon,
                        Altitude = existingMeta?.GpsAltitude ?? pendingMeta?.GpsAltitude,
                        TakenAt = file.EffectiveDateTaken,
                        SourceBlobMetadataId = (existingMeta?.Id ?? pendingMeta?.Id)!.Value,
                        CreatedAt = createdAt,
                        UpdatedAt = createdAt,
                    });
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    _db.ChangeTracker.Clear();
                }
            }

            return file;
        }
        catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
        {
            DetachPendingMeta(pendingMeta);
            await DetachAndReleaseAsync(tracked, blob.Id);
            throw new DuplicateFileNameException(ownerUserId, parentFolderId, validatedName);
        }
        catch
        {
            // The unsaved metadata entity must never linger in the scoped
            // context (a later SaveChanges on this scope would insert it as
            // an orphan).
            DetachPendingMeta(pendingMeta);
            await DetachAndReleaseAsync(tracked, blob.Id);
            throw;
        }
    }

    private void DetachPendingMeta(BlobMetadata? pendingMeta)
    {
        if (pendingMeta is null) return;
        var entry = _db.Entry(pendingMeta);
        if (entry.State != EntityState.Detached)
        {
            entry.State = EntityState.Detached;
        }
    }

    // Detach the failed FileItem entity (if it was tracked) and release the
    // BlobObject.ReferenceCount that StoreAsync incremented. Release errors are
    // best-effort: the janitor will eventually mop up if this UPDATE fails.
    private async Task DetachAndReleaseAsync(FileItem? tracked, Guid blobObjectId)
    {
        if (tracked is not null)
        {
            var entry = _db.Entry(tracked);
            if (entry.State != EntityState.Detached)
            {
                entry.State = EntityState.Detached;
            }
        }

        try
        {
            await _blobService.ReleaseAsync(blobObjectId, CancellationToken.None);
        }
        catch
        {
            // best-effort
        }
    }

    public Task<FileItem?> GetByIdAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
    }

    public async Task<FileContent?> OpenContentAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);

        if (file is null)
        {
            return null;
        }

        // Server-detected content type for safe serving (slice 54.2). Null for
        // non-images / detection failures / pre-metadata-model blobs.
        var detectedContentType = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == file.BlobObjectId)
            .Select(m => m.DetectedContentType)
            .FirstOrDefaultAsync(cancellationToken);

        var stream = await _blobService.OpenContentAsync(file.BlobObjectId, cancellationToken);
        return new FileContent(stream, file.MimeType, file.SizeBytes, file.Name, detectedContentType);
    }

    public async Task<IReadOnlyList<FileSummary>> ListChildrenAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        return await _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null)
            .OrderBy(f => f.Name)
            .Select(f => new FileSummary(f.Id, f.Name, f.MimeType, f.SizeBytes, f.CreatedAt, f.Width, f.Height))
            .ToListAsync(cancellationToken);
    }

    // Files UI v2: one seek-paginated page of a folder's active files. Mirrors
    // the gallery's seek pagination (ListMediaRowsAsync) but scoped to a single
    // parent folder and without media filters. The endpoint has already
    // validated the cursor's sort/direction/scope before we get here.
    public async Task<DirectoryFilesPage> ListChildFilesPageAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        DirectorySortField sort,
        DirectorySortDirection direction,
        int limit,
        DirectoryCursor? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null);

        if (cursor is not null)
        {
            query = ApplyDirectoryCursorSeek(query, cursor);
        }

        query = ApplyDirectoryOrdering(query, sort, direction);

        // Fetch limit + 1 to detect another page without a COUNT(*).
        var fetched = await query
            .Take(limit + 1)
            .Select(f => new FileSummary(f.Id, f.Name, f.MimeType, f.SizeBytes, f.CreatedAt, f.Width, f.Height))
            .ToListAsync(cancellationToken);

        var hasMore = fetched.Count > limit;
        var pageRows = hasMore ? fetched.Take(limit).ToList() : fetched;

        string? nextCursor = null;
        if (hasMore && pageRows.Count > 0)
        {
            var last = pageRows[^1];
            var scope = DirectoryCursor.ScopeFor(parentFolderId);
            var next = sort switch
            {
                DirectorySortField.Name => DirectoryCursor.FromString(sort, direction, last.Name, last.Id, scope),
                DirectorySortField.Type => DirectoryCursor.FromString(sort, direction, last.MimeType, last.Id, scope),
                DirectorySortField.Size => DirectoryCursor.FromNumber(sort, direction, last.SizeBytes, last.Id, scope),
                _ => DirectoryCursor.FromDate(sort, direction, last.CreatedAt, last.Id, scope),
            };
            nextCursor = next.Encode();
        }

        return new DirectoryFilesPage(pageRows, nextCursor, hasMore);
    }

    private static IQueryable<FileItem> ApplyDirectoryOrdering(
        IQueryable<FileItem> query, DirectorySortField sort, DirectorySortDirection direction)
    {
        var asc = direction == DirectorySortDirection.Asc;
        return (sort, asc) switch
        {
            (DirectorySortField.Created, true) => query.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            (DirectorySortField.Created, false) => query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id),
            (DirectorySortField.Size, true) => query.OrderBy(f => f.SizeBytes).ThenBy(f => f.Id),
            (DirectorySortField.Size, false) => query.OrderByDescending(f => f.SizeBytes).ThenByDescending(f => f.Id),
            (DirectorySortField.Type, true) => query.OrderBy(f => f.MimeType).ThenBy(f => f.Id),
            (DirectorySortField.Type, false) => query.OrderByDescending(f => f.MimeType).ThenByDescending(f => f.Id),
            (DirectorySortField.Name, false) => query.OrderByDescending(f => f.Name).ThenByDescending(f => f.Id),
            _ => query.OrderBy(f => f.Name).ThenBy(f => f.Id),
        };
    }

    // Keyset predicate that resumes the listing after the cursor's boundary row,
    // tie-broken on Id (mirrors ApplyCursorSeek for the gallery).
    private static IQueryable<FileItem> ApplyDirectoryCursorSeek(
        IQueryable<FileItem> query, DirectoryCursor cursor)
    {
        var asc = cursor.Direction == DirectorySortDirection.Asc;
        var cursorId = cursor.Id;

        return cursor.Sort switch
        {
            DirectorySortField.Name when cursor.PrimaryString is string sName => asc
                ? query.Where(f => string.Compare(f.Name, sName) > 0
                    || (f.Name == sName && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => string.Compare(f.Name, sName) < 0
                    || (f.Name == sName && f.Id.CompareTo(cursorId) < 0)),
            DirectorySortField.Type when cursor.PrimaryString is string sType => asc
                ? query.Where(f => string.Compare(f.MimeType, sType) > 0
                    || (f.MimeType == sType && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => string.Compare(f.MimeType, sType) < 0
                    || (f.MimeType == sType && f.Id.CompareTo(cursorId) < 0)),
            DirectorySortField.Size when cursor.PrimaryNumber is long n => asc
                ? query.Where(f => f.SizeBytes > n
                    || (f.SizeBytes == n && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => f.SizeBytes < n
                    || (f.SizeBytes == n && f.Id.CompareTo(cursorId) < 0)),
            DirectorySortField.Created when cursor.PrimaryDate is DateTime d => asc
                ? query.Where(f => f.CreatedAt > d
                    || (f.CreatedAt == d && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => f.CreatedAt < d
                    || (f.CreatedAt == d && f.Id.CompareTo(cursorId) < 0)),
            _ => query,
        };
    }

    public async Task<IReadOnlyList<FileTrashSummary>> ListTrashAsync(
        Guid ownerUserId,
        Guid? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt != null);

        if (parentFolderId is Guid parentId)
        {
            query = query.Where(f => f.ParentFolderId == parentId);
        }

        return await query
            .OrderByDescending(f => f.DeletedAt)
            .ThenBy(f => f.Name)
            .Select(f => new FileTrashSummary(
                f.Id, f.Name, f.MimeType, f.SizeBytes,
                f.ParentFolderId, f.CreatedAt, f.UpdatedAt, f.DeletedAt!.Value,
                f.Width, f.Height))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FileSummary>> SearchAsync(
        Guid ownerUserId,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        // Lower-case both sides so the comparison is case-insensitive on both
        // PostgreSQL (LIKE is case-sensitive by default) and SQLite (LIKE is
        // ASCII-case-insensitive only). EF Core 5+ escapes LIKE wildcards in
        // the user-supplied operand automatically.
        var needle = query.Trim().ToLowerInvariant();

        return await _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                && (f.Name.ToLower().Contains(needle) || f.MimeType.ToLower().Contains(needle)))
            .OrderBy(f => f.Name)
            .Select(f => new FileSummary(f.Id, f.Name, f.MimeType, f.SizeBytes, f.CreatedAt, f.Width, f.Height))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImageItem>> ListImagesAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        int limit,
        int offset,
        string? nameQuery = null,
        ImageSortField sort = ImageSortField.Created,
        ImageSortDirection direction = ImageSortDirection.Desc,
        CancellationToken cancellationToken = default)
    {
        // Gallery membership is based on SERVER-DETECTED image content, not the
        // untrusted client MIME (slice 54.2). A blob is a gallery image when
        // its BlobMetadata carries a detected "image/*" content type (set only
        // when ImageSharp actually recognized the bytes). Blobs uploaded before
        // the metadata model existed have no BlobMetadata row, so for those we
        // fall back to the client MIME prefix — there is no server detection to
        // consult. A spoofed/corrupt file claiming image/* gets a metadata row
        // with a null DetectedContentType and is therefore excluded.
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                && (_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && m.DetectedContentType.StartsWith("image/"))
                    || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && f.MimeType.StartsWith("image/"))));

        // Slice 94: media-library eligibility (folder exclusion rules). Same
        // single-source-of-truth filter as the cursor-mode gallery.
        if (_mediaLibrary is not null)
        {
            query = _mediaLibrary.ApplyMediaLibraryVisibility(
                query, NubArca.Api.MediaLibrary.MediaKind.Photo);
        }

        // Slice 3: the legacy offset gallery path only ever shows the ACTIVE
        // library (there is no offset "Esclusi" tab); excluded files must never
        // leak here.
        query = NubArca.Api.MediaLibrary.MediaLibraryScopePolicy.ApplyScope(
            query, NubArca.Api.MediaLibrary.MediaLibraryScope.Active);

        if (parentFolderId is Guid parentId)
        {
            query = query.Where(f => f.ParentFolderId == parentId);
        }

        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            // Lower-case both sides so the comparison is case-insensitive on
            // both PostgreSQL (LIKE is case-sensitive by default) and SQLite
            // (LIKE is ASCII-case-insensitive only). EF Core 5+ escapes LIKE
            // wildcards in the user-supplied operand automatically. Matches
            // the convention from SearchAsync.
            var needle = nameQuery.Trim().ToLowerInvariant();
            query = query.Where(f => f.Name.ToLower().Contains(needle));
        }

        // Primary sort + Id tiebreaker. Both sides of the tiebreaker direction
        // intentionally match the primary direction so the "last item of
        // page N" and "first item of page N+1" never collide on identical
        // primary values.
        var ordered = ApplyOrdering(query, sort, direction);

        // The owner title comes from ONE correlated subquery inside the same
        // statement (a left-join-shaped scalar subquery), never a per-card
        // round-trip. DisplayName is resolved in memory so the fallback rule
        // lives in exactly one place (MediaDisplayName).
        var rows = await ordered
            .Skip(offset)
            .Take(limit)
            .Select(f => new
            {
                f.Id,
                f.Name,
                Title = _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => u.Title)
                    .FirstOrDefault(),
                f.MimeType,
                f.SizeBytes,
                f.Width,
                f.Height,
                BlobWidth = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Width)
                    .FirstOrDefault(),
                BlobHeight = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Height)
                    .FirstOrDefault(),
                Orientation = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Orientation)
                    .FirstOrDefault(),
                f.CreatedAt,
                f.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r =>
            {
                var (width, height) = ImageDisplayDimensions.Resolve(
                    r.Width ?? r.BlobWidth, r.Height ?? r.BlobHeight, r.Orientation);
                return new ImageItem(
                    r.Id,
                    r.Name,
                    r.Title,
                    MediaDisplayName.Resolve(r.Title, r.Name),
                    r.MimeType,
                    r.SizeBytes,
                    width,
                    height,
                    r.CreatedAt,
                    r.UpdatedAt,
                    $"/api/files/{r.Id}/thumbnail?size=small");
            })
            .ToList();
    }

    // All sort keys (created / name / size / datetaken) are plain FileItem
    // columns backed by a matching composite index, so ordering + seek
    // pagination resolve via an index range scan with no Sort step. DateTaken
    // uses the denormalized EffectiveDateTaken column (slice 88).
    private IOrderedQueryable<FileItem> ApplyOrdering(
        IQueryable<FileItem> query,
        ImageSortField sort,
        ImageSortDirection direction)
    {
        return (sort, direction) switch
        {
            (ImageSortField.Created, ImageSortDirection.Asc)
                => query.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            (ImageSortField.Created, ImageSortDirection.Desc)
                => query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id),
            // sort=name means "order the way the gallery reads": by the
            // EFFECTIVE display name (owner title, else filename), lower-cased
            // so the order is case-insensitive and identical on PostgreSQL and
            // SQLite. This is the one sort key that is not a plain FileItem
            // column: it resolves through a correlated subquery on
            // FileItemUserMetadata, so PostgreSQL adds a Sort step instead of
            // walking the name index. Accepted deliberately — a name-sorted
            // gallery that ignored titles would contradict what is on screen.
            (ImageSortField.Name, ImageSortDirection.Asc)
                => query.OrderBy(DisplaySortKeyExpression()).ThenBy(f => f.Id),
            (ImageSortField.Name, ImageSortDirection.Desc)
                => query.OrderByDescending(DisplaySortKeyExpression()).ThenByDescending(f => f.Id),
            (ImageSortField.Size, ImageSortDirection.Asc)
                => query.OrderBy(f => f.SizeBytes).ThenBy(f => f.Id),
            (ImageSortField.Size, ImageSortDirection.Desc)
                => query.OrderByDescending(f => f.SizeBytes).ThenByDescending(f => f.Id),
            // Effective capture date precedence (slice 55 + 56):
            //   user DateTakenOverride → embedded BlobMetadata.DateTaken → upload time.
            // Slice 88: this precedence is denormalized onto FileItem.
            // EffectiveDateTaken (kept in sync by the write paths), so the sort
            // resolves via the ix_file_items_owner_deleted_effdate_id index range
            // scan instead of correlated subqueries + a Sort step.
            (ImageSortField.DateTaken, ImageSortDirection.Asc)
                => query
                    .OrderBy(f => f.EffectiveDateTaken)
                    .ThenBy(f => f.Id),
            (ImageSortField.DateTaken, ImageSortDirection.Desc)
                => query
                    .OrderByDescending(f => f.EffectiveDateTaken)
                    .ThenByDescending(f => f.Id),
            _ => query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id),
        };
    }

    // SQL form of MediaDisplayName.SortKey: lower(COALESCE(user title, name)).
    // Handed to OrderBy/ThenBy as an expression tree so the fallback rule is
    // written once. FileItemUserMetadata is keyed by FileItemId and FileItems
    // are already owner-scoped by the caller, so no other user's title can be
    // read here.
    private System.Linq.Expressions.Expression<Func<FileItem, string>> DisplaySortKeyExpression()
        => f => (_db.FileItemUserMetadata
                .Where(u => u.FileItemId == f.Id)
                .Select(u => u.Title)
                .FirstOrDefault() ?? f.Name)
            .ToLower();

    public async Task<ImagePage> ListImagesPageAsync(
        Guid ownerUserId,
        int limit,
        ImageCursor? cursor,
        ImageFilters filters,
        ImageSortField sort = ImageSortField.Created,
        ImageSortDirection direction = ImageSortDirection.Desc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var (rows, nextCursor, hasMore, totalCount) = await ListMediaRowsAsync(
            ownerUserId, limit, cursor, filters, sort, direction, MediaKindScope.Image, cancellationToken);

        var items = rows
            .Select(r =>
            {
                var (width, height) = ImageDisplayDimensions.Resolve(
                    r.Width ?? r.BlobWidth,
                    r.Height ?? r.BlobHeight,
                    r.Orientation);
                return new ImageItem(
                    r.Id, r.Name, r.Title, MediaDisplayName.Resolve(r.Title, r.Name),
                    r.MimeType, r.SizeBytes, width, height,
                    r.CreatedAt, r.UpdatedAt,
                    $"/api/files/{r.Id}/thumbnail?size=small",
                    r.OccurrenceCount);
            })
            .ToList();

        return new ImagePage(items, nextCursor, hasMore, totalCount);
    }

    public async Task<IReadOnlyList<ImageItem>> ListGalleryImagesByRankAsync(
        Guid ownerUserId,
        IReadOnlyList<Guid> rankedFileItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rankedFileItemIds);
        if (rankedFileItemIds.Count == 0) return Array.Empty<ImageItem>();

        var ids = rankedFileItemIds.Distinct().Take(100).ToList();
        var fetched = await BuildGalleryQuery(ownerUserId, new ImageFilters(), MediaKindScope.Image)
            .Where(f => ids.Contains(f.Id))
            .Select(f => new
            {
                f.Id,
                f.Name,
                Title = _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => u.Title)
                    .FirstOrDefault(),
                f.MimeType,
                f.SizeBytes,
                f.Width,
                f.Height,
                BlobWidth = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Width)
                    .FirstOrDefault(),
                BlobHeight = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Height)
                    .FirstOrDefault(),
                Orientation = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Orientation)
                    .FirstOrDefault(),
                f.CreatedAt,
                f.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var rows = fetched
            .Select(r =>
            {
                var (width, height) = ImageDisplayDimensions.Resolve(
                    r.Width ?? r.BlobWidth, r.Height ?? r.BlobHeight, r.Orientation);
                return new ImageItem(
                    r.Id, r.Name, r.Title, MediaDisplayName.Resolve(r.Title, r.Name),
                    r.MimeType, r.SizeBytes, width, height,
                    r.CreatedAt, r.UpdatedAt,
                    $"/api/files/{r.Id}/thumbnail?size=small");
            })
            .ToList();

        var byId = rows.ToDictionary(x => x.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // Physical-filter-first candidate set for semantic ranking. Applies the
    // PHYSICAL projection of the gallery filters (people / favourites / rating /
    // GPS / dates / duplicate-collapse / metadata q) through the SAME shared
    // BuildGalleryQuery, then applies the semantic-only small-image quality gate.
    // The result is the owner-scoped, Vault-excluded, media-library-visible set
    // the manual gallery would show, minus the semantic residual and technical
    // images below SemanticPhotoCandidatePolicy.MinEdgePixels. The normal gallery
    // itself is unchanged. The semantic ranker then ranks INSIDE this set only.
    //
    // Returns up to `cap` (Id, BlobObjectId) pairs ordered by Id (deterministic
    // when the cap truncates). The caller discloses truncation via a warning; the
    // cap is never applied silently. Returns the FileItem id (the collapse-aware
    // representative when collapsing is on) and its blob id (for vector lookup).
    // SEARCH-SEM-01: `afterId` makes this KEYSET-pageable. The projection was
    // always ordered by Id, so `Id > afterId` walks the eligible set in bounded
    // batches with no offset drift and no unbounded materialisation. Omitting
    // it reproduces the original single-batch behaviour exactly, so every
    // pre-existing caller is unaffected.
    public async Task<IReadOnlyList<GalleryCandidateRef>> ListPhysicalGalleryCandidatesAsync(
        Guid ownerUserId,
        ImageFilters filters,
        int cap,
        CancellationToken cancellationToken = default,
        Guid? afterId = null)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var physical = filters.WithoutSemantic();
        var boundedCap = Math.Max(1, cap);

        var semanticCandidates = SemanticPhotoCandidatePolicy.Apply(
            BuildGalleryQuery(ownerUserId, physical, MediaKindScope.Image), _db);
        if (afterId is Guid photoAfter)
        {
            semanticCandidates = semanticCandidates.Where(f => f.Id.CompareTo(photoAfter) > 0);
        }
        var rows = await semanticCandidates
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.BlobObjectId })
            .Take(boundedCap + 1)
            .ToListAsync(cancellationToken);

        return rows
            .Take(boundedCap)
            .Select(r => new GalleryCandidateRef(r.Id, r.BlobObjectId))
            .ToList();
    }

    // Total number of physically filtered candidates that ALSO have an embedding
    // for the given profile (the true denominator base before Top-K reduction).
    // Kept as a bounded COUNT over the owner-scoped projection joined to the
    // canonical embeddings — never materialises ids into memory.
    public Task<int> CountEmbeddedGalleryCandidatesAsync(
        Guid ownerUserId,
        ImageFilters filters,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var physical = filters.WithoutSemantic();
        var semanticCandidates = SemanticPhotoCandidatePolicy.Apply(
            BuildGalleryQuery(ownerUserId, physical, MediaKindScope.Image), _db);
        var query =
            from f in semanticCandidates
            where _db.BlobEmbeddings.Any(e => e.BlobObjectId == f.BlobObjectId && e.ProfileId == profileId)
            select f.Id;
        return query.CountAsync(cancellationToken);
    }

    public Task<bool> IsGalleryImageAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
        // Same membership rule as the unfiltered image gallery: owner-scoped,
        // active, server-detected image, media-library visible (and the global
        // Private-Vault filter on FileItems).
        => BuildGalleryQuery(ownerUserId, new ImageFilters(), MediaKindScope.Image)
            .AnyAsync(f => f.Id == fileItemId, cancellationToken);

    public Task<bool> IsGalleryVideoAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
        => BuildGalleryQuery(ownerUserId, new ImageFilters(), MediaKindScope.Video)
            .AnyAsync(f => f.Id == fileItemId, cancellationToken);

    // Slice 86: video gallery. Same cursor/sort/filter machinery as the image
    // gallery, but membership is server-detected `video/*` and each item
    // carries the existing poster URL instead of a thumbnail URL.
    public async Task<VideoPage> ListVideosPageAsync(
        Guid ownerUserId,
        int limit,
        ImageCursor? cursor,
        ImageFilters filters,
        ImageSortField sort = ImageSortField.Created,
        ImageSortDirection direction = ImageSortDirection.Desc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var (rows, nextCursor, hasMore, totalCount) = await ListMediaRowsAsync(
            ownerUserId, limit, cursor, filters, sort, direction, MediaKindScope.Video, cancellationToken);

        // Slice 95: one bounded lookup per PAGE (not per row) for poster
        // provenance, so placeholder posters can be marked in the UI.
        // Pre-provenance rows surface as "unknown".
        var pageIds = rows.Select(r => r.Id).ToList();
        var posterSources = await _db.FileThumbnails.AsNoTracking()
            .Where(t => pageIds.Contains(t.FileItemId) && t.Size == ThumbnailSizes.Poster)
            .Select(t => new { t.FileItemId, t.PosterSource })
            .ToDictionaryAsync(
                t => t.FileItemId,
                t => t.PosterSource ?? VideoPosterSources.Unknown,
                cancellationToken);

        // One batched lookup per PAGE (not per row) for the ffprobe-derived video
        // metadata, joining each page FileItem to its blob's BlobMetadata. Keyed
        // by FileItemId. Fields are null when the blob has not been probed.
        var videoMeta = await (
            from f in _db.FileItems.AsNoTracking()
            where pageIds.Contains(f.Id)
            join m in _db.BlobMetadata.AsNoTracking() on f.BlobObjectId equals m.BlobObjectId
            select new
            {
                f.Id,
                m.DurationSeconds,
                m.Width,
                m.Height,
                m.VideoCodec,
                m.AudioCodec,
                m.HasAudio,
                m.FrameRate,
                m.Rotation,
            })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var items = rows
            .Select(r =>
            {
                var vm = videoMeta.GetValueOrDefault(r.Id);
                var (width, height) = VideoDisplayDimensions.Resolve(
                    vm?.Width ?? r.Width, vm?.Height ?? r.Height, vm?.Rotation);
                return new VideoItem(
                    r.Id, r.Name, r.Title, MediaDisplayName.Resolve(r.Title, r.Name),
                    r.MimeType, r.SizeBytes,
                    // Same DISPLAY dimensions as the unified Library/Album
                    // projection and TV Party: apply the probe rotation to coded
                    // pixels so the tile matches the autorotated poster.
                    width,
                    height,
                    r.CreatedAt, r.UpdatedAt,
                    $"/api/files/{r.Id}/poster",
                    DurationSeconds: vm?.DurationSeconds,
                    r.OccurrenceCount,
                    PosterSource: posterSources.GetValueOrDefault(r.Id),
                    VideoCodec: vm?.VideoCodec,
                    AudioCodec: vm?.AudioCodec,
                    HasAudio: vm?.HasAudio ?? false,
                    FrameRate: vm?.FrameRate,
                    PreviewStripUrl: $"/api/files/{r.Id}/video-preview-strip");
            })
            .ToList();

        return new VideoPage(items, nextCursor, hasMore, totalCount);
    }

    // Slice 5: unified media page for the "Tutti | Foto | Video" workspace. Uses
    // the SAME cursor/sort/filter/scope machinery as the photo and video
    // galleries (ListMediaRowsAsync), so a single-kind request is byte-for-byte
    // equivalent to the legacy surfaces; `All` returns a mixed, server-ordered
    // stream. Projection is page-level batched (one lookup each for user
    // metadata, GPS presence, video probe, poster provenance) — never per row.
    public async Task<MediaPage> ListMediaPageAsync(
        Guid ownerUserId,
        int limit,
        ImageCursor? cursor,
        ImageFilters filters,
        MediaKindScope kind,
        ImageSortField sort = ImageSortField.Created,
        ImageSortDirection direction = ImageSortDirection.Desc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var (rows, nextCursor, hasMore, totalCount) = await ListMediaRowsAsync(
            ownerUserId, limit, cursor, filters, sort, direction, kind, cancellationToken,
            cursorFingerprintOverride: kind.MediaCursorFingerprint(filters),
            computeTotalCount: cursor is null);

        // Per-kind totals for the tab counts. Like the total above, these are a
        // query-identity property, so they are computed only on the first page;
        // paged requests return -1 and the client keeps the first page's counts,
        // avoiding a second global COUNT (the mixed request's video count) on
        // every load-more.
        int photoCount, videoCount;
        if (cursor is not null)
        {
            photoCount = -1;
            videoCount = -1;
        }
        else if (kind == MediaKindScope.Image)
        {
            photoCount = totalCount;
            videoCount = 0;
        }
        else if (kind == MediaKindScope.Video)
        {
            photoCount = 0;
            videoCount = totalCount;
        }
        else
        {
            videoCount = await BuildGalleryQuery(ownerUserId, filters, MediaKindScope.All)
                .CountAsync(IsVideoPredicate, cancellationToken);
            photoCount = totalCount - videoCount;
        }

        var items = await ProjectMediaItemsAsync(rows, cancellationToken);

        return new MediaPage(items, nextCursor, hasMore, totalCount, photoCount, videoCount);
    }

    // Page-level batched MediaItem projection shared by the unified media page
    // and the semantic-by-rank hydration, so display-dimension/rotation and
    // poster/duration rules exist exactly once.
    private async Task<List<MediaItem>> ProjectMediaItemsAsync(
        IReadOnlyList<GalleryRow> rows, CancellationToken cancellationToken)
    {
        var pageIds = rows.Select(r => r.Id).ToList();

        // Owner-scoped user metadata (favorite / rating) for the page.
        var userMeta = await _db.FileItemUserMetadata.AsNoTracking()
            .Where(u => pageIds.Contains(u.FileItemId))
            .Select(u => new { u.FileItemId, u.IsFavorite, u.Rating })
            .ToDictionaryAsync(u => u.FileItemId, cancellationToken);

        // GPS presence (never coordinates) for the page's image items.
        var gpsIds = (await (
            from f in _db.FileItems.AsNoTracking()
            where pageIds.Contains(f.Id)
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.GpsLatitude != null && m.GpsLongitude != null)
            select f.Id).ToListAsync(cancellationToken)).ToHashSet();

        // Blob-derived metadata for the page (one row per item with a blob):
        // ffprobe video fields plus the pixel dims + orientation used to expose
        // DISPLAY dimensions for both kinds.
        var blobMeta = await (
            from f in _db.FileItems.AsNoTracking()
            where pageIds.Contains(f.Id)
            join m in _db.BlobMetadata.AsNoTracking() on f.BlobObjectId equals m.BlobObjectId
            select new
            {
                f.Id,
                m.DurationSeconds,
                m.Width,
                m.Height,
                m.VideoCodec,
                m.HasAudio,
                m.Rotation,
                m.Orientation,
            })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var posterSources = await _db.FileThumbnails.AsNoTracking()
            .Where(t => pageIds.Contains(t.FileItemId) && t.Size == ThumbnailSizes.Poster)
            .Select(t => new { t.FileItemId, t.PosterSource })
            .ToDictionaryAsync(
                t => t.FileItemId,
                t => t.PosterSource ?? VideoPosterSources.Unknown,
                cancellationToken);

        var items = rows.Select(r =>
        {
            var meta = userMeta.GetValueOrDefault(r.Id);
            var favorite = meta?.IsFavorite ?? false;
            var rating = meta is { Rating: > 0 } ? meta.Rating : (int?)null;
            var display = MediaDisplayName.Resolve(r.Title, r.Name);
            if (r.IsVideo)
            {
                var vm = blobMeta.GetValueOrDefault(r.Id);
                // Expose the DISPLAY dimensions (rotation applied) so the media
                // wall's tile shape matches the autorotated poster — otherwise a
                // rotated portrait clip would get a landscape tile.
                var (videoWidth, videoHeight) = VideoDisplayDimensions.Resolve(
                    vm?.Width ?? r.Width, vm?.Height ?? r.Height, vm?.Rotation);
                return new MediaItem(
                    r.Id, "video", r.Name, r.Title, display, r.MimeType, r.SizeBytes,
                    videoWidth, videoHeight,
                    r.CreatedAt, r.UpdatedAt, r.EffectiveDateTaken, favorite, rating,
                    $"/api/files/{r.Id}/poster", r.OccurrenceCount,
                    HasGps: null,
                    PosterUrl: $"/api/files/{r.Id}/poster",
                    DurationSeconds: vm?.DurationSeconds,
                    VideoCodec: vm?.VideoCodec,
                    HasAudio: vm?.HasAudio ?? false,
                    PosterSource: posterSources.GetValueOrDefault(r.Id),
                    PreviewStripUrl: $"/api/files/{r.Id}/video-preview-strip");
            }

            var im = blobMeta.GetValueOrDefault(r.Id);
            // Detected dims are CODED pixels (Image.Identify ignores EXIF
            // orientation) while the thumbnail is auto-oriented, so expose the
            // DISPLAY dims (swapped for EXIF 5/6/7/8) — otherwise a rotated
            // portrait photo would get a landscape tile and letterbox.
            var (imageWidth, imageHeight) = ImageDisplayDimensions.Resolve(
                r.Width ?? im?.Width, r.Height ?? im?.Height, im?.Orientation);
            return new MediaItem(
                r.Id, "image", r.Name, r.Title, display, r.MimeType, r.SizeBytes,
                imageWidth, imageHeight, r.CreatedAt, r.UpdatedAt, r.EffectiveDateTaken,
                favorite, rating,
                $"/api/files/{r.Id}/thumbnail?size=small", r.OccurrenceCount,
                HasGps: gpsIds.Contains(r.Id));
        }).ToList();

        return items;
    }

    // VSEM-03: hydrate an already-ranked owner-private MIXED media set
    // (unified semantic retrieval) into normal MediaItem DTOs while preserving
    // the supplied relevance order. Same membership gate as the unfiltered
    // media workspace; an id that the gallery would never list is silently
    // dropped, never an error and never a leak.
    public async Task<IReadOnlyList<MediaItem>> ListGalleryMediaByRankAsync(
        Guid ownerUserId,
        IReadOnlyList<Guid> rankedFileItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rankedFileItemIds);
        if (rankedFileItemIds.Count == 0) return Array.Empty<MediaItem>();

        var ids = rankedFileItemIds.Distinct().Take(100).ToList();
        var rows = await BuildGalleryQuery(ownerUserId, new ImageFilters(), MediaKindScope.All)
            .Where(f => ids.Contains(f.Id))
            .Select(f => new GalleryRow
            {
                Id = f.Id,
                Name = f.Name,
                Title = _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => u.Title)
                    .FirstOrDefault(),
                MimeType = f.MimeType,
                SizeBytes = f.SizeBytes,
                Width = f.Width,
                Height = f.Height,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                EffectiveDateTaken = f.EffectiveDateTaken,
                IsVideo = _db.BlobMetadata
                        .Where(m => m.BlobObjectId == f.BlobObjectId)
                        .Select(m => (string?)m.DetectedContentType)
                        .FirstOrDefault() != null
                    ? _db.BlobMetadata
                        .Where(m => m.BlobObjectId == f.BlobObjectId)
                        .Select(m => m.DetectedContentType!)
                        .First()
                        .StartsWith("video/")
                    : f.MimeType.StartsWith("video/"),
                OccurrenceCount = 1,
            })
            .ToListAsync(cancellationToken);

        var items = await ProjectMediaItemsAsync(rows, cancellationToken);
        var byId = items.ToDictionary(x => x.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // VSEM-03: physical-filter-first VIDEO candidate set for semantic ranking —
    // the video counterpart of ListPhysicalGalleryCandidatesAsync, through the
    // SAME shared BuildGalleryQuery (owner-scoped, Vault-excluded via the
    // global filter, media-library-visible, server-detected video). No photo
    // quality gate applies. Returns up to `cap` (Id, BlobObjectId) pairs
    // ordered by Id.
    // SEARCH-SEM-01: keyset-pageable via `afterId`, same contract as the photo
    // candidate projection above.
    public async Task<IReadOnlyList<GalleryCandidateRef>> ListPhysicalVideoCandidatesAsync(
        Guid ownerUserId,
        ImageFilters filters,
        int cap,
        CancellationToken cancellationToken = default,
        Guid? afterId = null)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var physical = filters.WithoutSemantic();
        var boundedCap = Math.Max(1, cap);

        var videoCandidates = BuildGalleryQuery(ownerUserId, physical, MediaKindScope.Video);
        if (afterId is Guid videoAfter)
        {
            videoCandidates = videoCandidates.Where(f => f.Id.CompareTo(videoAfter) > 0);
        }
        var rows = await videoCandidates
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.BlobObjectId })
            .Take(boundedCap + 1)
            .ToListAsync(cancellationToken);

        return rows
            .Take(boundedCap)
            .Select(r => new GalleryCandidateRef(r.Id, r.BlobObjectId))
            .ToList();
    }

    // SQL-translatable "this FileItem is a video" predicate, by the same
    // server-detected-then-client-MIME rule as gallery membership. Used for the
    // mixed-kind video tab-count.
    private System.Linq.Expressions.Expression<Func<FileItem, bool>> IsVideoPredicate =>
        f => (_db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => (string?)m.DetectedContentType)
                    .FirstOrDefault() != null)
            ? _db.BlobMetadata
                .Where(m => m.BlobObjectId == f.BlobObjectId)
                .Select(m => m.DetectedContentType!)
                .First()
                .StartsWith("video/")
            : f.MimeType.StartsWith("video/");

    // Distinct video codecs across the owner's active videos (video gallery
    // codec-filter facet). Owner-scoped via the global FileItems query filter
    // (Private Vault excluded); returns lower-cased codec short names sorted.
    public async Task<IReadOnlyList<string>> ListVideoCodecsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await (
            from f in _db.FileItems.AsNoTracking()
            where f.OwnerUserId == ownerUserId && f.DeletedAt == null
            join m in _db.BlobMetadata.AsNoTracking() on f.BlobObjectId equals m.BlobObjectId
            where m.MediaCategory == MediaCategories.Video && m.VideoCodec != null
            select m.VideoCodec!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
    }

    // Shared seek-paginated row fetch for the image + video galleries. Returns
    // the page rows (cursor materialisation source) plus the next cursor.
    private async Task<(IReadOnlyList<GalleryRow> Rows, string? NextCursor, bool HasMore, int TotalCount)> ListMediaRowsAsync(
        Guid ownerUserId,
        int limit,
        ImageCursor? cursor,
        ImageFilters filters,
        ImageSortField sort,
        ImageSortDirection direction,
        MediaKindScope kind,
        CancellationToken cancellationToken,
        // When set, this replaces filters.Fingerprint() for BINDING the next
        // cursor. The unified media surface passes a kind-salted fingerprint so a
        // cursor cannot cross kinds; the legacy image/video paths pass null and
        // keep their existing (filters-only) cursor identity.
        string? cursorFingerprintOverride = null,
        // When false, the global COUNT is skipped and TotalCount is -1. The
        // unified media workspace sets this to (cursor == null) so the total is
        // computed once per query, not on every load-more; the legacy image/video
        // callers keep the default (always count) so their contract is unchanged.
        bool computeTotalCount = true)
    {
        var query = BuildGalleryQuery(ownerUserId, filters, kind);
        var cursorFingerprint = cursorFingerprintOverride ?? filters.Fingerprint();

        // Server-authoritative total for the CURRENT filter set: the SAME
        // filtered projection, with only the cursor seek / ordering / paging
        // removed. Duplicate collapsing lives inside BuildGalleryQuery, so this
        // COUNT already reflects the collapsed (visible) row count, not the raw
        // FileItem count. It is identical across the pages of a query (the filter
        // set is the query identity), so the unified media workspace asks for it
        // only on the first page (computeTotalCount) — recomputing this
        // bounded-but-non-trivial COUNT on every load-more added 160-320 ms per
        // page for no new information. When skipped, -1 signals "unchanged" and
        // the client keeps the first page's total.
        var totalCount = computeTotalCount ? await query.CountAsync(cancellationToken) : -1;

        var seek = cursor is not null ? ApplyCursorSeek(query, cursor) : query;

        var ordered = ApplyOrdering(seek, sort, direction);

        // Fetch limit + 1 to detect another page without a COUNT(*).
        var fetched = await ordered
            .Take(limit + 1)
            .Select(f => new GalleryRow
            {
                Id = f.Id,
                Name = f.Name,
                // One correlated scalar subquery per statement (NOT per card):
                // the owner's title for this FileItem, or null. Used for the
                // DTO's DisplayName and for the sort=name cursor boundary.
                Title = _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => u.Title)
                    .FirstOrDefault(),
                MimeType = f.MimeType,
                SizeBytes = f.SizeBytes,
                Width = f.Width,
                Height = f.Height,
                // Keep display-dimension inputs in the SAME page statement.
                // Besides avoiding an extra round-trip, query-shape tests pin
                // this statement as the source of title, album predicates and
                // ordering.
                BlobWidth = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Width)
                    .FirstOrDefault(),
                BlobHeight = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Height)
                    .FirstOrDefault(),
                Orientation = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Orientation)
                    .FirstOrDefault(),
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                EffectiveDateTaken = f.EffectiveDateTaken,
                // Per-row media-kind discriminator for the mixed "Tutti" page:
                // server-detected content type first, client MIME as fallback.
                IsVideo = _db.BlobMetadata
                        .Where(m => m.BlobObjectId == f.BlobObjectId)
                        .Select(m => (string?)m.DetectedContentType)
                        .FirstOrDefault() != null
                    ? _db.BlobMetadata
                        .Where(m => m.BlobObjectId == f.BlobObjectId)
                        .Select(m => m.DetectedContentType!)
                        .First()
                        .StartsWith("video/")
                    : f.MimeType.StartsWith("video/"),
                OccurrenceCount = filters.CollapseDuplicates
                    ? _db.FileItems.Count(g => g.BlobObjectId == f.BlobObjectId
                        && g.OwnerUserId == ownerUserId
                        && g.DeletedAt == null)
                    : 1,
            })
            .ToListAsync(cancellationToken);

        var hasMore = fetched.Count > limit;
        var pageRows = hasMore ? fetched.Take(limit).ToList() : fetched;

        string? nextCursor = null;
        if (hasMore && pageRows.Count > 0)
        {
            var last = pageRows[^1];
            nextCursor = BuildCursor(sort, direction, last, cursorFingerprint).Encode();
        }

        return (pageRows, nextCursor, hasMore, totalCount);
    }

    private static ImageCursor BuildCursor(
        ImageSortField sort, ImageSortDirection direction, GalleryRow last, string? filter)
        => sort switch
        {
            // sort=name orders by the DISPLAY name (title → filename), so the
            // boundary the next page seeks from must be that same key.
            ImageSortField.Name => ImageCursor.FromString(
                sort, direction, MediaDisplayName.SortKey(last.Title, last.Name), last.Id, filter),
            ImageSortField.Size => ImageCursor.FromNumber(sort, direction, last.SizeBytes, last.Id, filter),
            ImageSortField.DateTaken => ImageCursor.FromDate(sort, direction, last.EffectiveDateTaken, last.Id, filter),
            _ => ImageCursor.FromDate(sort, direction, last.CreatedAt, last.Id, filter),
        };

    // Internal projection used by the seek-paginated query so the same row
    // shape can power both the response DTO and the cursor materialisation.
    private sealed class GalleryRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        // Owner-scoped user title (null when unset); resolves to DisplayName.
        public string? Title { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BlobWidth { get; set; }
        public int? BlobHeight { get; set; }
        public int? Orientation { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        // Effective DateTaken (user override → embedded → CreatedAt) computed
        // server-side for the cursor's DateTaken seek + nextCursor encoding.
        public DateTime EffectiveDateTaken { get; set; }
        // Slice 5: per-row media-kind discriminator for the unified projection
        // (true = video, false = image). Never set on the single-kind paths that
        // do not read it.
        public bool IsVideo { get; set; }
        // Slice 75: library-wide count of active FileItems owned by the same
        // user pointing to the same blob. Default 1 (no duplicates / not
        // requested). Never exposes BlobObjectId or SHA-256.
        public int OccurrenceCount { get; set; } = 1;
    }

    // Slice 61 overload: cursor-mode entry point that applies the full filter
    // bag. Owner-scoped + soft-delete-aware + gallery-membership identical to
    // the legacy overload below.
    private IQueryable<FileItem> BuildGalleryQuery(
        Guid ownerUserId, ImageFilters filters, MediaKindScope kind = MediaKindScope.Image)
    {
        var query = BuildGalleryQuery(ownerUserId, filters.FolderId, null, kind);

        // Slice 3 (media organization): per-file media-library scope. The normal
        // galleries pass Active (default); the "Esclusi" tab passes Excluded.
        // This is the single place every filtered surface built on this method
        // (search, counts, duplicate collapsing, cursor + offset) gates on it.
        query = MediaLibrary.MediaLibraryScopePolicy.ApplyScope(query, filters.Scope);

        // q expansion (slice 61). Substring match, case-insensitive, against
        // Name OR user-supplied Title / Description / Tags. Tags are stored
        // as a JSON array string on FileItemUserMetadata.TagsJson; substring
        // search inside the JSON is good enough for the simple discovery UX
        // ("park" matches the tag "park", but also longer tags containing
        // "park" — documented).  User metadata is owner-scoped via the
        // FileItemId join, so another user's title/description/tags can
        // never appear in this owner's results.
        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            var needle = filters.Query.Trim().ToLowerInvariant();
            query = query.Where(f =>
                f.Name.ToLower().Contains(needle)
                || _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Any(u =>
                        (u.Title != null && u.Title.ToLower().Contains(needle))
                        || (u.Description != null && u.Description.ToLower().Contains(needle))
                        || (u.TagsJson != null && u.TagsJson.ToLower().Contains(needle))));
        }

        if (filters.Favorite is bool fav)
        {
            // IsFavorite defaults to false when no user-metadata row exists,
            // so .FirstOrDefault() on `bool` returning false matches a user
            // requesting `favorite=false` correctly.
            query = query.Where(f =>
                _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => (bool?)u.IsFavorite)
                    .FirstOrDefault() == fav
                || (fav == false && !_db.FileItemUserMetadata.Any(u => u.FileItemId == f.Id)));
        }

        if (filters.MinRating is int minR)
        {
            query = query.Where(f =>
                _db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id)
                    .Select(u => u.Rating)
                    .FirstOrDefault() >= minR);
        }

        if (filters.HasGps is bool hasGps)
        {
            // Presence-only: never reads coordinates. GPS in BlobMetadata is
            // blob-derived (slice 54); the filter reflects "this blob has
            // coordinates" without exposing them.
            query = query.Where(f =>
                _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.GpsLatitude != null
                    && m.GpsLongitude != null) == hasGps);
        }

        // Video-metadata filters (video gallery). Each is a presence-style
        // subquery on the blob's ffprobe-derived BlobMetadata; applied only when
        // the caller set it, so the image gallery (which never sets them) pays
        // nothing. A blob not yet probed has null video fields and is excluded by
        // any of these constraints (correct: it does not match a known-value
        // filter).
        if (filters.DurationMinSeconds is double durMin)
        {
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.DurationSeconds != null && m.DurationSeconds >= durMin));
        }
        if (filters.DurationMaxSeconds is double durMax)
        {
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.DurationSeconds != null && m.DurationSeconds <= durMax));
        }
        if (filters.MinWidth is int minW)
        {
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.Width != null && m.Width >= minW));
        }
        if (filters.MinHeight is int minH)
        {
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.Height != null && m.Height >= minH));
        }
        if (!string.IsNullOrWhiteSpace(filters.VideoCodec))
        {
            var codec = filters.VideoCodec.Trim().ToLowerInvariant();
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.VideoCodec != null && m.VideoCodec.ToLower() == codec));
        }
        if (filters.HasAudio is bool hasAudio)
        {
            query = query.Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.HasAudio == hasAudio));
        }

        // Slice 88: DateTaken range filters use the denormalized column too —
        // identical effective-date semantics, now sargable against the index.
        if (filters.DateTakenFrom is DateTime fromUtc)
        {
            var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            query = query.Where(f => f.EffectiveDateTaken >= from);
        }

        if (filters.DateTakenTo is DateTime toUtc)
        {
            var to = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
            query = query.Where(f => f.EffectiveDateTaken <= to);
        }

        // Album constraint (gallery-as-operational-surface). The album is
        // owner-validated by the endpoint; membership is restricted to it.
        // FileItems are already owner-scoped, so cross-owner leakage is
        // impossible even if a foreign albumId slipped through.
        if (filters.AlbumId is Guid albumId)
        {
            query = query.Where(f =>
                _db.AlbumItems.Any(ai => ai.AlbumId == albumId && ai.FileItemId == f.Id));
        }

        // Album membership: "in some album" vs "in no album". A plain EXISTS /
        // NOT EXISTS on album_items keyed by FileItemId (index
        // IX_album_items_FileItemId). Owner-safe without an explicit owner
        // predicate: `query` is already restricted to this owner's FileItems,
        // and an AlbumItem row can only point at a FileItem — so a foreign
        // album can neither add nor remove rows from this owner's result set.
        if (filters.AlbumMembership == AlbumMembershipFilter.Assigned)
        {
            query = query.Where(f => _db.AlbumItems.Any(ai => ai.FileItemId == f.Id));
        }
        else if (filters.AlbumMembership == AlbumMembershipFilter.Unassigned)
        {
            query = query.Where(f => !_db.AlbumItems.Any(ai => ai.FileItemId == f.Id));
        }

        // Similar-photo restrict set (resolved server-side into RestrictToFileIds).
        // A non-null empty list means "restrict to nothing" → no results.
        if (filters.RestrictToFileIds is { } restrict)
        {
            var restrictSet = restrict.ToList();
            query = query.Where(f => restrictSet.Contains(f.Id));
        }

        // People filters (owner-private). A FileItem "contains" a person when
        // one of that person's assigned faces was detected on the FileItem's
        // blob. FaceDetection is blob-level; ownership is enforced via
        // PersonFaceAssignment.OwnerUserId, so a foreign person id matches
        // nothing (no existence leak). "All" ANDs each person; "Any" ORs them;
        // exclude requires none of the excluded people.
        //
        // The EXISTS is nested detection-first (correlate FaceDetection on
        // f.BlobObjectId, THEN check the assignment) rather than assignment-
        // first. This is a pure query-shape choice with identical semantics but
        // a very different plan: the outer correlation now hits
        // ux_face_detections_blob_profile_index (BlobObjectId leading) and the
        // inner assignment lookup hits IX_person_face_assignments_FaceDetectionId
        // / ux_person_face_assignments_owner_face, so Postgres builds the small
        // (detections ⋈ owner-scoped assignments) set once and index-seeks
        // file_items by blob. The assignment-first shape instead forced a
        // nested-loop semi/anti join whose join filter (a.FaceDetectionId AND
        // f.BlobObjectId) was evaluated once per (candidate file × assigned
        // face) — millions of times on a person present in many photos, plus a
        // seq scan of file_items. Measured ~60-290x faster on 8k photos with a
        // heavy person; the win is the plan, not more CPU (both are single-worker
        // plans). See docs/current-work.md.
        if (filters.IncludePersonIds is { Count: > 0 } includePeople)
        {
            if (filters.IncludePeopleMode == PeopleFilterMode.Any)
            {
                var includeSet = includePeople.Distinct().ToList();
                query = query.Where(f => _db.FaceDetections.Any(d =>
                    d.BlobObjectId == f.BlobObjectId
                    && _db.PersonFaceAssignments.Any(a =>
                        a.FaceDetectionId == d.Id
                        && a.OwnerUserId == ownerUserId
                        && includeSet.Contains(a.PersonId))));
            }
            else
            {
                foreach (var personId in includePeople.Distinct())
                {
                    var pid = personId; // capture per-iteration for the closure
                    query = query.Where(f => _db.FaceDetections.Any(d =>
                        d.BlobObjectId == f.BlobObjectId
                        && _db.PersonFaceAssignments.Any(a =>
                            a.FaceDetectionId == d.Id
                            && a.OwnerUserId == ownerUserId
                            && a.PersonId == pid)));
                }
            }
        }

        if (filters.ExcludePersonIds is { Count: > 0 } excludePeople)
        {
            var excludeSet = excludePeople.Distinct().ToList();
            query = query.Where(f => !_db.FaceDetections.Any(d =>
                d.BlobObjectId == f.BlobObjectId
                && _db.PersonFaceAssignments.Any(a =>
                    a.FaceDetectionId == d.Id
                    && a.OwnerUserId == ownerUserId
                    && excludeSet.Contains(a.PersonId))));
        }

        // Slice 75: duplicate collapsing. Keep only the "canonical"
        // FileItem per blob: the OLDEST FILTER-MATCHING FileItem for this
        // user+blob. Using the pre-collapse filtered query (not the raw
        // FileItems table) means a group appears when ANY occurrence
        // matches the active search/filter — not only when the globally-
        // oldest occurrence matches. This satisfies:
        //   "prefer a matching FileItem when filtering/searching".
        // Owner-scoped: the pre-collapse query already restricts to this
        // user, so cross-user FileItems can never become canonical.
        // Cursor seek-pagination is unaffected — filtering happens before
        // the page boundary is applied.
        if (filters.CollapseDuplicates)
        {
            var preCollapse = query;  // snapshot: all filter-matching rows
            query = preCollapse.Where(f =>
                f.Id == preCollapse
                    .Where(g => g.BlobObjectId == f.BlobObjectId)
                    .OrderBy(g => g.CreatedAt)
                    .ThenBy(g => g.Id)
                    .Select(g => g.Id)
                    .First());
        }

        return query;
    }

    // Shared between offset and cursor modes. Owner-scoped, soft-delete-
    // aware, gallery-membership-by-server-detected-mime (slice 54.2). `kind`
    // selects images, videos, or BOTH (slice 5, the unified "Tutti" tab).
    private IQueryable<FileItem> BuildGalleryQuery(
        Guid ownerUserId, Guid? parentFolderId, string? nameQuery, MediaKindScope kind = MediaKindScope.Image)
    {
        // Membership is by SERVER-DETECTED content type (BlobMetadata.
        // DetectedContentType), falling back to the client MIME only for
        // pre-metadata blobs with no row — the same no-leak, owner-scoped,
        // soft-delete-aware rule the single-kind galleries use. `All` matches an
        // item that qualifies as EITHER an image or a video.
        var query = _db.FileItems
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt == null);
        query = kind switch
        {
            MediaKindScope.Image => query.Where(f =>
                _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && m.DetectedContentType.StartsWith("image/"))
                    || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && f.MimeType.StartsWith("image/"))),
            MediaKindScope.Video => query.Where(f =>
                _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && m.DetectedContentType.StartsWith("video/"))
                    || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && f.MimeType.StartsWith("video/"))),
            _ => query.Where(f =>
                _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && (m.DetectedContentType.StartsWith("image/")
                            || m.DetectedContentType.StartsWith("video/")))
                    || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && (f.MimeType.StartsWith("image/") || f.MimeType.StartsWith("video/")))),
        };

        // Slice 94: media-library eligibility (folder exclusion rules) — the
        // ONLY place the cursor-mode galleries (and everything built on
        // BuildGalleryQuery: search, filters, counts, duplicate collapsing) gate
        // on it. Single kinds delegate to the shared per-kind policy; `All`
        // hides a photo only in a photos-excluded folder and a video only in a
        // videos-excluded folder (so a photo in a video-excluded folder stays).
        if (_mediaLibrary is not null)
        {
            query = kind switch
            {
                MediaKindScope.Image => _mediaLibrary.ApplyMediaLibraryVisibility(
                    query, NubArca.Api.MediaLibrary.MediaKind.Photo),
                MediaKindScope.Video => _mediaLibrary.ApplyMediaLibraryVisibility(
                    query, NubArca.Api.MediaLibrary.MediaKind.Video),
                // Mixed kind: an item is hidden only when ITS OWN kind's folder
                // exclusion applies. The image/video tests are inlined (a custom
                // method call cannot be translated to SQL inside a Where).
                _ => query.Where(f => !_db.Folders.Any(d => d.Id == f.ParentFolderId
                    && ((d.MediaPhotosExcluded && (
                            _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                                && m.DetectedContentType != null
                                && m.DetectedContentType.StartsWith("image/"))
                            || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                                && f.MimeType.StartsWith("image/"))))
                        || (d.MediaVideosExcluded && (
                            _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                                && m.DetectedContentType != null
                                && m.DetectedContentType.StartsWith("video/"))
                            || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                                && f.MimeType.StartsWith("video/"))))))),
            };
        }

        if (parentFolderId is Guid parentId)
        {
            query = query.Where(f => f.ParentFolderId == parentId);
        }

        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            var needle = nameQuery.Trim().ToLowerInvariant();
            query = query.Where(f => f.Name.ToLower().Contains(needle));
        }

        return query;
    }

    // Seek predicate. For sort=created desc + cursor (V,I) the next page is:
    //   (f.CreatedAt < V) OR (f.CreatedAt == V AND f.Id < I)
    // Asc flips the inequality. The Id tie-breaker mirrors ApplyOrdering.
    // The effective DateTaken seek correlates each FileItem to its metadata
    // rows the same way ApplyOrdering does.
    private IQueryable<FileItem> ApplyCursorSeek(
        IQueryable<FileItem> query, ImageCursor cursor)
    {
        var asc = cursor.Direction == ImageSortDirection.Asc;
        var cursorId = cursor.Id;

        return cursor.Sort switch
        {
            // sort=name seeks on the same lower(COALESCE(title, name)) key the
            // ORDER BY uses (see DisplaySortKeyExpression). The projection-then-
            // filter shape lets the key expression be written once; the trailing
            // Select restores IQueryable<FileItem> so callers are unaffected.
            ImageSortField.Name when cursor.PrimaryString is string sName => asc
                ? query
                    .Select(f => new
                    {
                        File = f,
                        Key = (_db.FileItemUserMetadata
                            .Where(u => u.FileItemId == f.Id)
                            .Select(u => u.Title)
                            .FirstOrDefault() ?? f.Name).ToLower(),
                    })
                    .Where(x => string.Compare(x.Key, sName) > 0
                        || (x.Key == sName && x.File.Id.CompareTo(cursorId) > 0))
                    .Select(x => x.File)
                : query
                    .Select(f => new
                    {
                        File = f,
                        Key = (_db.FileItemUserMetadata
                            .Where(u => u.FileItemId == f.Id)
                            .Select(u => u.Title)
                            .FirstOrDefault() ?? f.Name).ToLower(),
                    })
                    .Where(x => string.Compare(x.Key, sName) < 0
                        || (x.Key == sName && x.File.Id.CompareTo(cursorId) < 0))
                    .Select(x => x.File),
            ImageSortField.Size when cursor.PrimaryNumber is long n => asc
                ? query.Where(f => f.SizeBytes > n
                    || (f.SizeBytes == n && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => f.SizeBytes < n
                    || (f.SizeBytes == n && f.Id.CompareTo(cursorId) < 0)),
            // Slice 88: seek on the denormalized EffectiveDateTaken column so the
            // (date, Id) keyset predicate maps onto the effdate index.
            ImageSortField.DateTaken when cursor.PrimaryDate is DateTime dt => asc
                ? query.Where(f =>
                    f.EffectiveDateTaken > dt
                    || (f.EffectiveDateTaken == dt && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f =>
                    f.EffectiveDateTaken < dt
                    || (f.EffectiveDateTaken == dt && f.Id.CompareTo(cursorId) < 0)),
            // Default to created-sort semantics (any cursor with a date primary)
            _ when cursor.PrimaryDate is DateTime d => asc
                ? query.Where(f => f.CreatedAt > d
                    || (f.CreatedAt == d && f.Id.CompareTo(cursorId) > 0))
                : query.Where(f => f.CreatedAt < d
                    || (f.CreatedAt == d && f.Id.CompareTo(cursorId) < 0)),
            _ => query,
        };
    }

    public async Task<FileItem?> RenameAsync(
        Guid ownerUserId,
        Guid fileItemId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateAndTrimName(newName);

        var file = await _db.FileItems.FirstOrDefaultAsync(
            f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
            cancellationToken);
        if (file is null)
        {
            return null;
        }

        if (file.Name == validatedName)
        {
            return file; // no-op
        }

        var siblingExists = await _db.FileItems
            .AsNoTracking()
            .AnyAsync(
                f => f.OwnerUserId == ownerUserId
                    && f.ParentFolderId == file.ParentFolderId
                    && f.DeletedAt == null
                    && f.Id != fileItemId
                    && f.Name == validatedName,
                cancellationToken);
        if (siblingExists)
        {
            throw new DuplicateFileNameException(ownerUserId, file.ParentFolderId, validatedName);
        }

        file.Name = validatedName;
        file.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return file;
        }
        catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
        {
            _db.Entry(file).State = EntityState.Detached;
            throw new DuplicateFileNameException(ownerUserId, file.ParentFolderId, validatedName);
        }
    }

    public async Task<FileItem?> MoveAsync(
        Guid ownerUserId,
        Guid fileItemId,
        Guid? newParentFolderId,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var file = await _db.FileItems.FirstOrDefaultAsync(
                f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
            if (file is null)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            if (newParentFolderId is Guid newParentId)
            {
                var parentValid = await _db.Folders
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.Id == newParentId
                            && f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null,
                        cancellationToken);
                if (!parentValid)
                {
                    throw new FolderNotFoundException(newParentId);
                }
            }

            if (file.ParentFolderId == newParentFolderId)
            {
                await tx.CommitAsync(cancellationToken);
                return file; // no-op
            }

            var siblingExists = await _db.FileItems
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == newParentFolderId
                        && f.DeletedAt == null
                        && f.Id != fileItemId
                        && f.Name == file.Name,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFileNameException(ownerUserId, newParentFolderId, file.Name);
            }

            file.ParentFolderId = newParentFolderId;
            file.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                _db.Entry(file).State = EntityState.Detached;
                throw new DuplicateFileNameException(ownerUserId, newParentFolderId, file.Name);
            }

            await tx.CommitAsync(cancellationToken);
            return file;
        });
    }

    public async Task<FileItem?> MoveToFolderAsync(
        Guid ownerUserId,
        Guid fileItemId,
        Guid? targetParentFolderId,
        string finalName,
        CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateAndTrimName(finalName);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var file = await _db.FileItems.FirstOrDefaultAsync(
                f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
            if (file is null)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            if (targetParentFolderId is Guid newParentId)
            {
                var parentValid = await _db.Folders
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.Id == newParentId
                            && f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null,
                        cancellationToken);
                if (!parentValid)
                {
                    throw new FolderNotFoundException(newParentId);
                }
            }

            if (file.ParentFolderId == targetParentFolderId && file.Name == validatedName)
            {
                await tx.CommitAsync(cancellationToken);
                return file; // no-op
            }

            var siblingExists = await _db.FileItems
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == targetParentFolderId
                        && f.DeletedAt == null
                        && f.Id != fileItemId
                        && f.Name == validatedName,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFileNameException(ownerUserId, targetParentFolderId, validatedName);
            }

            file.ParentFolderId = targetParentFolderId;
            file.Name = validatedName;
            file.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                _db.Entry(file).State = EntityState.Detached;
                throw new DuplicateFileNameException(ownerUserId, targetParentFolderId, validatedName);
            }

            await tx.CommitAsync(cancellationToken);
            return file;
        });
    }

    public async Task<bool> SoftDeleteAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default,
        FileDeleteReason reason = FileDeleteReason.Unspecified)
    {
        // Read the blob id first so we know which row to release after the
        // soft-delete commits. If the file is missing / foreign / already
        // deleted, this returns null and we short-circuit (no decrement).
        // Also capture the (safe) name for the tombstone snapshot.
        var target = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Select(f => new { f.BlobObjectId, f.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return false;
        }
        var blobObjectId = target.BlobObjectId;

        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var affected = await _db.FileItems
                .Where(f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(f => f.DeletedAt, _ => (DateTime?)now),
                    cancellationToken);

            if (affected == 0)
            {
                // Lost a race with another writer who already soft-deleted the
                // same file. They will have released the blob; we must not.
                await tx.CommitAsync(cancellationToken);
                return false;
            }

            // A trashed FileItem remains a real, restorable owner through its
            // FK. Release the ACTIVE refcount, but do not start the physical
            // purge grace window until the retained row is hard-deleted.
            await _db.BlobObjects
                .Where(b => b.Id == blobObjectId && b.ReferenceCount > 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount - 1)
                        .SetProperty(b => b.PurgeEligibleAt, _ => null),
                    cancellationToken);

            // deleted-content-import-skip: record a tombstone iff this was an
            // explicit user-intent delete of the owner's final active occurrence
            // of the content. Runs inside this transaction (shared DbContext) so
            // the ledger write commits atomically with the delete; a no-op for
            // every other reason. Never blocks the delete on a ledger failure.
            if (_tombstones is not null && reason.MayRecordTombstone())
            {
                try
                {
                    await _tombstones.RecordFinalOccurrenceDeletionAsync(
                        ownerUserId, blobObjectId, reason,
                        fileNameSnapshot: target.Name,
                        deletedFromPathSnapshot: null,
                        cancellationToken);
                }
                catch
                {
                    // The delete itself must always succeed; the ledger is
                    // best-effort. Detach any half-tracked ledger entity so the
                    // failure can't poison the committed delete, then continue.
                    foreach (var e in _db.ChangeTracker
                        .Entries<OwnerDeletedContentTombstone>().ToList())
                    {
                        e.State = EntityState.Detached;
                    }
                }
            }

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<FileItem?> RestoreAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // Owner-scoped lookup that ignores DeletedAt so we can distinguish
            // "missing / foreign" (return null → 404) from "already active"
            // (idempotent no-op success) and "soft-deleted" (do real work).
            var file = await _db.FileItems
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    f => f.Id == fileItemId && f.OwnerUserId == ownerUserId,
                    cancellationToken);
            if (file is null)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            if (file.DeletedAt is null)
            {
                // Idempotent: restoring an already-active file is a no-op success.
                // ReferenceCount is intentionally not incremented again.
                await tx.CommitAsync(cancellationToken);
                return file;
            }

            // Parent + sibling checks inside the lock so a concurrent
            // FolderService.SoftDeleteAsync on the parent cannot slip between
            // "parent active?" and "clear DeletedAt" — the second mover sees
            // the first mover's commit and 409s as appropriate.
            if (file.ParentFolderId is Guid parentId)
            {
                var parentActive = await _db.Folders
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.Id == parentId
                            && f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null,
                        cancellationToken);
                if (!parentActive)
                {
                    throw new RestoreParentDeletedException(parentId);
                }
            }

            var siblingExists = await _db.FileItems
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == file.ParentFolderId
                        && f.DeletedAt == null
                        && f.Id != fileItemId
                        && f.Name == file.Name,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFileNameException(ownerUserId, file.ParentFolderId, file.Name);
            }

            int affected;
            try
            {
                // Atomic gate: only flip + bump if the row is still soft-deleted.
                // Without the DeletedAt != null clause two concurrent restores
                // would both increment ReferenceCount.
                affected = await _db.FileItems
                    .Where(f => f.Id == fileItemId
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt != null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(f => f.DeletedAt, _ => (DateTime?)null)
                            .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                await tx.RollbackAsync(cancellationToken);
                throw new DuplicateFileNameException(ownerUserId, file.ParentFolderId, file.Name);
            }

            if (affected == 0)
            {
                // Lost a race: another writer either already restored or hard-
                // deleted this row. No increment, no audit.
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            // Re-increment the BlobObject.ReferenceCount that SoftDeleteAsync
            // released. The WHERE clause is intentionally minimal: a paired
            // soft-delete decremented exactly once, so we increment exactly
            // once. The atomic gate above guarantees we only get here when the
            // soft-delete transition was real.
            await _db.BlobObjects
                .Where(b => b.Id == file.BlobObjectId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount + 1)
                        .SetProperty(b => b.PurgeEligibleAt, _ => null),
                    cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return await _db.FileItems
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileItemId, cancellationToken);
        });
    }

    public async Task<bool> PermanentDeleteAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // Owner-scoped lookup that includes soft-deleted rows so we can
        // distinguish "missing / foreign" (return false → 404) from
        // "active, not in trash" (throw → 409) from "soft-deleted" (do work).
        var current = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId)
            .Select(f => new { f.Id, f.BlobObjectId, f.DeletedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return false;
        }

        if (current.DeletedAt is null)
        {
            throw new ResourceNotInTrashException(fileItemId);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // Capture thumbnail blob ids so we can release their ReferenceCount
            // after the FileItem row is gone. Mirrors FileItemSweeper exactly.
            var thumbnailBlobIds = await _db.FileThumbnails
                .Where(t => t.FileItemId == fileItemId)
                .Select(t => t.BlobObjectId)
                .ToListAsync(cancellationToken);

            // Delete dependent share_links + thumbnails + user metadata + album memberships first
            // (all FK Restrict to FileItem).
            await _db.ShareLinks
                .Where(s => s.FileItemId == fileItemId)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.FileThumbnails
                .Where(t => t.FileItemId == fileItemId)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.FileItemUserMetadata
                .Where(m => m.FileItemId == fileItemId)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.AlbumItems
                .Where(ai => ai.FileItemId == fileItemId)
                .ExecuteDeleteAsync(cancellationToken);
            // Slice 94: the owner-scoped GPS projection dies with the file.
            await _db.FileItemLocations
                .Where(l => l.FileItemId == fileItemId)
                .ExecuteDeleteAsync(cancellationToken);

            // Atomic gate: only delete if the row is still soft-deleted and
            // owned. Defends against a concurrent restore between the
            // pre-check and this delete.
            var rowsDeleted = await _db.FileItems
                .Where(f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt != null)
                .ExecuteDeleteAsync(cancellationToken);
            if (rowsDeleted == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Release ReferenceCount for each thumbnail blob. The file blob
            // itself was already decremented on soft-delete (slice 15); we
            // deliberately don't decrement it again here.
            foreach (var thumbBlobId in thumbnailBlobIds)
            {
                await _blobService.ReleaseAsync(thumbBlobId, cancellationToken);
            }

            // Soft-delete deliberately left this restorable blob ineligible.
            // The retained FileItem row is now gone, so start the grace window
            // without decrementing the already-zero active refcount again.
            await _blobService.MarkPurgeEligibleIfUnreferencedAsync(
                current.BlobObjectId,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<FileItem?> StripEmbeddedMetadataAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        if (_stripper is null)
        {
            throw new InvalidOperationException(
                $"{nameof(IImageMetadataStripper)} is not registered. " +
                "StripEmbeddedMetadataAsync requires a stripper to be injected.");
        }

        // Owner-scoped + soft-delete-aware lookup. Missing / foreign /
        // soft-deleted all collapse to null → 404 (no-leak).
        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        // The dispatch key is the SERVER-DETECTED content type from
        // BlobMetadata (slice 54.2). The client MimeType is untrusted; a
        // null detection means the bytes weren't recognised as an image
        // we know how to safely re-encode → 415.
        var detectedContentType = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == file.BlobObjectId)
            .Select(m => m.DetectedContentType)
            .FirstOrDefaultAsync(cancellationToken);

        if (!_stripper.IsSupported(detectedContentType))
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        // Re-encode bytes through the stripper. The original blob is read
        // but NEVER modified — blobs are immutable by contract. The stripper
        // returns a fresh in-memory stream of new bytes.
        MemoryStream strippedBytes;
        await using (var source = await _blobService.OpenContentAsync(file.BlobObjectId, cancellationToken))
        {
            strippedBytes = await _stripper.StripAsync(source, detectedContentType!, cancellationToken);
        }

        BlobObject newBlob;
        await using (strippedBytes)
        {
            strippedBytes.Position = 0;
            // Content-addressed store. If the stripped bytes resolve to an
            // existing BlobObject (e.g. the file was already stripped, or
            // another file with the same stripped content was uploaded
            // earlier), this just increments ReferenceCount.
            newBlob = await _blobService.StoreAsync(strippedBytes, cancellationToken);
        }

        // Hand the freshly-stored blob to the shared strong-mutation helper,
        // which performs the atomic FileItem repoint + derivative regeneration
        // (or releases the refcount on the idempotent / lost-race paths).
        return await RepointFileToNewBlobAsync(file, ownerUserId, newBlob, cancellationToken);
    }

    // Slice 66: bake the user's DateTaken override into the image bytes.
    // Strong mutation — produces a NEW blob (new SHA-256) and repoints only
    // this FileItem. Requires a DateTaken override on the file's user
    // metadata (set via PATCH /metadata); throws MetadataOperationInputMissingException
    // (400) when absent and UnsupportedImageFormatException (415) for formats
    // the writer can't handle. Other metadata + other FileItems are untouched.
    public async Task<FileItem?> WriteDateTakenAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        if (_metadataWriter is null)
        {
            throw new InvalidOperationException(
                $"{nameof(IImageMetadataWriter)} is not registered. " +
                "WriteDateTakenAsync requires a writer to be injected.");
        }

        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        var detectedContentType = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == file.BlobObjectId)
            .Select(m => m.DetectedContentType)
            .FirstOrDefaultAsync(cancellationToken);

        if (!_metadataWriter.SupportsDateTaken(detectedContentType))
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        // The value to bake in is the owner's explicit DateTaken override.
        // We never invent a value or fall back to embedded/upload dates here —
        // this operation is "write MY corrected date into the file".
        var dateTakenOverride = await _db.FileItemUserMetadata
            .AsNoTracking()
            .Where(u => u.FileItemId == fileItemId)
            .Select(u => u.DateTakenOverride)
            .FirstOrDefaultAsync(cancellationToken);
        if (dateTakenOverride is not DateTime dateTaken)
        {
            throw new MetadataOperationInputMissingException(
                "Set a Date taken override on this file before writing it into the image.");
        }

        MemoryStream newBytes;
        await using (var source = await _blobService.OpenContentAsync(file.BlobObjectId, cancellationToken))
        {
            newBytes = await _metadataWriter.WriteDateTakenAsync(
                source, detectedContentType!, dateTaken, cancellationToken);
        }

        BlobObject newBlob;
        await using (newBytes)
        {
            newBytes.Position = 0;
            newBlob = await _blobService.StoreAsync(newBytes, cancellationToken);
        }

        return await RepointFileToNewBlobAsync(file, ownerUserId, newBlob, cancellationToken);
    }

    // Slice 66: privacy-safe read. Streams metadata-stripped bytes WITHOUT
    // mutating the FileItem or creating a new blob — the source blob is read
    // and re-encoded on the fly. Owner-scoped; null = 404; throws
    // UnsupportedImageFormatException (415) for non-strippable formats.
    public async Task<FileContent?> OpenPrivacySafeContentAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        if (_stripper is null)
        {
            throw new InvalidOperationException(
                $"{nameof(IImageMetadataStripper)} is not registered. " +
                "OpenPrivacySafeContentAsync requires a stripper to be injected.");
        }

        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        var detectedContentType = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == file.BlobObjectId)
            .Select(m => m.DetectedContentType)
            .FirstOrDefaultAsync(cancellationToken);

        if (!_stripper.IsSupported(detectedContentType))
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        MemoryStream stripped;
        await using (var source = await _blobService.OpenContentAsync(file.BlobObjectId, cancellationToken))
        {
            stripped = await _stripper.StripAsync(source, detectedContentType!, cancellationToken);
        }
        stripped.Position = 0;

        // The returned content reports the SERVER-DETECTED type (the only one
        // the serving layer may trust). The original FileItem is unchanged.
        return new FileContent(
            stripped, file.MimeType, stripped.Length, file.Name, detectedContentType);
    }

    // Shared strong-mutation tail (slices 58 + 66). Given a freshly-stored
    // `newBlob` whose refcount the CALLER currently holds, atomically repoints
    // `file` to it (guarded against concurrent strip/move/restore), releases
    // the old file + thumbnail blob refcounts, then regenerates BlobMetadata +
    // the small thumbnail from the new bytes. Returns the updated FileItem,
    // the unchanged `file` when the bytes were identical (idempotent no-op,
    // releases the duplicate refcount), or null when the row moved out from
    // under us (also releases the refcount). User metadata rows hang off the
    // FileItem and are therefore preserved across the swap.
    private async Task<FileItem?> RepointFileToNewBlobAsync(
        FileItem file,
        Guid ownerUserId,
        BlobObject newBlob,
        CancellationToken cancellationToken)
    {
        var fileItemId = file.Id;
        var newBlobOwnedByCaller = true;
        try
        {
            if (newBlob.Id == file.BlobObjectId)
            {
                // Deterministic encoders can produce the same bytes (e.g. a
                // re-strip of an already-clean file). Release the duplicate
                // refcount so the row's accounting is unchanged.
                await _blobService.ReleaseAsync(newBlob.Id, cancellationToken);
                newBlobOwnedByCaller = false;
                return file;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var swapped = false;

            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

                var capturedThumbBlobIds = await _db.FileThumbnails
                    .Where(t => t.FileItemId == fileItemId)
                    .Select(t => t.BlobObjectId)
                    .ToListAsync(cancellationToken);

                await _db.FileThumbnails
                    .Where(t => t.FileItemId == fileItemId)
                    .ExecuteDeleteAsync(cancellationToken);

                // Atomic swap. The blob_object_id guard rejects a swap that
                // races with a concurrent strip / soft-delete + restore that
                // put a different blob under us.
                var rows = await _db.FileItems
                    .Where(f => f.Id == fileItemId
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt == null
                        && f.BlobObjectId == file.BlobObjectId)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(f => f.BlobObjectId, _ => newBlob.Id)
                            .SetProperty(f => f.SizeBytes, _ => newBlob.SizeBytes)
                            .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);

                if (rows == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                foreach (var thumbBlobId in capturedThumbBlobIds)
                {
                    await _blobService.ReleaseAsync(thumbBlobId, cancellationToken);
                }

                // Release the OLD file blob reference. The row stays — if other
                // FileItems reference it, ReferenceCount stays > 0; otherwise
                // BlobJanitor reclaims it after the grace window.
                await _blobService.ReleaseAsync(file.BlobObjectId, cancellationToken);

                await tx.CommitAsync(cancellationToken);
                swapped = true;
            });

            if (!swapped)
            {
                await _blobService.ReleaseAsync(newBlob.Id, cancellationToken);
                newBlobOwnedByCaller = false;
                return null;
            }

            newBlobOwnedByCaller = false;

            // Regenerate blob-derived metadata from the new bytes.
            var newFacts = await TryDetectImageFactsAsync(newBlob.Id, cancellationToken);
            await EnsureBlobMetadataAsync(
                newBlob, newFacts, file.MimeType, extractEmbeddedMetadata: true, cancellationToken);
            // Slice 94: the file now points at different bytes — rebuild its
            // GPS projection from the NEW blob (a strip typically removes the
            // coordinates, so this usually deletes the row).
            await RefreshLocationsForBlobAsync(newBlob.Id, cancellationToken);

            await _db.FileItems
                .Where(f => f.Id == fileItemId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(f => f.Width, _ => newFacts.Width)
                        .SetProperty(f => f.Height, _ => newFacts.Height),
                    cancellationToken);

            // The blob under this file changed (e.g. strip drops the embedded
            // date, write-DateTaken bakes it in), so its effective capture date
            // may change. A user override still wins; otherwise it layers the
            // new blob's embedded date over CreatedAt. CreatedAt is unchanged by
            // a repoint, so the passed-in `file.CreatedAt` is authoritative.
            var newBlobDate = await _db.BlobMetadata
                .AsNoTracking()
                .Where(m => m.BlobObjectId == newBlob.Id)
                .Select(m => m.DateTaken)
                .FirstOrDefaultAsync(cancellationToken);
            var overrideDate = await _db.FileItemUserMetadata
                .AsNoTracking()
                .Where(u => u.FileItemId == fileItemId)
                .Select(u => u.DateTakenOverride)
                .FirstOrDefaultAsync(cancellationToken);
            var (repointEff, repointSrc) = EffectiveDateTakenSources.Compute(
                overrideDate, newBlobDate, file.CreatedAt);
            await _db.FileItems
                .Where(f => f.Id == fileItemId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(f => f.EffectiveDateTaken, _ => repointEff)
                        .SetProperty(f => f.EffectiveDateTakenSource, _ => repointSrc),
                    cancellationToken);

            // Regenerate the small thumbnail. Best-effort — thumbnail failures
            // never break the operation, matching the upload path.
            if (newFacts.IsImage)
            {
                var generated = await _thumbnails.TryGenerateSmallAsync(
                    fileItemId, newBlob.Id, cancellationToken);
                await SetBlobThumbnailStatusAsync(
                    newBlob.Id,
                    generated ? MetadataStatuses.Generated : MetadataStatuses.Skipped,
                    cancellationToken);
            }

            return await _db.FileItems
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == fileItemId, cancellationToken);
        }
        catch
        {
            if (newBlobOwnedByCaller)
            {
                try
                {
                    await _blobService.ReleaseAsync(newBlob.Id, CancellationToken.None);
                }
                catch
                {
                    // best-effort
                }
            }
            throw;
        }
    }

    // Slice 98: internal so the admin-import DB batch pipeline applies the
    // EXACT same validation/normalisation as the per-file path (no drift).
    internal static string ValidateAndTrimName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"File name must be {MaxNameLength} characters or fewer.",
                nameof(name));
        }

        if (trimmed.Contains('/'))
        {
            throw new ArgumentException("File name must not contain '/'.", nameof(name));
        }

        if (trimmed.Contains('\\'))
        {
            throw new ArgumentException("File name must not contain '\\'.", nameof(name));
        }

        if (trimmed is "." or "..")
        {
            throw new ArgumentException("File name must not be '.' or '..'.", nameof(name));
        }

        return trimmed;
    }

    internal static string NormalizeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return DefaultMimeType;
        }

        var trimmed = mimeType.Trim();
        if (trimmed.Length > MaxMimeTypeLength)
        {
            throw new ArgumentException(
                $"MIME type must be {MaxMimeTypeLength} characters or fewer.",
                nameof(mimeType));
        }

        return trimmed;
    }

    private static bool IsSiblingNameUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName == SiblingNameUniqueIndex;
    }

    // Best-effort blob fact extraction. Tries image header detection first
    // (header-only ImageSharp identify), then falls back to slice-62 video
    // signature detection. Any failure silently yields empty facts so the
    // upload itself never fails on detection.
    private async Task<BlobImageFacts> TryDetectImageFactsAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = await _blobService.OpenContentAsync(blobObjectId, cancellationToken))
            {
                var info = await Image.IdentifyAsync(stream, cancellationToken);
                if (info is not null)
                {
                    var format = info.Metadata.DecodedImageFormat;
                    return new BlobImageFacts(
                        info.Width, info.Height, format?.Name, format?.DefaultMimeType, BlobMediaKind.Image);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // fall through to video detection
        }

        // Slice 62: video signature detection. The stream is reopened (the
        // image path may have advanced or disposed it) so the sniffer starts
        // at offset 0.
        try
        {
            await using var videoStream = await _blobService.OpenContentAsync(blobObjectId, cancellationToken);
            var sig = await _videoDetector.InspectAsync(videoStream, cancellationToken);
            if (sig is not null)
            {
                return new BlobImageFacts(
                    Width: null, Height: null,
                    Format: sig.Container, ContentType: sig.ContentType,
                    Kind: BlobMediaKind.Video);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // best-effort
        }

        return default;
    }

    // Detected, blob-derived facts. IsImage is the gate for dimension-dependent
    // work (thumbnail generation, pixel count). IsVideo is the gate for the
    // slice-62 video playback endpoint and the "video" media category.
    private readonly record struct BlobImageFacts(
        int? Width, int? Height, string? Format, string? ContentType,
        BlobMediaKind Kind = BlobMediaKind.None)
    {
        public bool IsImage => Kind == BlobMediaKind.Image;
        public bool IsVideo => Kind == BlobMediaKind.Video;
    }

    private enum BlobMediaKind { None = 0, Image = 1, Video = 2 }

    // Inserts the one-per-blob BlobMetadata row if it does not yet exist.
    // Returns true when this call created it, false when a row already existed
    // (dedup) or a concurrent ingest won the race. Blob metadata is immutable
    // from the user's perspective, so we never overwrite an existing row here.
    // Existence-checked, self-committing variant used by the strong-mutation
    // repoint path (strip / DateTaken writeback). The hot upload path uses
    // BuildBlobMetadataAsync directly and persists the row inside the FileItem
    // transaction instead (slice 95).
    private async Task<bool> EnsureBlobMetadataAsync(
        BlobObject blob,
        BlobImageFacts facts,
        string fallbackMimeType,
        bool extractEmbeddedMetadata,
        CancellationToken cancellationToken)
    {
        var exists = await _db.BlobMetadata
            .AsNoTracking()
            .AnyAsync(m => m.BlobObjectId == blob.Id, cancellationToken);
        if (exists)
        {
            return false;
        }

        var meta = await BuildBlobMetadataAsync(
            blob, facts, fallbackMimeType, extractEmbeddedMetadata, cancellationToken);
        _db.BlobMetadata.Add(meta);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsBlobMetadataUniqueViolation(ex))
        {
            // A concurrent upload of identical new bytes created the row first.
            _db.Entry(meta).State = EntityState.Detached;
            return false;
        }
    }

    // Builds (without persisting) the one-per-blob metadata row from the
    // detection facts, optionally running the full embedded extraction inline.
    private async Task<BlobMetadata> BuildBlobMetadataAsync(
        BlobObject blob,
        BlobImageFacts facts,
        string fallbackMimeType,
        bool extractEmbeddedMetadata,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // Malformed images can report a non-positive dimension; coerce to NULL
        // so we never violate ck_blob_metadata_{width,height}_positive (the file
        // still imports, just without dimensions). Shared with the batch path.
        var (width, height, pixelCount) = BlobDimensions.Normalize(facts.Width, facts.Height);

        var meta = new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blob.Id,
            SizeBytes = blob.SizeBytes,
            DetectedContentType = facts.ContentType,
            // Server-detected category. Image / Video win when the bytes are
            // recognised; otherwise fall back to the (untrusted) client MIME
            // bucket. A spoofed "image/jpeg" upload with non-image bytes ends
            // up here with ContentType=null + Category=image (from MIME), but
            // gallery/playback gates downstream both require a non-null
            // server-detected content type, so playback / gallery membership
            // is never granted on spoofed MIME alone.
            MediaCategory = facts.IsImage
                ? MediaCategories.Image
                : facts.IsVideo
                    ? MediaCategories.Video
                    : MediaCategories.FromMimeType(fallbackMimeType),
            DetectedFormat = facts.Format,
            Width = width,
            Height = height,
            PixelCount = pixelCount,
            ThumbnailStatus = facts.IsImage ? MetadataStatuses.Pending : MetadataStatuses.Skipped,
            CreatedAt = now,
        };

        if (facts.IsImage && extractEmbeddedMetadata)
        {
            // Exhaustive embedded extraction (EXIF/GPS/IPTC/XMP/ICC/...). Runs
            // once per blob, here, before the row is persisted so the typed
            // fields + raw document commit atomically with the metadata row.
            // Non-fatal: any failure resolves to a safe status/error code.
            await RunEmbeddedExtractionAsync(meta, blob.Id, now, cancellationToken);
        }
        else if (facts.IsImage)
        {
            // Slice 94 (pipeline V2): full embedded extraction is deferred to
            // the asynchronous metadata.embedded.backfill job — the row keeps
            // only the cheap detection facts (content type, dimensions,
            // category) the gallery/derivative gates need. `pending` is
            // exactly what the backfill's candidate query selects.
            meta.ExtractionStatus = MetadataStatuses.Pending;
        }
        else
        {
            // No embedded image metadata to extract from a non-image blob.
            meta.ExtractionStatus = MetadataStatuses.Skipped;
        }

        return meta;
    }

    private Task SetBlobThumbnailStatusAsync(
        Guid blobObjectId,
        string status,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return _db.BlobMetadata
            .Where(m => m.BlobObjectId == blobObjectId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.ThumbnailStatus, _ => status)
                    .SetProperty(m => m.UpdatedAt, _ => (DateTime?)now),
                cancellationToken);
    }

    private static bool IsBlobMetadataUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName == BlobMetadataUniqueIndex;
    }

    // Opens the blob bytes, runs embedded extraction, and maps the result onto
    // the metadata row. Never throws: a stream-open failure resolves to a
    // Failed status with a sanitized error code so the upload still completes.
    private async Task RunEmbeddedExtractionAsync(
        BlobMetadata meta,
        Guid blobObjectId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ImageMetadataExtractionResult result;
        try
        {
            await using var stream = await _blobService.OpenContentAsync(blobObjectId, cancellationToken);
            result = _embeddedExtractor.Extract(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result = ImageMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.IoError, EmbeddedImageMetadataExtractor.Version);
        }

        ApplyEmbeddedMetadata(meta, result, now);
    }

    // Maps a typed extraction result onto a BlobMetadata row. Pure copy — no
    // I/O. Used by both the upload path and the re-extraction hook.
    private static void ApplyEmbeddedMetadata(BlobMetadata meta, ImageMetadataExtractionResult r, DateTime now)
    {
        meta.ExtractionStatus = r.Status;
        meta.ExtractionErrorCode = r.ErrorCode;
        meta.ExtractionVersion = r.Version;
        meta.ExtractedAt = now;
        meta.RawMetadataJson = r.RawMetadataJson;

        meta.DateTaken = r.DateTaken;
        meta.DateTakenSource = r.DateTakenSource;
        meta.DateTakenOffset = r.DateTakenOffset;
        meta.Orientation = r.Orientation;

        meta.CameraMake = r.CameraMake;
        meta.CameraModel = r.CameraModel;
        meta.LensMake = r.LensMake;
        meta.LensModel = r.LensModel;
        meta.Software = r.Software;
        meta.BodySerialNumber = r.BodySerialNumber;
        meta.LensSerialNumber = r.LensSerialNumber;

        meta.IsoSpeed = r.IsoSpeed;
        meta.FNumber = r.FNumber;
        meta.ExposureTime = r.ExposureTime;
        meta.FocalLength = r.FocalLength;
        meta.FocalLength35mm = r.FocalLength35mm;
        meta.ExposureBias = r.ExposureBias;
        meta.ExposureProgram = r.ExposureProgram;
        meta.MeteringMode = r.MeteringMode;
        meta.Flash = r.Flash;
        meta.WhiteBalance = r.WhiteBalance;

        meta.ColorSpace = r.ColorSpace;
        meta.HasIccProfile = r.HasIccProfile;
        meta.IccProfileName = r.IccProfileName;

        meta.GpsLatitude = r.GpsLatitude;
        meta.GpsLongitude = r.GpsLongitude;
        meta.GpsAltitude = r.GpsAltitude;
    }

    // Re-runs embedded extraction for one existing blob's metadata row and
    // persists the refreshed typed fields. Returns false when no metadata row
    // exists for the blob. Driven by the slice-55 backfill (MetadataBackfillService)
    // over many blobs; the upload path does not call it. Non-image blobs are
    // left as "skipped" without touching the bytes.
    public async Task<bool> ReExtractEmbeddedMetadataAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken = default)
    {
        var meta = await _db.BlobMetadata
            .FirstOrDefaultAsync(m => m.BlobObjectId == blobObjectId, cancellationToken);
        if (meta is null)
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (meta.MediaCategory != MediaCategories.Image)
        {
            meta.ExtractionStatus = MetadataStatuses.Skipped;
            meta.ExtractionErrorCode = null;
            meta.ExtractionVersion = EmbeddedImageMetadataExtractor.Version;
            meta.ExtractedAt = now;
            meta.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        await RunEmbeddedExtractionAsync(meta, blobObjectId, now, cancellationToken);
        meta.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        await RecomputeEffectiveDatesForBlobAsync(blobObjectId, meta.DateTaken, cancellationToken);
        // Slice 94: keep the owner-scoped GPS projection in step with the
        // (re)extracted coordinates — runs AFTER the effective-date recompute
        // so TakenAt reflects the fresh dates.
        await RefreshLocationsForBlobAsync(blobObjectId, cancellationToken);
        return true;
    }

    // Probes one existing VIDEO blob's metadata row (ffprobe) and persists the
    // refreshed typed fields + dimensions. Returns false when no metadata row
    // exists. Driven by VideoMetadataBackfillService; the upload path does not
    // call it. Non-video blobs are marked video-skipped without touching bytes.
    // A container creation_time populates the shared DateTaken (so videos gain a
    // capture date), which then feeds the denormalized EffectiveDateTaken.
    public async Task<bool> ReExtractVideoMetadataAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken = default)
    {
        var meta = await _db.BlobMetadata
            .FirstOrDefaultAsync(m => m.BlobObjectId == blobObjectId, cancellationToken);
        if (meta is null)
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (meta.MediaCategory != MediaCategories.Video)
        {
            meta.VideoExtractionStatus = MetadataStatuses.Skipped;
            meta.VideoExtractionErrorCode = null;
            meta.VideoExtractionVersion = FfprobeVideoMetadataExtractor.Version;
            meta.VideoExtractedAt = now;
            meta.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        VideoMetadataExtractionResult result;
        try
        {
            result = await _videoMetadataExtractor.ExtractAsync(
                ct => _blobService.OpenContentAsync(blobObjectId, ct), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Defence in depth: the extractor contract says it never throws, but
            // a stream-open failure resolves to a sanitized transient failure so
            // the backfill always completes. No exception detail is logged.
            result = VideoMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.IoError, FfprobeVideoMetadataExtractor.Version);
        }

        ApplyVideoMetadata(meta, result, now);
        meta.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        // A newly-derived container date changes the effective capture date of
        // every file referencing the blob (used for gallery sort).
        if (meta.DateTaken is not null)
        {
            await RecomputeEffectiveDatesForBlobAsync(blobObjectId, meta.DateTaken, cancellationToken);
        }
        return true;
    }

    // Maps a video probe result onto a BlobMetadata row. Pure copy — no I/O.
    // On a non-success status the typed fields are cleared (a failed re-probe
    // must not leave stale values) but the row keeps its category/dimensions
    // contract via the extractor (which returns them only on success).
    private static void ApplyVideoMetadata(BlobMetadata meta, VideoMetadataExtractionResult r, DateTime now)
    {
        meta.VideoExtractionStatus = r.Status;
        meta.VideoExtractionErrorCode = r.ErrorCode;
        meta.VideoExtractionVersion = r.Version;
        meta.VideoExtractedAt = now;

        // Pixel dimensions live in the shared Width/Height/PixelCount columns.
        var (width, height, pixelCount) = BlobDimensions.Normalize(r.Width, r.Height);
        meta.Width = width;
        meta.Height = height;
        meta.PixelCount = pixelCount;

        meta.DurationSeconds = r.DurationSeconds;
        meta.VideoCodec = r.VideoCodec;
        meta.AudioCodec = r.AudioCodec;
        meta.FrameRate = r.FrameRate;
        meta.VideoBitrate = r.VideoBitrate;
        meta.HasAudio = r.HasAudio;
        meta.AudioChannels = r.AudioChannels;
        meta.AudioSampleRate = r.AudioSampleRate;
        meta.Rotation = r.Rotation;

        // Container creation time feeds the shared capture-date field, but only
        // when present — never clobber an existing DateTaken with null.
        if (r.CreationTime is DateTime taken)
        {
            meta.DateTaken = taken;
            meta.DateTakenSource = "video_creation_time";
        }
    }

    // Slice 94: rebuilds the FileItemLocation rows of every ACTIVE file
    // pointing at this blob from the blob's current metadata. Delete-then-
    // insert keeps the logic trivially idempotent; a blob has few referencing
    // files. Coordinates never leave this owner-scoped projection.
    private async Task RefreshLocationsForBlobAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        var meta = await _db.BlobMetadata
            .AsNoTracking()
            .Where(m => m.BlobObjectId == blobObjectId)
            .Select(m => new { m.Id, m.GpsLatitude, m.GpsLongitude, m.GpsAltitude })
            .FirstOrDefaultAsync(cancellationToken);
        var files = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.BlobObjectId == blobObjectId && f.DeletedAt == null)
            .Select(f => new { f.Id, f.OwnerUserId, f.EffectiveDateTaken })
            .ToListAsync(cancellationToken);

        var fileIds = files.Select(f => f.Id).ToList();
        await _db.FileItemLocations
            .Where(l => fileIds.Contains(l.FileItemId))
            .ExecuteDeleteAsync(cancellationToken);

        if (meta is { GpsLatitude: not null, GpsLongitude: not null } && files.Count > 0)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var f in files)
            {
                _db.FileItemLocations.Add(new FileItemLocation
                {
                    FileItemId = f.Id,
                    OwnerUserId = f.OwnerUserId,
                    Latitude = meta.GpsLatitude.Value,
                    Longitude = meta.GpsLongitude.Value,
                    Altitude = meta.GpsAltitude,
                    TakenAt = f.EffectiveDateTaken,
                    SourceBlobMetadataId = meta.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                foreach (var entry in _db.ChangeTracker.Entries<FileItemLocation>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
            catch (DbUpdateException)
            {
                // Lost a race with a concurrent refresh — its rows are as fresh.
                _db.ChangeTracker.Clear();
            }
        }
    }

    // Refresh the denormalized FileItem.EffectiveDateTaken for every active file
    // pointing at this blob whose effective date is NOT pinned by a user
    // override. Files WITH a non-null DateTakenOverride are intentionally
    // skipped: their effective date is the override regardless of the blob's
    // embedded date. Set-based — one UPDATE, no per-row round-trips. The blob's
    // (possibly null) embedded date is passed as a constant.
    private async Task RecomputeEffectiveDatesForBlobAsync(
        Guid blobObjectId, DateTime? blobDateTaken, CancellationToken cancellationToken)
    {
        await _db.FileItems
            .Where(f => f.BlobObjectId == blobObjectId
                && f.DeletedAt == null
                && !_db.FileItemUserMetadata.Any(u =>
                    u.FileItemId == f.Id && u.DateTakenOverride != null))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.EffectiveDateTaken, f => blobDateTaken ?? f.CreatedAt)
                    .SetProperty(f => f.EffectiveDateTakenSource,
                        _ => blobDateTaken != null
                            ? EffectiveDateTakenSources.Embedded
                            : EffectiveDateTakenSources.Uploaded),
                cancellationToken);
    }

    // Slice 75: all active FileItems owned by the same user that point to the
    // same blob as the given fileItemId. The blob identity is determined server-
    // side from the fileItemId → never exposed in the response. Returns null
    // when the fileItemId is missing / foreign / soft-deleted (→ 404).
    public async Task<IReadOnlyList<DuplicateOccurrence>?> ListDuplicateOccurrencesAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // Owner-scoped lookup. Soft-deleted → null → 404. No-leak: missing
        // and foreign look identical.
        var blobId = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (blobId is null)
        {
            return null;
        }

        return await _db.FileItems
            .AsNoTracking()
            .Where(f => f.BlobObjectId == blobId.Value
                     && f.OwnerUserId == ownerUserId
                     && f.DeletedAt == null)
            .OrderBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .Select(f => new DuplicateOccurrence(
                f.Id,
                f.Name,
                f.ParentFolderId,
                f.MimeType,
                f.SizeBytes,
                f.CreatedAt,
                f.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}

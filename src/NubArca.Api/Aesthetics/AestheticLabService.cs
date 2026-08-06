using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Security;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Aesthetics;

public sealed class AestheticLabService : IAestheticLabService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IFileItemService _files;
    private readonly IJobQueue _jobs;
    private readonly TimeProvider _clock;
    private readonly ImageDerivativeRenderer _renderer;
    private readonly ImageProcessingOptions _imageOptions;
    private readonly MediaDerivativesOptions _mediaOptions;
    private readonly AestheticsOptions _options;
    private readonly ILogger<AestheticLabService> _logger;

    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    public AestheticLabService(
        AppDbContext db,
        IBlobService blobs,
        IFileItemService files,
        IJobQueue jobs,
        TimeProvider clock,
        ILogger<AestheticLabService> logger,
        ImageDerivativeRenderer? renderer = null,
        IOptions<ImageProcessingOptions>? imageOptions = null,
        IOptions<MediaDerivativesOptions>? mediaOptions = null,
        IOptions<AestheticsOptions>? options = null)
    {
        _db = db;
        _blobs = blobs;
        _files = files;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
        _renderer = renderer ?? ImageDerivativeRenderer.ImageSharpOnly();
        _imageOptions = imageOptions?.Value ?? new ImageProcessingOptions();
        _mediaOptions = mediaOptions?.Value ?? new MediaDerivativesOptions();
        _options = options?.Value ?? new AestheticsOptions();
    }

    public async Task<AestheticLabItemDto> AddFromGalleryAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
    {
        // Per-item authorization = the exact gallery listing rule (owner, active,
        // server-detected image, media-library visible, not vault-filtered).
        if (!await _files.IsGalleryImageAsync(ownerUserId, fileItemId, cancellationToken))
        {
            throw new AestheticLabValidationException(AestheticLabValidationException.NotAnImage);
        }
        var file = await _files.GetByIdAsync(fileItemId, ownerUserId, cancellationToken);
        if (file is null)
        {
            throw new AestheticLabValidationException(AestheticLabValidationException.NotAnImage);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Idempotent: reuse an existing ACTIVE item for the same owner/blob.
            var existing = await _db.AestheticLabItems
                .FirstOrDefaultAsync(i => i.OwnerUserId == ownerUserId
                    && i.BlobObjectId == file.BlobObjectId, cancellationToken);
            if (existing is not null)
            {
                await tx.CommitAsync(cancellationToken);
                return await ToListDtoAsync(existing, cancellationToken);
            }

            // Acquire ONE additional reference to the EXISTING gallery blob — no
            // bytes copied. The increment runs INSIDE this transaction, so any
            // failure below is undone atomically by the rollback — no separate
            // release (a manual release would double-decrement the source file's
            // reference).
            try
            {
                await _blobs.AcquireExistingAsync(file.BlobObjectId, cancellationToken);

                var now = _clock.GetUtcNow().UtcDateTime;
                var item = new AestheticLabItem
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    BlobObjectId = file.BlobObjectId,
                    SourceFileItemId = fileItemId,
                    OriginalFileName = SanitizeFileName(file.Name),
                    ContentType = SafeContentType.IsTrustedImage(file.MimeType) ? file.MimeType : "image/jpeg",
                    SizeBytes = file.SizeBytes,
                    Width = file.Width,
                    Height = file.Height,
                    LogicalContainerKey = AestheticContainerKey.Compute(_options.Pepper, ownerUserId),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.AestheticLabItems.Add(item);
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return ToListDtoNoRun(item);
            }
            catch
            {
                // Rollback undoes both the item insert AND the reference
                // increment (both are in this transaction). No manual release.
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    public async Task<AestheticLabItemDto> AddFromUploadAsync(
        Guid ownerUserId, string? fileName, string? clientContentType, Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Reuse the bounded streaming/hash/dedup store (refcount++). Everything
        // after this releases the reference on any failure.
        var blob = await _blobs.StoreAsync(content, cancellationToken);
        try
        {
            if (blob.SizeBytes > _options.MaxUploadBytes)
            {
                throw new AestheticLabValidationException(AestheticLabValidationException.TooLarge);
            }

            ImageInfo? info;
            try
            {
                await using var probe = await _blobs.OpenContentAsync(blob.Id, cancellationToken);
                info = await Image.IdentifyAsync(probe, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (ImageFormatException)
            {
                throw new AestheticLabValidationException(AestheticLabValidationException.NotAnImage);
            }
            if (info is null)
            {
                throw new AestheticLabValidationException(AestheticLabValidationException.NotAnImage);
            }

            var detectedContentType = info.Metadata.DecodedImageFormat?.DefaultMimeType;
            if (!SafeContentType.IsTrustedImage(detectedContentType))
            {
                throw new AestheticLabValidationException(AestheticLabValidationException.NotAnImage);
            }
            var pixels = (long)info.Width * info.Height;
            if (info.Width > _imageOptions.MaxWidth
                || info.Height > _imageOptions.MaxHeight
                || pixels > _imageOptions.MaxPixels)
            {
                throw new AestheticLabValidationException(AestheticLabValidationException.DimensionsTooLarge);
            }

            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

                // If the same bytes already back an active lab item (dedup hit),
                // reuse it and release the extra reference StoreAsync acquired.
                var existing = await _db.AestheticLabItems
                    .FirstOrDefaultAsync(i => i.OwnerUserId == ownerUserId
                        && i.BlobObjectId == blob.Id, cancellationToken);
                if (existing is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                    await TryReleaseQuietlyAsync(blob.Id);
                    return await ToListDtoAsync(existing, cancellationToken);
                }

                var now = _clock.GetUtcNow().UtcDateTime;
                var item = new AestheticLabItem
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    BlobObjectId = blob.Id,
                    SourceFileItemId = null,
                    OriginalFileName = SanitizeFileName(fileName),
                    ContentType = detectedContentType!,
                    SizeBytes = blob.SizeBytes,
                    Width = info.Width,
                    Height = info.Height,
                    LogicalContainerKey = AestheticContainerKey.Compute(_options.Pepper, ownerUserId),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.AestheticLabItems.Add(item);
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return ToListDtoNoRun(item);
            });
        }
        catch
        {
            await TryReleaseQuietlyAsync(blob.Id);
            throw;
        }
    }

    public async Task<AestheticLabPageDto> ListAsync(
        Guid ownerUserId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var pageSize = limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize);

        var query = _db.AestheticLabItems.AsNoTracking()
            .Where(i => i.OwnerUserId == ownerUserId);

        if (TryDecodeCursor(cursor, out var afterCreated, out var afterId))
        {
            query = query.Where(i =>
                i.CreatedAt < afterCreated || (i.CreatedAt == afterCreated && i.Id.CompareTo(afterId) < 0));
        }

        var rows = await query
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        string? next = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            next = EncodeCursor(last.CreatedAt, last.Id);
            rows = rows.Take(pageSize).ToList();
        }

        var latest = await LoadLatestRunInfoAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var items = rows.Select(r =>
        {
            latest.TryGetValue(r.Id, out var info);
            return ToListDto(r, info);
        }).ToList();

        return new AestheticLabPageDto(items, next);
    }

    public async Task<AestheticLabItemDetailDto?> GetDetailAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var item = await _db.AestheticLabItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.OwnerUserId == ownerUserId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var runs = await _db.AestheticAnalysisRuns.AsNoTracking()
            .Where(r => r.AestheticLabItemId == id && r.OwnerUserId == ownerUserId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        AestheticRunDto? latestRun = null;
        var history = new List<AestheticRunSummaryDto>();
        if (runs.Count > 0)
        {
            var latest = runs[0];
            latestRun = await LoadRunDtoAsync(latest, cancellationToken);
            foreach (var r in runs)
            {
                var overall = await LoadOverallAsync(r.Id, cancellationToken);
                history.Add(new AestheticRunSummaryDto(
                    r.Id, r.Status, r.CreatedAt, r.CompletedAt, r.DurationMs, r.ErrorCode,
                    SplitCsv(r.CompletedCapabilities), overall));
            }
        }

        return new AestheticLabItemDetailDto(
            item.Id, item.OriginalFileName, item.ContentType, item.SizeBytes,
            item.Width, item.Height, item.CreatedAt, PreviewUrl(item.Id),
            latestRun, history);
    }

    public async Task<bool> RemoveAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        // Best-effort cancel any live analysis BEFORE we purge the rows, so the
        // worker doesn't keep grinding on a deleted run. The handler treats a
        // vanished run as a safe no-op regardless.
        var liveJobIds = await _db.AestheticAnalysisRuns.AsNoTracking()
            .Where(r => r.AestheticLabItemId == id && r.OwnerUserId == ownerUserId
                && (r.Status == AestheticRunStatuses.Queued || r.Status == AestheticRunStatuses.Running)
                && r.BackgroundJobId != null)
            .Select(r => r.BackgroundJobId!.Value)
            .ToListAsync(cancellationToken);
        foreach (var jobId in liveJobIds)
        {
            await _jobs.RequestCancellationAsync(jobId, cancellationToken);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // AsNoTracking: RemoveAsync deletes set-based (ExecuteDelete) rather
            // than via the change tracker, so a same-scope instance already
            // tracked from an earlier Add cannot trigger a spurious concurrency
            // exception.
            var item = await _db.AestheticLabItems.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id && i.OwnerUserId == ownerUserId, cancellationToken);
            if (item is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Derived-rendition blob references this item owns; their rows go
            // explicitly and the blob REFERENCES must be released too.
            var derivativeBlobIds = await _db.AestheticLabDerivatives.AsNoTracking()
                .Where(d => d.AestheticLabItemId == id && d.OwnerUserId == ownerUserId)
                .Select(d => d.BlobObjectId)
                .ToListAsync(cancellationToken);

            var itemBlobId = item.BlobObjectId;

            // HARD delete (Targhe-style; no soft-delete/Trash/restore). Delete
            // children FIRST — runs cascade their metrics/text; derivative rows go
            // explicitly — then physically delete the item, then release every
            // reference in the SAME transaction so a rollback restores both the
            // rows AND the refcounts (no leak, no undercount). Order (row-delete
            // before release) matches PlateImageService: with the rows gone, a
            // blob that drops to zero references is immediately janitor-eligible.
            await _db.AestheticLabDerivatives
                .Where(d => d.AestheticLabItemId == id && d.OwnerUserId == ownerUserId)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.AestheticAnalysisRuns
                .Where(r => r.AestheticLabItemId == id && r.OwnerUserId == ownerUserId)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.AestheticLabItems
                .Where(i => i.Id == id && i.OwnerUserId == ownerUserId)
                .ExecuteDeleteAsync(cancellationToken);

            // Release each derivative reference exactly once, then the item ref.
            foreach (var derivedBlobId in derivativeBlobIds)
            {
                await _blobs.ReleaseAsync(derivedBlobId, cancellationToken);
            }
            await _blobs.ReleaseAsync(itemBlobId, cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<AestheticDerivativeContent?> RenderDerivativeAsync(
        Guid ownerUserId, Guid id, string size, CancellationToken cancellationToken = default)
    {
        if (!IsLabDerivativeSize(size))
        {
            return null;
        }
        var normalized = ThumbnailSizes.Normalize(size);

        var item = await _db.AestheticLabItems.AsNoTracking()
            .Where(i => i.Id == id && i.OwnerUserId == ownerUserId)
            .Select(i => new { i.BlobObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        // Serve a persisted derivative if present + its bytes still exist.
        var existing = await _db.AestheticLabDerivatives.AsNoTracking()
            .FirstOrDefaultAsync(d => d.AestheticLabItemId == id && d.OwnerUserId == ownerUserId && d.Size == normalized, cancellationToken);
        if (existing is not null)
        {
            var cached = await _blobs.OpenDerivedContentAsync(existing.BlobObjectId, cancellationToken);
            if (cached is not null)
            {
                await using (cached)
                {
                    using var buf = new MemoryStream();
                    await cached.CopyToAsync(buf, cancellationToken);
                    return new AestheticDerivativeContent(buf.ToArray(), existing.ContentType);
                }
            }
            // Bytes were wiped — regenerate below and update the row.
        }

        var rendered = await RenderDerivativeBytesAsync(item.BlobObjectId, normalized, id, cancellationToken);
        if (rendered is null)
        {
            return null;
        }

        // Persist the derivative (own its derived blob reference) so the audit
        // counts it. Store bytes first, then upsert the row.
        Guid derivedBlobId;
        await using (var ms = new MemoryStream(rendered.Jpeg, writable: false))
        {
            var derived = await _blobs.StoreDerivedAsync(ms, cancellationToken);
            derivedBlobId = derived.Id;
        }
        try
        {
            await UpsertDerivativeRowAsync(ownerUserId, id, normalized, derivedBlobId, rendered, cancellationToken);
        }
        catch
        {
            await TryReleaseQuietlyAsync(derivedBlobId);
            // Still serve the freshly rendered bytes even if row persistence lost
            // a race; the winning row owns the reference.
        }

        return new AestheticDerivativeContent(rendered.Jpeg, "image/jpeg");
    }

    private async Task UpsertDerivativeRowAsync(
        Guid ownerUserId, Guid itemId, string size, Guid derivedBlobId,
        RenderedDerivative rendered, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var current = await _db.AestheticLabDerivatives
                .FirstOrDefaultAsync(d => d.AestheticLabItemId == itemId && d.OwnerUserId == ownerUserId && d.Size == size, cancellationToken);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (current is null)
            {
                _db.AestheticLabDerivatives.Add(new AestheticLabDerivative
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    AestheticLabItemId = itemId,
                    Size = size,
                    BlobObjectId = derivedBlobId,
                    ContentType = "image/jpeg",
                    Width = rendered.Width,
                    Height = rendered.Height,
                    SizeBytes = rendered.Jpeg.LongLength,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            else
            {
                // A prior row existed but its bytes were wiped: repoint to the new
                // derived blob and release the stale reference.
                var stale = current.BlobObjectId;
                current.BlobObjectId = derivedBlobId;
                current.Width = rendered.Width;
                current.Height = rendered.Height;
                current.SizeBytes = rendered.Jpeg.LongLength;
                await _db.SaveChangesAsync(cancellationToken);
                if (stale != derivedBlobId)
                {
                    await _blobs.ReleaseAsync(stale, cancellationToken);
                }
                await tx.CommitAsync(cancellationToken);
            }
        });
    }

    // ---- rendering (mirrors PlateImageService defense-in-depth gates) --------

    private async Task<RenderedDerivative?> RenderDerivativeBytesAsync(
        Guid blobObjectId, string normalizedSize, Guid itemId, CancellationToken cancellationToken)
    {
        var sourceSize = await _db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobObjectId)
            .Select(b => (long?)b.SizeBytes)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceSize is long bytes && bytes > _imageOptions.MaxThumbnailInputBytes)
        {
            return null;
        }

        try
        {
            ImageInfo? info;
            await using (var probe = await _blobs.OpenContentAsync(blobObjectId, cancellationToken))
            {
                info = await Image.IdentifyAsync(probe, cancellationToken);
            }
            if (info is null)
            {
                return null;
            }
            var pixels = (long)info.Width * info.Height;
            if (info.Width > _imageOptions.MaxWidth
                || info.Height > _imageOptions.MaxHeight
                || pixels > _imageOptions.MaxPixels)
            {
                return null;
            }

            var source = await ReadSourceBytesAsync(blobObjectId, cancellationToken);
            var requests = new[]
            {
                new DerivativeRequest(normalizedSize, _mediaOptions.EdgeFor(normalizedSize), _mediaOptions.QualityFor(normalizedSize)),
            };
            var render = await _renderer.RenderAsync(source, requests, cancellationToken);
            return render.Results[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aesthetic derivative render failed for item {ItemId}.", itemId);
            return null;
        }
    }

    private async Task<byte[]> ReadSourceBytesAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // ---- projection helpers -------------------------------------------------

    private async Task<AestheticLabItemDto> ToListDtoAsync(AestheticLabItem item, CancellationToken cancellationToken)
    {
        var latest = await LoadLatestRunInfoAsync(new List<Guid> { item.Id }, cancellationToken);
        latest.TryGetValue(item.Id, out var info);
        return ToListDto(item, info);
    }

    private static AestheticLabItemDto ToListDtoNoRun(AestheticLabItem item) => ToListDto(item, null);

    private static AestheticLabItemDto ToListDto(AestheticLabItem item, LatestRunInfo? info) => new(
        item.Id,
        item.OriginalFileName,
        item.ContentType,
        item.SizeBytes,
        item.Width,
        item.Height,
        item.CreatedAt,
        info?.Status,
        info?.ErrorCode,
        info?.OverallScore,
        info?.ProfileKey ?? string.Empty,
        ThumbnailUrl(item.Id),
        PreviewUrl(item.Id));

    private sealed record LatestRunInfo(string Status, string? ErrorCode, double? OverallScore, string ProfileKey);

    private async Task<Dictionary<Guid, LatestRunInfo>> LoadLatestRunInfoAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, LatestRunInfo>();
        if (itemIds.Count == 0)
        {
            return result;
        }
        // Latest run per item (small pages, so a per-item top-1 is fine).
        var runs = await _db.AestheticAnalysisRuns.AsNoTracking()
            .Where(r => itemIds.Contains(r.AestheticLabItemId))
            .Select(r => new { r.Id, r.AestheticLabItemId, r.Status, r.ErrorCode, r.ProfileKey, r.CreatedAt })
            .ToListAsync(cancellationToken);
        var latestByItem = runs
            .GroupBy(r => r.AestheticLabItemId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).First())
            .ToList();

        var latestRunIds = latestByItem.Select(r => r.Id).ToList();
        var overalls = await _db.AestheticMetrics.AsNoTracking()
            .Where(m => latestRunIds.Contains(m.RunId) && m.MetricKey == AestheticMetricCatalog.OverallKey)
            .Select(m => new { m.RunId, m.NumericValue })
            .ToListAsync(cancellationToken);
        var overallByRun = overalls.ToDictionary(x => x.RunId, x => (double?)x.NumericValue);

        foreach (var r in latestByItem)
        {
            overallByRun.TryGetValue(r.Id, out var overall);
            result[r.AestheticLabItemId] = new LatestRunInfo(r.Status, r.ErrorCode, overall, r.ProfileKey);
        }
        return result;
    }

    private async Task<double?> LoadOverallAsync(Guid runId, CancellationToken cancellationToken)
    {
        return await _db.AestheticMetrics.AsNoTracking()
            .Where(m => m.RunId == runId && m.MetricKey == AestheticMetricCatalog.OverallKey)
            .Select(m => (double?)m.NumericValue)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AestheticRunDto> LoadRunDtoAsync(AestheticAnalysisRun run, CancellationToken cancellationToken)
    {
        var metrics = await _db.AestheticMetrics.AsNoTracking()
            .Where(m => m.RunId == run.Id)
            .OrderBy(m => m.MetricKey)
            .Select(m => new AestheticMetricDto(m.MetricKey, m.MetricGroup, m.NumericValue, m.ScaleMin, m.ScaleMax, m.Confidence, m.MetricVersion))
            .ToListAsync(cancellationToken);
        var texts = await _db.AestheticTextResults.AsNoTracking()
            .Where(t => t.RunId == run.Id)
            .OrderBy(t => t.TextKind)
            .Select(t => new AestheticTextDto(t.TextKind, t.Language, t.Text, t.PromptTemplateVersion))
            .ToListAsync(cancellationToken);

        return new AestheticRunDto(
            run.Id, run.Status, run.ProfileKey, run.ModelName, run.ModelRevision,
            run.RuntimeName, run.RuntimeVersion, run.PreprocessingProfileKey,
            SplitCsv(run.RequestedCapabilities), SplitCsv(run.CompletedCapabilities),
            run.CreatedAt, run.StartedAt, run.CompletedAt, run.DurationMs, run.ErrorCode,
            DeserializeWarnings(run.WarningsJson), metrics, texts);
    }

    private async Task TryReleaseQuietlyAsync(Guid blobObjectId)
    {
        try
        {
            await _blobs.ReleaseAsync(blobObjectId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release aesthetic lab blob reference after a failed operation.");
        }
    }

    private static bool IsLabDerivativeSize(string? size) =>
        string.Equals(size, ThumbnailSizes.Small, StringComparison.OrdinalIgnoreCase)
        || string.Equals(size, ThumbnailSizes.Medium, StringComparison.OrdinalIgnoreCase);

    private static string ThumbnailUrl(Guid id) => $"/api/aesthetics-lab/items/{id}/thumbnail?size=small";
    private static string PreviewUrl(Guid id) => $"/api/aesthetics-lab/items/{id}/preview";

    internal static IReadOnlyList<string> SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "image";
        }
        var justName = Path.GetFileName(name.Trim());
        var cleaned = new string((justName ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "image";
        }
        return cleaned.Length > 512 ? cleaned[..512] : cleaned;
    }

    // ---- cursor (keyset over CreatedAt desc, Id desc) -----------------------

    private static string EncodeCursor(DateTime createdAt, Guid id)
    {
        var payload = $"{createdAt.Ticks}:{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeCursor(string? cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParseExact(parts[1], "N", out id))
            {
                return false;
            }
            createdAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

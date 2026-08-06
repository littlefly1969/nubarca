using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;

namespace NubArca.Api.Plates.Redaction;

public sealed class PlateRedactedMediaService : IPlateRedactedMediaService
{
    private readonly AppDbContext _db;
    private readonly IPlateImageService _plateImages;
    private readonly IPlateFaceRedactionService _redaction;
    private readonly ImageRedactionRenderer _renderer;
    private readonly IBlobService _blobs;
    private readonly TimeProvider _clock;
    private readonly PlatesFaceRedactionOptions _options;
    private readonly MediaDerivativesOptions _mediaOptions;
    private readonly ILogger<PlateRedactedMediaService> _logger;

    private const string RedactedContentType = "image/jpeg";

    public PlateRedactedMediaService(
        AppDbContext db,
        IPlateImageService plateImages,
        IPlateFaceRedactionService redaction,
        ImageRedactionRenderer renderer,
        IBlobService blobs,
        TimeProvider clock,
        ILogger<PlateRedactedMediaService> logger,
        IOptions<PlatesFaceRedactionOptions>? options = null,
        IOptions<MediaDerivativesOptions>? mediaOptions = null)
    {
        _db = db;
        _plateImages = plateImages;
        _redaction = redaction;
        _renderer = renderer;
        _blobs = blobs;
        _clock = clock;
        _logger = logger;
        _options = options?.Value ?? new PlatesFaceRedactionOptions();
        _mediaOptions = mediaOptions?.Value ?? new MediaDerivativesOptions();
    }

    public async Task<PlateRedactedContent?> GetAsync(
        Guid ownerUserId, Guid plateImageId, PlateRedactionSourceKind kind,
        CancellationToken cancellationToken = default)
    {
        // Never silently serve the unredacted image: with redaction disabled or
        // no runnable detector, refuse with a safe 409.
        if (!_redaction.IsAvailable)
        {
            throw new PlateFaceRedactionUnavailableException();
        }

        // Cheap owner-scoped existence check (a foreign/missing id → 404).
        var exists = await _db.PlateImages.AsNoTracking()
            .AnyAsync(p => p.Id == plateImageId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (!exists)
        {
            return null;
        }

        // Ensure privacy boxes exist for the current profile (detector runs once
        // per image/profile, on the lazily-loaded original). A (re)generation
        // invalidates any stale cached renditions.
        var ensure = await _redaction.EnsureBoxesAsync(
            ownerUserId, plateImageId,
            ct => LoadOriginalInputAsync(ownerUserId, plateImageId, ct),
            cancellationToken);
        if (ensure.Regenerated)
        {
            await InvalidateCacheAsync(ownerUserId, plateImageId, cancellationToken);
        }

        var sourceKind = ToSourceKind(kind);

        // Cache hit → serve the stored derived bytes without re-rendering.
        if (_options.CacheEnabled)
        {
            var hit = await _db.PlateRedactedMedia
                .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId
                    && m.PlateImageId == plateImageId
                    && m.SourceKind == sourceKind
                    && m.BlurFaces
                    && m.ProfileKey == _options.ProfileKey
                    && m.RedactionMode == _options.Mode
                    && m.PixelBlockSize == _options.PixelBlockSize, cancellationToken);
            if (hit is not null)
            {
                var cached = await ReadDerivedAsync(hit.BlobObjectId, cancellationToken);
                if (cached is not null)
                {
                    return new PlateRedactedContent(cached, hit.ContentType, hit.Width, hit.Height);
                }
                // Derived bytes gone (cache wiped) — drop the stale row + ref and
                // fall through to re-render.
                await DeleteCacheRowAsync(hit, cancellationToken);
            }
        }

        // Render: load the target rendition (small/medium derivative or the
        // original), enforce the pixel ceiling, then redact.
        var source = await _plateImages.OpenRedactionSourceAsync(ownerUserId, plateImageId, kind, cancellationToken);
        if (source is null)
        {
            return null;
        }
        if ((long)source.Width * source.Height > _options.MaxImagePixels)
        {
            throw new PlateRedactionImageTooLargeException();
        }

        var boxes = ensure.Boxes
            .Select(b => new ImageRedactionRenderer.NormalizedBox(
                b.BoundingBoxX, b.BoundingBoxY, b.BoundingBoxWidth, b.BoundingBoxHeight))
            .ToList();

        ImageRedactionRenderer.RedactedImage redacted;
        try
        {
            redacted = _renderer.Render(
                source.Bytes, boxes, _options.BoxExpansionRatio, _options.PixelBlockSize,
                _mediaOptions.QualityFor(ThumbnailSizes.Medium));
        }
        catch (Exception ex)
        {
            // Unrenderable/pathological source — treat as not found rather than
            // leaking anything. (Never returns the unredacted image.)
            _logger.LogWarning(ex, "Plate redaction render failed for a plate image.");
            return null;
        }

        if (_options.CacheEnabled)
        {
            await StoreCacheAsync(ownerUserId, plateImageId, sourceKind, redacted, cancellationToken);
        }

        return new PlateRedactedContent(redacted.Jpeg, RedactedContentType, redacted.Width, redacted.Height);
    }

    private async Task<PlateRedactionImageInput?> LoadOriginalInputAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken)
    {
        var original = await _plateImages.OpenRedactionSourceAsync(
            ownerUserId, plateImageId, PlateRedactionSourceKind.Original, cancellationToken);
        return original is null
            ? null
            : new PlateRedactionImageInput(original.Bytes, original.Width, original.Height);
    }

    private async Task<byte[]?> ReadDerivedAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        var stream = await _blobs.OpenDerivedContentAsync(blobObjectId, cancellationToken);
        if (stream is null)
        {
            return null;
        }
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
    }

    private async Task StoreCacheAsync(
        Guid ownerUserId, Guid plateImageId, string sourceKind,
        ImageRedactionRenderer.RedactedImage redacted, CancellationToken cancellationToken)
    {
        // Store the redacted bytes in the shared content-addressed derived store
        // (dedup + refcount++). Registered with BlobReferenceAuditService, so
        // repair never zeroes this live reference.
        BlobObject blob;
        await using (var ms = new MemoryStream(redacted.Jpeg, writable: false))
        {
            blob = await _blobs.StoreDerivedAsync(ms, cancellationToken);
        }

        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

                // Replace any prior row for this exact key (e.g. a concurrent
                // render), releasing its reference so the cache stays single-row.
                var stale = await _db.PlateRedactedMedia
                    .Where(m => m.OwnerUserId == ownerUserId
                        && m.PlateImageId == plateImageId
                        && m.SourceKind == sourceKind
                        && m.ProfileKey == _options.ProfileKey
                        && m.RedactionMode == _options.Mode
                        && m.PixelBlockSize == _options.PixelBlockSize)
                    .ToListAsync(cancellationToken);
                var staleBlobIds = stale.Select(s => s.BlobObjectId).ToList();
                if (stale.Count > 0)
                {
                    _db.PlateRedactedMedia.RemoveRange(stale);
                }

                _db.PlateRedactedMedia.Add(new PlateRedactedMedia
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    PlateImageId = plateImageId,
                    SourceKind = sourceKind,
                    BlurFaces = true,
                    ProfileKey = _options.ProfileKey,
                    RedactionMode = _options.Mode,
                    PixelBlockSize = _options.PixelBlockSize,
                    BlobObjectId = blob.Id,
                    ContentType = RedactedContentType,
                    SizeBytes = redacted.Jpeg.LongLength,
                    Width = redacted.Width,
                    Height = redacted.Height,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);

                foreach (var staleBlobId in staleBlobIds)
                {
                    if (staleBlobId != blob.Id)
                    {
                        await _blobs.ReleaseAsync(staleBlobId, cancellationToken);
                    }
                }

                await tx.CommitAsync(cancellationToken);
            });
        }
        catch (Exception ex)
        {
            // Row persist failed — undo the reference we just acquired so the
            // orphaned derived blob becomes janitor-eligible. Best-effort.
            _logger.LogWarning(ex, "Failed to persist redacted-media cache row; releasing the derived blob.");
            try
            {
                await _blobs.ReleaseAsync(blob.Id, CancellationToken.None);
            }
            catch (Exception releaseEx)
            {
                _logger.LogWarning(releaseEx, "Failed to release redacted-media blob after a cache persist failure.");
            }
        }
    }

    private async Task InvalidateCacheAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken)
    {
        var rows = await _db.PlateRedactedMedia
            .Where(m => m.OwnerUserId == ownerUserId && m.PlateImageId == plateImageId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var blobIds = rows.Select(r => r.BlobObjectId).ToList();
        _db.PlateRedactedMedia.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var blobId in blobIds)
        {
            await _blobs.ReleaseAsync(blobId, cancellationToken);
        }
    }

    private async Task DeleteCacheRowAsync(PlateRedactedMedia row, CancellationToken cancellationToken)
    {
        _db.PlateRedactedMedia.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        await _blobs.ReleaseAsync(row.BlobObjectId, cancellationToken);
    }

    private static string ToSourceKind(PlateRedactionSourceKind kind) => kind switch
    {
        PlateRedactionSourceKind.Thumbnail => PlateRedactionSourceKinds.Thumbnail,
        PlateRedactionSourceKind.Preview => PlateRedactionSourceKinds.Preview,
        PlateRedactionSourceKind.Original => PlateRedactionSourceKinds.Original,
        _ => PlateRedactionSourceKinds.Preview,
    };
}

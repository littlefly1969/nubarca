using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Plates.Redaction;
using NubArca.Api.Security;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Plates;

public sealed class PlateImageService : IPlateImageService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IFileItemService _files;
    private readonly TimeProvider _clock;
    private readonly ImageDerivativeRenderer _renderer;
    private readonly ImageProcessingOptions _imageOptions;
    private readonly MediaDerivativesOptions _mediaOptions;
    private readonly PlatesOptions _plateOptions;
    private readonly ILogger<PlateImageService> _logger;

    private readonly IPlateAnalysisService _analysis;
    private readonly IPlateFaceRedactionService? _redaction;

    public PlateImageService(
        AppDbContext db,
        IBlobService blobs,
        IFileItemService files,
        TimeProvider clock,
        ILogger<PlateImageService> logger,
        IPlateAnalysisService analysis,
        IPlateFaceRedactionService? redaction = null,
        ImageDerivativeRenderer? renderer = null,
        IOptions<ImageProcessingOptions>? imageOptions = null,
        IOptions<MediaDerivativesOptions>? mediaOptions = null,
        IOptions<PlatesOptions>? plateOptions = null)
    {
        _db = db;
        _blobs = blobs;
        _files = files;
        _clock = clock;
        _logger = logger;
        _analysis = analysis;
        _redaction = redaction;
        _renderer = renderer ?? ImageDerivativeRenderer.ImageSharpOnly();
        _imageOptions = imageOptions?.Value ?? new ImageProcessingOptions();
        _mediaOptions = mediaOptions?.Value ?? new MediaDerivativesOptions();
        _plateOptions = plateOptions?.Value ?? new PlatesOptions();
    }

    public async Task<PlateImageListItem> CreateFromUploadAsync(
        Guid ownerUserId,
        string? fileName,
        string? clientContentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Store + dedup + refcount++ up front (streams to a temp file, hashes,
        // never buffers the whole image in memory). Everything after this must
        // release the reference on any failure so an invalid upload never leaks
        // a pinned blob.
        var blob = await _blobs.StoreAsync(content, cancellationToken);
        try
        {
            if (blob.SizeBytes > _plateOptions.MaxUploadBytes)
            {
                throw new PlateImageValidationException(PlateImageValidationException.TooLarge);
            }

            // Header-only decode: gets dimensions + the real format without
            // allocating the pixel buffer, so a decompression bomb cannot
            // exhaust memory here.
            ImageInfo? info;
            try
            {
                await using var probe = await _blobs.OpenContentAsync(blob.Id, cancellationToken);
                info = await Image.IdentifyAsync(probe, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ImageFormatException)
            {
                // Unknown format or corrupt/invalid content — not a usable image.
                throw new PlateImageValidationException(PlateImageValidationException.NotAnImage);
            }

            if (info is null)
            {
                throw new PlateImageValidationException(PlateImageValidationException.NotAnImage);
            }

            var detectedContentType = info.Metadata.DecodedImageFormat?.DefaultMimeType;
            if (!SafeContentType.IsTrustedImage(detectedContentType))
            {
                throw new PlateImageValidationException(PlateImageValidationException.NotAnImage);
            }

            var pixels = (long)info.Width * info.Height;
            if (info.Width > _imageOptions.MaxWidth
                || info.Height > _imageOptions.MaxHeight
                || pixels > _imageOptions.MaxPixels)
            {
                throw new PlateImageValidationException(PlateImageValidationException.DimensionsTooLarge);
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var plate = new PlateImage
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                BlobObjectId = blob.Id,
                OriginalFileName = SanitizeFileName(fileName),
                ContentType = detectedContentType!,
                SizeBytes = blob.SizeBytes,
                Width = info.Width,
                Height = info.Height,
                LogicalContainerKey = PlateContainerKey.Compute(_plateOptions.Pepper, ownerUserId),
                Status = PlateImageStatuses.Uploaded,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.PlateImages.Add(plate);
            await _db.SaveChangesAsync(cancellationToken);

            // A freshly-uploaded plate has no detections yet.
            return ToListItem(plate, platesCount: 0);
        }
        catch
        {
            // Undo the reference we acquired so the orphaned blob becomes
            // janitor-eligible. Best-effort: a release failure must not mask the
            // original error.
            await TryReleaseQuietlyAsync(blob.Id);
            throw;
        }
    }

    public async Task<PlateImageListItem> AddFromGalleryAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
    {
        // Per-item authorization = the exact gallery listing rule (owner, active,
        // server-detected image, media-library visible, not vault-filtered).
        // Missing/foreign/non-image all collapse to NotAnImage (no existence leak).
        if (!await _files.IsGalleryImageAsync(ownerUserId, fileItemId, cancellationToken))
        {
            throw new PlateImageValidationException(PlateImageValidationException.NotAnImage);
        }
        var file = await _files.GetByIdAsync(fileItemId, ownerUserId, cancellationToken);
        if (file is null)
        {
            throw new PlateImageValidationException(PlateImageValidationException.NotAnImage);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Idempotent: reuse an existing ACTIVE plate for the same owner/blob.
            var existing = await _db.PlateImages
                .FirstOrDefaultAsync(p => p.OwnerUserId == ownerUserId
                    && p.BlobObjectId == file.BlobObjectId, cancellationToken);
            if (existing is not null)
            {
                await tx.CommitAsync(cancellationToken);
                var count = await _analysis.CountDetectionsForImagesAsync(
                    ownerUserId, new List<Guid> { existing.Id }, cancellationToken);
                return ToListItem(existing, count.TryGetValue(existing.Id, out var c) ? c : 0);
            }

            // Acquire ONE additional reference to the EXISTING gallery blob — no
            // bytes copied. The increment runs INSIDE this transaction, so any
            // failure below is undone atomically by the rollback (no manual
            // release — that would double-decrement the source file's reference).
            try
            {
                await _blobs.AcquireExistingAsync(file.BlobObjectId, cancellationToken);

                var now = _clock.GetUtcNow().UtcDateTime;
                var plate = new PlateImage
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    BlobObjectId = file.BlobObjectId,
                    OriginalFileName = SanitizeFileName(file.Name),
                    ContentType = SafeContentType.IsTrustedImage(file.MimeType) ? file.MimeType : "image/jpeg",
                    SizeBytes = file.SizeBytes,
                    Width = file.Width,
                    Height = file.Height,
                    LogicalContainerKey = PlateContainerKey.Compute(_plateOptions.Pepper, ownerUserId),
                    Status = PlateImageStatuses.Uploaded,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.PlateImages.Add(plate);
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                // A freshly-added plate has no detections yet and no analysis is started.
                return ToListItem(plate, platesCount: 0);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    public async Task<IReadOnlyList<PlateImageListItem>> ListAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PlateImages.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync(cancellationToken);

        var counts = await _analysis.CountDetectionsForImagesAsync(
            ownerUserId, rows.Select(r => r.Id).ToList(), cancellationToken);

        return rows
            .Select(p => ToListItem(p, counts.TryGetValue(p.Id, out var c) ? c : 0))
            .ToList();
    }

    public async Task<PlateImageDetail?> GetDetailAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var plate = await _db.PlateImages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerUserId == ownerUserId, cancellationToken);
        if (plate is null)
        {
            return null;
        }

        var (summary, detections) = await _analysis.LoadForDetailAsync(
            ownerUserId, plate.Id, plate.Status, cancellationToken);
        var redaction = _redaction is null
            ? new PlateRedactionInfo(false, 0, string.Empty)
            : await _redaction.GetInfoAsync(ownerUserId, plate.Id, cancellationToken);
        return ToDetail(plate, summary, detections, redaction);
    }

    public async Task<bool> DeleteAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var plate = await _db.PlateImages
                .FirstOrDefaultAsync(p => p.Id == id && p.OwnerUserId == ownerUserId, cancellationToken);
            if (plate is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            var blobId = plate.BlobObjectId;

            // Derived redacted-media cache blobs this plate owns. Their rows are
            // FK-cascaded away with the PlateImage, but the blob REFERENCES must
            // be released too (mirrors FileThumbnail derived-artifact cleanup) so
            // a zero-ref cache blob becomes janitor-eligible and nothing leaks.
            var cacheBlobIds = await _db.PlateRedactedMedia.AsNoTracking()
                .Where(m => m.PlateImageId == plate.Id && m.OwnerUserId == ownerUserId)
                .Select(m => m.BlobObjectId)
                .ToListAsync(cancellationToken);

            // Remove the row (and its cascading FKs) FIRST, then release the
            // references in the SAME transaction. Order matters: with the FKs
            // gone, a blob that drops to zero references is immediately
            // janitor-eligible; if the transaction rolls back, both the rows and
            // the refcounts are restored.
            _db.PlateImages.Remove(plate);
            await _db.SaveChangesAsync(cancellationToken);
            await _blobs.ReleaseAsync(blobId, cancellationToken);
            foreach (var cacheBlobId in cacheBlobIds)
            {
                await _blobs.ReleaseAsync(cacheBlobId, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }

    public async Task<PlateDerivativeContent?> RenderDerivativeAsync(
        Guid ownerUserId, Guid id, string size, CancellationToken cancellationToken = default)
    {
        if (!IsPlateDerivativeSize(size))
        {
            return null;
        }
        var normalized = ThumbnailSizes.Normalize(size);

        var blobId = await _db.PlateImages.AsNoTracking()
            .Where(p => p.Id == id && p.OwnerUserId == ownerUserId)
            .Select(p => (Guid?)p.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (blobId is null)
        {
            return null;
        }

        var rendered = await RenderDerivativeBytesAsync(blobId.Value, normalized, id, cancellationToken);
        return rendered is null ? null : new PlateDerivativeContent(rendered.Jpeg, "image/jpeg");
    }

    public async Task<PlateRedactionSource?> OpenRedactionSourceAsync(
        Guid ownerUserId, Guid id, PlateRedactionSourceKind kind, CancellationToken cancellationToken = default)
    {
        var plate = await _db.PlateImages.AsNoTracking()
            .Where(p => p.Id == id && p.OwnerUserId == ownerUserId)
            .Select(p => new { p.BlobObjectId, p.Width, p.Height })
            .FirstOrDefaultAsync(cancellationToken);
        if (plate is null)
        {
            return null;
        }

        if (kind == PlateRedactionSourceKind.Original)
        {
            // Redaction of the original operates on the full-resolution bytes so
            // the redacted rendition preserves the original dimensions.
            var bytes = await ReadSourceBytesAsync(plate.BlobObjectId, cancellationToken);
            int width = plate.Width ?? 0, height = plate.Height ?? 0;
            if (width <= 0 || height <= 0)
            {
                try
                {
                    var img = Image.Identify(bytes);
                    width = img.Width;
                    height = img.Height;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Plate original identify failed for plate {PlateId}.", id);
                    return null;
                }
            }
            return new PlateRedactionSource(bytes, width, height);
        }

        // Thumbnail/Preview: redact the derived small/medium JPEG so the redacted
        // rendition matches the served derivative's dimensions exactly.
        var normalized = kind == PlateRedactionSourceKind.Thumbnail
            ? ThumbnailSizes.Small
            : ThumbnailSizes.Medium;
        var rendered = await RenderDerivativeBytesAsync(plate.BlobObjectId, normalized, id, cancellationToken);
        return rendered is null ? null : new PlateRedactionSource(rendered.Jpeg, rendered.Width, rendered.Height);
    }

    // Shared derivative render with the defense-in-depth resource gates (mirrors
    // FileThumbnailService): byte cap, header-only Identify, dimension/pixel caps
    // — before any full decode. Returns the JPEG bytes + rendered dimensions, or
    // null for an unrenderable/pathological source.
    private async Task<RenderedDerivative?> RenderDerivativeBytesAsync(
        Guid blobObjectId, string normalizedSize, Guid plateId, CancellationToken cancellationToken)
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plate derivative render failed for plate {PlateId}.", plateId);
            return null;
        }
    }

    public async Task<PlateOriginalContent?> OpenOriginalAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var plate = await _db.PlateImages.AsNoTracking()
            .Where(p => p.Id == id && p.OwnerUserId == ownerUserId)
            .Select(p => new { p.BlobObjectId, p.ContentType, p.OriginalFileName })
            .FirstOrDefaultAsync(cancellationToken);
        if (plate is null)
        {
            return null;
        }

        var stream = await _blobs.OpenContentAsync(plate.BlobObjectId, cancellationToken);
        return new PlateOriginalContent(
            stream,
            SafeContentType.ForServing(plate.ContentType),
            plate.OriginalFileName);
    }

    private async Task<byte[]> ReadSourceBytesAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        await using var stream = await _blobs.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private async Task TryReleaseQuietlyAsync(Guid blobObjectId)
    {
        try
        {
            await _blobs.ReleaseAsync(blobObjectId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release plate blob reference after a failed upload.");
        }
    }

    // Plate media is served only as small (grid) and medium (viewer) derivatives.
    private static bool IsPlateDerivativeSize(string? size) =>
        string.Equals(size, ThumbnailSizes.Small, StringComparison.OrdinalIgnoreCase)
        || string.Equals(size, ThumbnailSizes.Medium, StringComparison.OrdinalIgnoreCase);

    private static PlateImageListItem ToListItem(PlateImage p, int platesCount) => new(
        p.Id,
        p.OriginalFileName,
        p.ContentType,
        p.SizeBytes,
        p.Width,
        p.Height,
        p.Status,
        PlateAnalysisProductStatus.FromPlateImageStatus(p.Status),
        platesCount,
        p.CreatedAt,
        p.UpdatedAt,
        ThumbnailUrl(p.Id),
        PreviewUrl(p.Id));

    private static PlateImageDetail ToDetail(
        PlateImage p, PlateAnalysisSummary summary, IReadOnlyList<PlateDetectionDto> detections,
        PlateRedactionInfo redaction) => new(
        p.Id,
        p.OriginalFileName,
        p.ContentType,
        p.SizeBytes,
        p.Width,
        p.Height,
        p.Status,
        p.CreatedAt,
        p.UpdatedAt,
        PreviewUrl(p.Id),
        OriginalUrl(p.Id),
        summary,
        detections,
        redaction);

    private static string ThumbnailUrl(Guid id) => $"/api/plates/images/{id}/thumbnail?size=small";
    private static string PreviewUrl(Guid id) => $"/api/plates/images/{id}/preview";
    private static string OriginalUrl(Guid id) => $"/api/plates/images/{id}/original";

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "plate";
        }
        // Display-only: strip any directory component and control characters. The
        // value is never used as a storage path.
        var justName = Path.GetFileName(name.Trim());
        var cleaned = new string((justName ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "plate";
        }
        return cleaned.Length > 512 ? cleaned[..512] : cleaned;
    }
}

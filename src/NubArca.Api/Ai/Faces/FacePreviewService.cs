using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Ai.Faces;

// Generates + serves high-quality square face crops for the People UI. The crop
// is derived from the ORIGINAL blob using the SAME EXIF-orient convention as face
// detection, then padded/squared/clamped and resized (Lanczos3, no upscale).
// Stored as a regenerable derived artifact (derived store + FacePreview row).
//
// This is UI-ONLY. It is NEVER an embedding source — embeddings stay computed from
// the original blob + landmark alignment.
//
// Owner/vault safety: every serve path requires the face's blob to be referenced
// by an active, NON-VAULT FileItem owned by the caller; otherwise it returns null
// (the endpoint maps that to a generic 404 — cross-owner / vaulted / missing are
// indistinguishable). The cached crop is blob-level (shared across owners) but the
// gate is per request.
public sealed class FacePreviewService
{
    public const string PreviewMimeType = "image/jpeg";
    private const int JpegQuality = 82;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<ImageProcessingOptions> _imageOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<FacePreviewService> _logger;

    public FacePreviewService(
        AppDbContext db,
        IBlobService blobs,
        IOptions<AiOptions> aiOptions,
        IOptions<ImageProcessingOptions> imageOptions,
        TimeProvider clock,
        ILogger<FacePreviewService> logger)
    {
        _db = db;
        _blobs = blobs;
        _aiOptions = aiOptions;
        _imageOptions = imageOptions;
        _clock = clock;
        _logger = logger;
    }

    // Open (or lazily generate) the owner-visible face crop. Null = generic 404.
    public async Task<ThumbnailContent?> EnsureAsync(
        Guid faceId, Guid ownerUserId, string size, CancellationToken cancellationToken = default)
    {
        if (!FacePreviewSizes.IsKnown(size))
        {
            return null;
        }
        var normalized = FacePreviewSizes.Normalize(size);

        var face = await ResolveVisibleFaceAsync(faceId, ownerUserId, cancellationToken);
        if (face is null)
        {
            return null; // missing / cross-owner / vaulted → 404
        }

        var existing = await OpenExistingAsync(faceId, normalized, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var generated = await TryGenerateAsync(face, normalized, cancellationToken);
        if (!generated)
        {
            return null; // safe fallback: UI drops to CSS crop / placeholder
        }

        return await OpenExistingAsync(faceId, normalized, cancellationToken);
    }

    // Drop all cached crops for a face (owner-scoped) so the next request
    // regenerates. Idempotent.
    public async Task<bool> RegenerateAsync(Guid faceId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var face = await ResolveVisibleFaceAsync(faceId, ownerUserId, cancellationToken);
        if (face is null)
        {
            return false;
        }

        var rows = await _db.FacePreviews.Where(p => p.FaceDetectionId == faceId).ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            _db.FacePreviews.RemoveRange(rows);
            await _db.SaveChangesAsync(cancellationToken);
            foreach (var r in rows)
            {
                await TryReleaseQuietlyAsync(r.BlobObjectId);
            }
        }

        return true;
    }

    // ---- internals -------------------------------------------------------

    private sealed record VisibleFace(
        Guid FaceId, Guid BlobObjectId,
        double X, double Y, double Width, double Height);

    private async Task<VisibleFace?> ResolveVisibleFaceAsync(
        Guid faceId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var face = await _db.FaceDetections.AsNoTracking()
            .Where(d => d.Id == faceId)
            .Select(d => new { d.BlobObjectId, d.BoundingBoxX, d.BoundingBoxY, d.BoundingBoxWidth, d.BoundingBoxHeight })
            .FirstOrDefaultAsync(cancellationToken);
        if (face is null)
        {
            return null;
        }

        // Owner + non-vault visibility (FileItems carries the global vault filter).
        var visible = await _db.FileItems.AsNoTracking().AnyAsync(
            f => f.BlobObjectId == face.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
            cancellationToken);
        if (!visible)
        {
            return null;
        }

        return new VisibleFace(
            faceId, face.BlobObjectId, face.BoundingBoxX, face.BoundingBoxY, face.BoundingBoxWidth, face.BoundingBoxHeight);
    }

    private async Task<ThumbnailContent?> OpenExistingAsync(
        Guid faceId, string size, CancellationToken cancellationToken)
    {
        var hit = await _db.FacePreviews.AsNoTracking()
            .Where(p => p.FaceDetectionId == faceId && p.Size == size)
            .Select(p => new { p.BlobObjectId, p.Width, p.Height })
            .FirstOrDefaultAsync(cancellationToken);
        if (hit is null)
        {
            return null;
        }

        var blob = await _db.BlobObjects.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == hit.BlobObjectId, cancellationToken);
        if (blob is null)
        {
            return null;
        }

        var stream = await _blobs.OpenDerivedContentAsync(hit.BlobObjectId, cancellationToken);
        if (stream is null)
        {
            // Bytes displaced (post-root-split) or wiped: try the cheap repair,
            // else regenerate on the next call.
            if (await _blobs.TryRestoreDerivedFromOriginalAsync(hit.BlobObjectId, cancellationToken))
            {
                stream = await _blobs.OpenDerivedContentAsync(hit.BlobObjectId, cancellationToken);
            }
            if (stream is null)
            {
                return null;
            }
        }

        return new ThumbnailContent(stream, PreviewMimeType, hit.Width, hit.Height, blob.SizeBytes);
    }

    private async Task<bool> TryGenerateAsync(VisibleFace face, string size, CancellationToken cancellationToken)
    {
        var img = _imageOptions.Value;
        try
        {
            // Cheap byte cap + header identify (same gates as thumbnails).
            var sourceBytes = await _db.BlobObjects.AsNoTracking()
                .Where(b => b.Id == face.BlobObjectId)
                .Select(b => (long?)b.SizeBytes)
                .FirstOrDefaultAsync(cancellationToken);
            if (sourceBytes is null || sourceBytes > img.MaxThumbnailInputBytes)
            {
                return false;
            }

            byte[] bytes;
            await using (var stream = await _blobs.OpenContentAsync(face.BlobObjectId, cancellationToken))
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, cancellationToken);
                bytes = ms.ToArray();
            }

            var info = await Image.IdentifyAsync(new MemoryStream(bytes, writable: false), cancellationToken);
            if (info is null
                || info.Width > img.MaxWidth || info.Height > img.MaxHeight
                || (long)info.Width * info.Height > img.MaxPixels)
            {
                return false;
            }

            var (jpeg, edge) = RenderCrop(bytes, face, size);

            BlobObject? previewBlob = null;
            FacePreview? row = null;
            try
            {
                using var encoded = new MemoryStream(jpeg, writable: false);
                previewBlob = await _blobs.StoreDerivedAsync(encoded, cancellationToken);
                row = new FacePreview
                {
                    Id = Guid.NewGuid(),
                    FaceDetectionId = face.FaceId,
                    BlobObjectId = previewBlob.Id,
                    Size = size,
                    Width = edge,
                    Height = edge,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime,
                };
                _db.FacePreviews.Add(row);
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                // Lost the (face, size) unique race — release ours; the winner serves.
                if (row is not null) _db.Entry(row).State = EntityState.Detached;
                if (previewBlob is not null) await TryReleaseQuietlyAsync(previewBlob.Id);
                return true; // a row now exists either way
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Safe aggregate diagnostic: size + action only, never ids/paths/keys.
            _logger.LogWarning(
                "Face preview generation failed; size={Size}; action=fallback ({Type}).", size, ex.GetType().Name);
            return false;
        }
    }

    // Pure crop: decode, EXIF-orient (matching detection + the context overlay),
    // compute the crop rectangle from the SAME normalized bounding box the viewer
    // overlays, then Lanczos3 resize with NO upscaling (never invents detail).
    // Returns JPEG bytes + the actual square edge produced.
    private (byte[] Jpeg, int Edge) RenderCrop(byte[] bytes, VisibleFace face, string size)
    {
        using var image = Image.Load<Rgb24>(bytes);
        image.Mutate(c => c.AutoOrient());
        int w = image.Width, h = image.Height;

        var padding = Math.Clamp(_aiOptions.Value.Face.PreviewPaddingPercent, 0d, 3d);
        var crop = ComputeCropRect(w, h, face.X, face.Y, face.Width, face.Height, padding);

        var targetEdge = FacePreviewSizes.GetEdge(size);
        var outEdge = Math.Min(targetEdge, crop.Width); // no upscaling

        image.Mutate(c => c
            .Crop(crop)
            .Resize(new ResizeOptions
            {
                Size = new Size(outEdge, outEdge),
                Mode = ResizeMode.Stretch, // square→square, no distortion
                Sampler = KnownResamplers.Lanczos3,
            }));

        using var outMs = new MemoryStream();
        image.SaveAsJpeg(outMs, new JpegEncoder { Quality = JpegQuality });
        return (outMs.ToArray(), outEdge);
    }

    // Pure, unit-testable crop geometry. The context viewer overlays the raw
    // normalized FaceDetection bounding box on the EXIF-oriented image, so that box
    // is the source of truth. We:
    //   1. map the normalized bbox → oriented-image pixels (same space as detection
    //      and the overlay),
    //   2. expand the RECTANGLE by paddingPerSide on every side
    //      (x -= w*p; y -= h*p; w *= 1+2p; h *= 1+2p),
    //   3. square it, centered on the expanded rectangle
    //      (side = max(w, h), centre = expanded-rect centre),
    //   4. clamp to image bounds (never exceeds the image; the full square stays in).
    // No landmark centering, no detection change: the chip shows exactly the boxed
    // area plus paddingPerSide margin per side, squared.
    public static Rectangle ComputeCropRect(
        int imageWidth, int imageHeight,
        double normX, double normY, double normWidth, double normHeight,
        double paddingPerSide)
    {
        // 1) normalized bbox → oriented pixels
        double fx = normX * imageWidth;
        double fy = normY * imageHeight;
        double fw = normWidth * imageWidth;
        double fh = normHeight * imageHeight;

        // 2) expand the rectangle by paddingPerSide on each side
        double ex = fx - fw * paddingPerSide;
        double ey = fy - fh * paddingPerSide;
        double ew = fw * (1 + 2 * paddingPerSide);
        double eh = fh * (1 + 2 * paddingPerSide);

        // 3) square, centered on the expanded rectangle
        double centerX = ex + ew / 2;
        double centerY = ey + eh / 2;
        double sideD = Math.Max(ew, eh);
        if (!(sideD > 0)) // degenerate / zero box → best-effort centre square
        {
            sideD = Math.Min(imageWidth, imageHeight);
        }

        // 4) clamp: cap the square to the image, then clamp its position in-bounds
        var side = (int)Math.Round(Math.Min(sideD, Math.Min(imageWidth, imageHeight)));
        side = Math.Clamp(side, 1, Math.Min(imageWidth, imageHeight));

        var x = Math.Clamp((int)Math.Round(centerX - side / 2.0), 0, imageWidth - side);
        var y = Math.Clamp((int)Math.Round(centerY - side / 2.0), 0, imageHeight - side);

        return new Rectangle(x, y, side, side);
    }

    private async Task TryReleaseQuietlyAsync(Guid blobId)
    {
        try
        {
            await _blobs.ReleaseAsync(blobId, CancellationToken.None);
        }
        catch
        {
            // Best-effort; a stray derived blob is reclaimed by the janitor.
        }
    }
}

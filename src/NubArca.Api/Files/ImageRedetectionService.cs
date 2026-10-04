using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Files;

public sealed record ImageRedetectionOptions
{
    public int? Limit { get; init; }
    public bool DryRun { get; init; }
    public int PageSize { get; init; } = 100;
}

/// <summary>Numbers only: never a name, a path or a key.</summary>
public sealed record ImageRedetectionResult(int Examined, int Recognised, int StillUnreadable);

/// <summary>
/// Gives a second look to originals stored as images their upload could not
/// read: a category of "image" (from the name or MIME type the client sent)
/// but no content type detected from the bytes. They were HEIC — the iPhone's
/// photo format — before the server could decode it.
///
/// A blob is recognised exactly as an upload now recognises it: by the HEIF
/// signature, and only if FFmpeg actually decodes its frame. Its metadata takes
/// the detected type, the decoded (upright) dimensions and an orientation of 1,
/// and every file of it takes the dimensions; its thumbnail status goes back to
/// pending. Derivatives are then the ordinary derivative backfill's work, and
/// the AI's backfills — which look for exactly such images — pick them up on
/// their next run. Nothing else is touched: the original stays byte for byte
/// what it was.
///
/// Keyset-paged by blob id: candidates are never all loaded at once.
/// </summary>
public sealed class ImageRedetectionService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IOriginalImageReader _originals;
    private readonly TimeProvider _clock;

    public ImageRedetectionService(
        AppDbContext db, IBlobService blobs, IOriginalImageReader originals, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _originals = originals;
        _clock = clock;
    }

    public async Task<ImageRedetectionResult> RunAsync(
        ImageRedetectionOptions options, CancellationToken cancellationToken)
    {
        int examined = 0, recognised = 0, unreadable = 0;
        Guid? after = null;
        while (options.Limit is not int limit || examined < limit)
        {
            var take = options.Limit is int cap ? Math.Min(options.PageSize, cap - examined) : options.PageSize;
            var page = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.MediaCategory == MediaCategories.Image && m.DetectedContentType == null
                    && (after == null || m.BlobObjectId.CompareTo(after.Value) > 0))
                .OrderBy(m => m.BlobObjectId)
                .Select(m => m.BlobObjectId)
                .Take(take)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;

            foreach (var blobId in page)
            {
                examined++;
                after = blobId;
                var size = await DecodedSizeAsync(blobId, cancellationToken);
                if (size is not var (width, height))
                {
                    unreadable++;
                    continue;
                }
                recognised++;
                if (!options.DryRun) await RecordAsync(blobId, width, height, cancellationToken);
            }
        }
        return new ImageRedetectionResult(examined, recognised, unreadable);
    }

    private async Task<(int Width, int Height)?> DecodedSizeAsync(Guid blobId, CancellationToken cancellationToken)
    {
        byte[]? pixels;
        try
        {
            // HEIC only, by its signature: a file that is not one is left as
            // it is, whatever else might happen to read it.
            await using (var stream = await _blobs.OpenContentAsync(blobId, cancellationToken))
            {
                var header = new byte[HeifSignature.HeaderLength];
                var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
                if (!HeifSignature.IsHeif(header.AsSpan(0, read))) return null;
            }
            pixels = await _originals.ReadForPixelsAsync(blobId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (pixels is null) return null;
        try
        {
            var info = Image.Identify(pixels);
            return (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    private async Task RecordAsync(Guid blobId, int width, int height, CancellationToken cancellationToken)
    {
        var (w, h, pixelCount) = BlobDimensions.Normalize(width, height);
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        // Guarded on the content type still being unknown: a second run, or a
        // race with another, changes nothing twice.
        var updated = await _db.BlobMetadata
            .Where(m => m.BlobObjectId == blobId && m.DetectedContentType == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.DetectedContentType, HeifSignature.ContentType)
                .SetProperty(m => m.DetectedFormat, HeifSignature.Format)
                .SetProperty(m => m.Width, w)
                .SetProperty(m => m.Height, h)
                .SetProperty(m => m.PixelCount, pixelCount)
                // Decoded upright: the EXIF orientation must not apply again.
                .SetProperty(m => m.Orientation, 1)
                .SetProperty(m => m.ThumbnailStatus, MetadataStatuses.Pending)
                .SetProperty(m => m.UpdatedAt, now), cancellationToken);
        if (updated == 1)
        {
            await _db.FileItems
                .Where(f => f.BlobObjectId == blobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Width, width)
                    .SetProperty(f => f.Height, height), cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }
}

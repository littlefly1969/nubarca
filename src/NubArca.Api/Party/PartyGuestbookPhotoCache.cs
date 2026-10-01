using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Party;

/// <summary>
/// THE PICTURE A MEMORY IS DRAWN WITH: a medium preview, rendered from the
/// memory's OWN original and kept as a derived blob the memory holds.
///
/// <para>Why it exists at all. Every other party surface serves a photograph
/// through <c>IFileThumbnailService</c>, whose derivatives belong to the album
/// FILE. A memory deliberately does not depend on that file — the host may
/// remove the photograph from the album, or delete it, and the memory must not
/// notice — so it cannot borrow the file's derivatives either. It holds the
/// original blob itself, and this draws the preview from that.</para>
///
/// <para>It is a CACHE, exactly like a <c>FileThumbnail</c>: rendered on first
/// request through the same <see cref="ImageDerivativeRenderer"/> and the same
/// input gates, stored through <see cref="IBlobService.StoreDerivedAsync"/>
/// (content-addressed, so identical renders share bytes), and held by one
/// reference in <c>PreviewBlobObjectId</c>. If the derived bytes are ever lost,
/// the next request draws them again from the original. The caller strips
/// metadata before anything leaves the server; a guest is never handed an
/// original.</para>
///
/// <para>Authorization is NOT decided here. The caller has already established
/// that whoever is asking may see this memory; this only knows how to produce
/// its picture.</para>
/// </summary>
public sealed class PartyGuestbookPhotoCache
{
    /// <summary>The derivative a memory is drawn with: the viewer's size.</summary>
    internal const string PreviewSize = ThumbnailSizes.Medium;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly ImageDerivativeRenderer _renderer;
    private readonly ImageProcessingOptions _imageOptions;
    private readonly MediaDerivativesOptions _mediaOptions;
    private readonly ILogger<PartyGuestbookPhotoCache> _logger;

    public PartyGuestbookPhotoCache(
        AppDbContext db,
        IBlobService blobs,
        ImageDerivativeRenderer renderer,
        IOptions<ImageProcessingOptions> imageOptions,
        IOptions<MediaDerivativesOptions> mediaOptions,
        ILogger<PartyGuestbookPhotoCache> logger)
    {
        _db = db;
        _blobs = blobs;
        _renderer = renderer;
        _imageOptions = imageOptions.Value;
        _mediaOptions = mediaOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// The memory's preview as a JPEG stream (metadata NOT yet stripped), or
    /// null when the memory does not exist or its photograph cannot be drawn.
    /// </summary>
    public async Task<Stream?> OpenAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .Where(e => e.Id == entryId)
            .Select(e => new { e.BlobObjectId, e.PreviewBlobObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return null;
        }

        if (entry.PreviewBlobObjectId is Guid preview)
        {
            var cached = await _blobs.OpenDerivedContentAsync(preview, cancellationToken);
            // A derivative made before the derived root was split may still
            // live only in the original root: move it across rather than
            // decoding the whole original again.
            if (cached is null && await _blobs.TryRestoreDerivedFromOriginalAsync(preview, cancellationToken))
            {
                cached = await _blobs.OpenDerivedContentAsync(preview, cancellationToken);
            }
            if (cached is not null)
            {
                return cached;
            }
            // The bytes are gone. Draw them again below, and repoint.
        }

        var rendered = await RenderAsync(entry.BlobObjectId, entryId, cancellationToken);
        if (rendered is null)
        {
            return null;
        }

        await KeepAsync(entryId, entry.PreviewBlobObjectId, rendered, cancellationToken);
        return new MemoryStream(rendered.Jpeg, writable: false);
    }

    /// <summary>
    /// Stores the render and points the memory at it — but only if nobody did
    /// so first. Every reference taken here is either kept by the row or given
    /// back, so a lost race or a memory deleted mid-render leaks nothing.
    /// </summary>
    private async Task KeepAsync(
        Guid entryId, Guid? previous, RenderedDerivative rendered, CancellationToken cancellationToken)
    {
        Guid derived;
        await using (var bytes = new MemoryStream(rendered.Jpeg, writable: false))
        {
            derived = (await _blobs.StoreDerivedAsync(bytes, cancellationToken)).Id;
        }

        int claimed;
        try
        {
            // Compare-and-set on the value this request READ: two first views
            // at once both render, exactly one row update wins, and the loser
            // returns its reference below.
            claimed = previous is Guid stale
                ? await _db.PartyGuestbookEntries
                    .Where(e => e.Id == entryId && e.PreviewBlobObjectId == stale)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(e => e.PreviewBlobObjectId, derived), cancellationToken)
                : await _db.PartyGuestbookEntries
                    .Where(e => e.Id == entryId && e.PreviewBlobObjectId == null)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(e => e.PreviewBlobObjectId, derived), cancellationToken);
        }
        catch
        {
            // Including cancellation of the update itself: the reference the
            // store took goes back whatever happened to the request.
            await _blobs.ReleaseAsync(derived, CancellationToken.None);
            throw;
        }

        // From here on the store has happened and the compare-and-set has been
        // decided: what is left is BOOKKEEPING, not request work. Both releases
        // run with CancellationToken.None, so a guest closing the page at this
        // instant cannot cancel the decrement and leave a reference nothing
        // owns until the next repair.
        if (claimed == 1)
        {
            // The row now holds the new reference; the one it held before is
            // the stale derivative's, and goes back. When the render hashed to
            // the very same blob, this is what undoes the second increment.
            if (previous is Guid replaced)
            {
                await _blobs.ReleaseAsync(replaced, CancellationToken.None);
            }
        }
        else
        {
            await _blobs.ReleaseAsync(derived, CancellationToken.None);
        }
    }

    /// <summary>
    /// The medium rendition of an original, behind the same input gates the
    /// gallery's own derivatives use, or null when it cannot be drawn.
    /// </summary>
    private async Task<RenderedDerivative?> RenderAsync(
        Guid blobObjectId, Guid entryId, CancellationToken cancellationToken)
    {
        var sourceBytes = await _db.BlobObjects
            .AsNoTracking()
            .Where(b => b.Id == blobObjectId)
            .Select(b => (long?)b.SizeBytes)
            .FirstOrDefaultAsync(cancellationToken);
        if (sourceBytes is not long length || length > _imageOptions.MaxThumbnailInputBytes)
        {
            return null;
        }

        try
        {
            byte[] source;
            await using (var stream = await _blobs.OpenContentAsync(blobObjectId, cancellationToken))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                source = buffer.ToArray();
            }

            using (var probe = new MemoryStream(source, writable: false))
            {
                var info = await Image.IdentifyAsync(probe, cancellationToken);
                var pixels = (long)info.Width * info.Height;
                if (info.Width > _imageOptions.MaxWidth
                    || info.Height > _imageOptions.MaxHeight
                    || pixels > _imageOptions.MaxPixels)
                {
                    return null;
                }
            }

            var render = await _renderer.RenderAsync(
                source,
                [
                    new DerivativeRequest(
                        PreviewSize, _mediaOptions.EdgeFor(PreviewSize), _mediaOptions.QualityFor(PreviewSize)),
                ],
                cancellationToken);
            return render.Results[0];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The memory's id is the only identifier logged: never the blob,
            // its hash or its storage key.
            _logger.LogWarning(ex, "Guest book preview render failed for entry {EntryId}.", entryId);
            return null;
        }
    }
}

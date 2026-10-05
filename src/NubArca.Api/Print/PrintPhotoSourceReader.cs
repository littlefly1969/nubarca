using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Files;

namespace NubArca.Api.Print;

/// <summary>
/// Reads an original photograph's bytes so the server can compose a print with
/// them — a party's sheet, or an owner's own photograph printed from their
/// library or an album.
///
/// This is the one place printing touches originals, and it is deliberately
/// server-side only: a browser composes against safe previews and never
/// receives an original URL. Printing at 300dpi from a downscaled preview would
/// produce a soft print, so the sheet is built from the real file — inside the
/// server, scoped to the owner the file belongs to.
/// </summary>
public sealed class PrintPhotoSourceReader : IPrintPhotoSourceReader
{
    private readonly AppDbContext _db;
    private readonly IOriginalImageReader _originals;
    private readonly HeifDecodeGate _gate;

    public PrintPhotoSourceReader(AppDbContext db, IOriginalImageReader originals, HeifDecodeGate gate)
    {
        _db = db;
        _originals = originals;
        _gate = gate;
    }

    public async Task<PrintPhotoSources?> OpenAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        // Owner-scoped: a print token belongs to one party, and that party's
        // owner is the only person whose files it may ever compose; an owner's
        // own print is their own file. A file in the trash is no source.
        var ids = fileItemIds.Distinct().ToList();
        var blobByFile = await _db.FileItems.AsNoTracking()
            .Where(f => ids.Contains(f.Id) && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .ToDictionaryAsync(f => f.Id, f => f.BlobObjectId, cancellationToken);
        if (fileItemIds.Any(id => !blobByFile.ContainsKey(id))) return null;
        var blobs = fileItemIds.Select(id => blobByFile[id]).ToList();

        // The sheet's frames are decoded and HELD together — the composer
        // takes them all at once — so their decode slots are taken together:
        // as many as the sheet has frames that may need decoding (HEIC, or not
        // yet recognised), at most all of them, never one by one while
        // holding others.
        var distinct = blobs.Distinct().ToList();
        var maybeFrames = (await _db.BlobMetadata.AsNoTracking()
                .Where(m => distinct.Contains(m.BlobObjectId)
                    && (m.DetectedContentType == null || m.DetectedContentType == HeifSignature.ContentType))
                .Select(m => m.BlobObjectId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var frames = blobs.Count(maybeFrames.Contains);
        var slots = frames > 0 ? await _gate.EnterAsync(frames, cancellationToken) : null;

        var leases = new List<OriginalPixelsLease>(blobs.Count);
        var held = new Held(leases, slots);
        try
        {
            var photos = new List<byte[]>(blobs.Count);
            foreach (var blob in blobs)
            {
                // Still the original: its own bytes, or for HEIC its frame
                // decoded losslessly and upright by FFmpeg — never a preview.
                var lease = slots is null
                    ? await _originals.OpenForPixelsAsync(blob, cancellationToken)
                    : await _originals.OpenForPixelsAsync(blob, slots, cancellationToken);
                if (lease is null)
                {
                    await held.DisposeAsync();
                    return null;
                }
                leases.Add(lease);
                photos.Add(await lease.ReadAllBytesAsync(cancellationToken));
            }
            return new PrintPhotoSources(photos, held);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            || (ex is InvalidOperationException && ex.Message.Contains("was not found", StringComparison.Ordinal)))
        {
            // The row survived its bytes. Nothing to compose: the caller refuses
            // the source rather than printing a blank.
            await held.DisposeAsync();
            return null;
        }
        catch
        {
            await held.DisposeAsync();
            throw;
        }
    }

    /// <summary>The sheet's leases, then its slots.</summary>
    private sealed class Held(List<OriginalPixelsLease> leases, IDisposable? slots) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var lease in leases) await lease.DisposeAsync();
            slots?.Dispose();
        }
    }
}

/// <summary>
/// The DISPLAY shape (width / height, after the EXIF orientation) of an owner's
/// photographs, from the metadata already stored — no bytes read. What a
/// framing is validated against before anything is reserved or rendered.
/// </summary>
public static class PrintPhotoShapes
{
    public static async Task<IReadOnlyDictionary<Guid, double>> DisplayAspectsAsync(
        AppDbContext db, Guid ownerUserId, IReadOnlyCollection<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        var rows = await db.FileItems.AsNoTracking()
            .Where(f => fileItemIds.Contains(f.Id) && f.OwnerUserId == ownerUserId)
            .Select(f => new
            {
                f.Id, f.Width, f.Height,
                Meta = db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => new { m.Width, m.Height, m.Orientation }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var shapes = new Dictionary<Guid, double>();
        foreach (var row in rows)
        {
            var (width, height) = Metadata.ImageDisplayDimensions.Resolve(
                row.Width ?? row.Meta?.Width, row.Height ?? row.Meta?.Height, row.Meta?.Orientation);
            if (width is > 0 && height is > 0) shapes[row.Id] = (double)width.Value / height.Value;
        }
        return shapes;
    }
}

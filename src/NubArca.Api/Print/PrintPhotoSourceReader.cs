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

    public PrintPhotoSourceReader(AppDbContext db, IOriginalImageReader originals)
    {
        _db = db;
        _originals = originals;
    }

    public async Task<byte[]?> ReadAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken)
    {
        // Owner-scoped: a print token belongs to one party, and that party's
        // owner is the only person whose files it may ever compose; an owner's
        // own print is their own file. A file in the trash is no source.
        var blobObjectId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (blobObjectId is null) return null;

        try
        {
            // Still the original: its own bytes, or for HEIC its frame decoded
            // losslessly and upright by FFmpeg at this moment — never a preview.
            // The composer takes bytes; the frame's file is gone once read.
            await using var pixels = await _originals.OpenForPixelsAsync(blobObjectId.Value, cancellationToken);
            return pixels is null ? null : await pixels.ReadAllBytesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            || (ex is InvalidOperationException && ex.Message.Contains("was not found", StringComparison.Ordinal)))
        {
            // The row survived its bytes. Nothing to compose: the caller refuses
            // the source rather than printing a blank.
            return null;
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

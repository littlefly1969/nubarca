using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Storage;

namespace NubArca.Api.Print;

/// <summary>
/// Reads an original photograph's bytes so the server can compose with them.
///
/// This is the one place party printing touches originals, and it is
/// deliberately server-side only: the guest's browser composes against safe
/// previews and never receives an original URL. Printing at 300dpi from a
/// downscaled preview would produce a soft print, so the sheet is built from the
/// real file — inside the server, scoped to the owner whose party this is.
/// </summary>
public sealed class PartyPrintSourceReader : IPartyPrintSourceReader
{
    private readonly AppDbContext _db;
    private readonly IBlobStorage _storage;

    public PartyPrintSourceReader(AppDbContext db, IBlobStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public async Task<byte[]?> ReadAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken)
    {
        // Owner-scoped: a print token belongs to one party, and that party's
        // owner is the only person whose files it may ever compose.
        var storageKey = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId)
            .Join(_db.BlobObjects.AsNoTracking(),
                f => f.BlobObjectId, b => b.Id, (f, b) => b.StorageKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(storageKey)) return null;

        try
        {
            await using var source = await _storage.OpenReadAsync(storageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            // The row survived its bytes. Nothing to compose: the caller refuses
            // the source rather than printing a blank.
            return null;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Party;

/// <summary>
/// Which of an owner's files a Party FEATURE may point at — the menu's
/// photograph, an activity's picture — independently of any album.
///
/// <para><b>Party does not own a second media library.</b> The file is the
/// owner's ordinary <see cref="FileItem"/>. Album membership is one relation to
/// it and a Party reference is another, and neither implies the other: a menu
/// photo may also be in the party's album, or may be a graphic that never will
/// be, and choosing it must not file it anywhere.</para>
///
/// <para><b>Album membership and a Party reference are independent;
/// media-library eligibility is not.</b> Reaching a file through a slot rather
/// than through an <c>AlbumItem</c> changes which RELATION authorizes it, never
/// whether it is media the product still shows — so a file the owner moved out
/// of their media library is out of Party too, exactly as it is on the
/// album-scoped path, and "extra-album" is not another word for
/// "excluded".</para>
///
/// <para>This is the ONE rule for the second relation. It is asked when the
/// owner writes a reference AND every time a guest asks for its bytes, so a
/// file that stops qualifying — sent to Trash, moved into the Private Vault —
/// stops being served on the next request without anybody rewriting the
/// reference.</para>
///
/// <para>Eligible means: the owner's own file, not in Trash, not in the Private
/// Vault (<c>FileItems</c> carries that global filter and nothing here ignores
/// it), in the ACTIVE media library — narrowed through
/// <see cref="MediaLibraryScopePolicy"/>, the one policy every media surface
/// uses, rather than a second copy of the same comparison — and that the SERVER
/// recognised as an image. The last is two facts on
/// <c>BlobMetadata</c>, not one: ingestion falls back to the client's MIME type
/// for <c>MediaCategory</c> when the sniffer recognises nothing, so a text file
/// uploaded as <c>image/png</c> is categorised "image" with a NULL
/// <c>DetectedContentType</c>. The non-null detection is the gate the gallery
/// and playback already require, and it is the one that makes the browser's
/// declared type irrelevant here.</para>
/// </summary>
public static class PartyMediaReference
{
    public static IQueryable<Guid> EligibleFileIds(AppDbContext db, Guid ownerUserId) =>
        MediaLibraryScopePolicy
            .ApplyScope(
                db.FileItems
                    .AsNoTracking()
                    .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt == null),
                MediaLibraryScope.Active)
            .Join(db.BlobMetadata.AsNoTracking(),
                f => f.BlobObjectId,
                m => m.BlobObjectId,
                (f, m) => new { f.Id, m.MediaCategory, m.DetectedContentType })
            .Where(x => x.MediaCategory == MediaCategories.Image && x.DetectedContentType != null)
            .Select(x => x.Id);

    public static Task<bool> IsEligibleAsync(
        AppDbContext db, Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default) =>
        EligibleFileIds(db, ownerUserId).AnyAsync(id => id == fileItemId, cancellationToken);

    /// <summary>The subset of <paramref name="candidates"/> that is eligible, in one query.</summary>
    public static async Task<HashSet<Guid>> EligibleAmongAsync(
        AppDbContext db, Guid ownerUserId, IReadOnlyCollection<Guid> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var eligible = await EligibleFileIds(db, ownerUserId)
            .Where(id => candidates.Contains(id))
            .ToListAsync(cancellationToken);
        return eligible.ToHashSet();
    }
}

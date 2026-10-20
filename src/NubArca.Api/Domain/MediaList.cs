namespace NubArca.Api.Domain;

// An owner's list of media: either a NAMED list ("Da stampare", "Per la nonna")
// or the single CURRENT SELECTION that follows the account across devices.
//
// A list holds REFERENCES to the owner's own FileItems — never a copy of a file,
// a blob, a thumbnail, a URL or a token. A photo can sit in any number of lists;
// removing it from one never touches the file or the other lists. What a list
// SHOWS is always re-derived from the live FileItem: a trashed file, a file moved
// into the Private Vault or a file that no longer exists drops out of every list
// view on its own, without anybody rewriting list rows.
public enum MediaListKind
{
    Named = 0,
    // At most one per owner (filtered unique index). Created lazily on first use.
    CurrentSelection = 1,
    // A member's personal selection inside ONE album shared with them: at most
    // one per (member, album). It references the album's media, which belongs to
    // somebody else, so it is never a /api/lists list and never mixes with the
    // member's own library selection. Readable only while the membership holds.
    AlbumSelection = 2,
}

public class MediaList
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    // Null for the current selection, which has no name.
    public string? Name { get; set; }
    public MediaListKind Kind { get; set; }
    // The album an AlbumSelection is scoped to; null for every other kind. The
    // album id only — never a share token or URL.
    public Guid? AlbumId { get; set; }
    // Moves on every change to the name or the contents. A device that holds an
    // older version knows its copy is stale; a rename that names an old version
    // is refused (409) rather than silently overwriting a newer name.
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// One file's membership of one list. The primary key (ListId, FileItemId) is the
// per-list dedup: the same photo can never appear twice in one list.
public class MediaListItem
{
    public Guid ListId { get; set; }
    public Guid FileItemId { get; set; }
    // Insertion order (pick order for the selection). Never assumed contiguous or
    // unique: every read breaks ties on FileItemId.
    public long Position { get; set; }
    public DateTime AddedAt { get; set; }
}

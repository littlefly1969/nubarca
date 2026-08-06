namespace NubArca.Api.Domain;

public class Album
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Owner-controlled allowlist flag: when true, a paired TV session belonging
    // to this album's owner may browse/play this album's media. Owner-scoped by
    // virtue of the album itself (never blob-global); default false so nothing
    // is exposed to a TV until the owner explicitly opts in. Disabling it is
    // re-checked on every TV request, so an album disappears from the TV on the
    // next list/poll/media call.
    public bool ShowOnTv { get; set; }

    // SHARE-ALBUM-03: the album's chosen cover.
    //
    // NULL means "derive it", which is what every album did before this slice
    // and still does until somebody picks one — the first few displayable
    // members, in album order. A null default is what makes this additive: no
    // existing album changes appearance, and deleting or withdrawing the chosen
    // item falls back to the derived cover rather than leaving a hole.
    //
    // Deliberately NOT a foreign key to FileItem. The chosen item can stop being
    // servable (withdrawn by its contributor, soft-deleted, vaulted, excluded)
    // while the album row lives on; an FK Restrict would then block those
    // perfectly legitimate operations, and an FK Cascade would silently rewrite
    // the owner's choice. It is validated on write and re-validated on read.
    public Guid? CoverFileItemId { get; set; }

    // SHARE-ALBUM-03: optimistic concurrency token.
    //
    // The album is now writable by more than one person — an Editor renaming it
    // while another reorders it, or the owner revoking an Editor mid-edit — so
    // "last write wins" would silently discard somebody's work. Every mutation
    // states the version it read; a mismatch is a 409 carrying the current
    // state, never a silent overwrite.
    //
    // A plain incrementing int rather than a database row-version: PostgreSQL's
    // xmin would work in production but not on the SQLite the endpoint tests run
    // against, and a token that cannot be exercised by the tests is a token
    // nobody can prove works. Incremented by the service on every album
    // mutation, including item add/remove/reorder — the ORDER is part of what a
    // concurrent editor may be looking at.
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

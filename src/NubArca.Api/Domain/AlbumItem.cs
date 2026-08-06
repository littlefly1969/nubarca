namespace NubArca.Api.Domain;

// A file's membership of an album.
//
// SHARE-ALBUM-02 gave this row PROVENANCE. Before it, only the album owner could
// add anything, so "who put this here" was always the owner and never needed
// storing. Now a Contributor can add media they own to somebody else's album,
// and three questions have to be answerable from the row itself:
//
//   who owns the media   → FileItem.OwnerUserId (never duplicated here)
//   who put it here      → AddedByUserId
//   when                 → AddedAt
//
// "Is it currently active" is the existence of the row. Album items are HARD
// deleted — by the owner removing them, by the contributor withdrawing them, by
// a membership revocation, and by the permanent-delete/sweeper paths that
// already clear every AlbumItem for a file. Introducing a soft-delete instead
// would mean re-checking an `IsActive` predicate in every one of the read paths
// listed in the SHARE-ALBUM-02 audit — including Party and TV — where a single
// miss silently republishes withdrawn media. "Who removed it, and when" is
// answered by the audit log, which is where that question belongs.
public class AlbumItem
{
    // SHARE-ALBUM-03: a stable surrogate identity for the MEMBERSHIP row.
    //
    // The primary key stays (AlbumId, FileItemId) — that is what enforces "one
    // row per file per album" and it is not weakened here. This id exists so a
    // reorder can name the rows it is reordering unambiguously, rather than
    // naming files and relying on the album context to disambiguate them. It
    // also keeps the reorder contract stable if the model ever allows the same
    // file to appear twice in one album.
    //
    // An alternate key, not the primary key: changing the PK would be a
    // rewrite of a table three other slices already query, for no gain.
    public Guid Id { get; set; }

    public Guid AlbumId { get; set; }
    public Guid FileItemId { get; set; }
    public DateTime AddedAt { get; set; }

    // The user who placed this item in the album — the album owner for their
    // own media, or a Contributor for a linked contribution. NOT the media's
    // owner: that stays on FileItem and is never copied here, so the two can
    // never drift apart.
    //
    // Backfilled to the album's owner by the AddAlbumItemProvenance migration:
    // before SHARE-ALBUM-02 nobody else could add anything, so that value is
    // accurate rather than a placeholder. Non-nullable, so no "null means the
    // owner" special case leaks into the query predicates.
    public Guid AddedByUserId { get; set; }

    // SHARE-ALBUM-03: the item's position in the album's own order.
    //
    // Until this slice every surface ordered by AddedAt — which is not an order
    // anybody chose, and which bulk-adds make ambiguous because they share a
    // timestamp. SortOrder is the album's curated sequence.
    //
    // Backfilled by AddAlbumOrderingAndCover from the previous implicit order
    // (AddedAt, then FileItemId as the stable tie-break the read paths already
    // used), so no album visibly reshuffles when this ships. New items append.
    //
    // Never assumed contiguous or unique: every read applies FileItemId as a
    // final tie-break, so two rows that somehow share a SortOrder still produce
    // a stable, repeatable order rather than one that shuffles per query.
    public int SortOrder { get; set; }
}

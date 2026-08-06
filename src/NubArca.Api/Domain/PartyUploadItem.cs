namespace NubArca.Api.Domain;

// Owner-side moderation record for a single ANONYMOUS party upload. One row per
// guest-uploaded FileItem, created the moment the upload lands in a party album.
// It tracks whether that guest photo is currently visible on the PUBLIC party
// page / TV surfaces, so the owner can quickly hide unwanted content and can
// optionally require approval before new uploads appear.
//
// This is a VISIBILITY control only — it never touches the owner's stored
// FileItem/blob (immutable content-addressed storage is preserved). A hidden or
// rejected upload simply stops being surfaced through party/TV; the owner's copy
// remains in their library unless deleted separately.
//
// Owner-added album content has NO row here and is therefore always visible —
// moderation applies only to guest uploads, identified by this row (never by
// folder name).
public class PartyUploadItem
{
    public Guid Id { get; set; }

    // The album owner. Every moderation query is scoped to this.
    public Guid OwnerUserId { get; set; }

    // The party album the guest uploaded into.
    public Guid AlbumId { get; set; }

    // The party link the upload came through, when known. Nullable so the row
    // survives a link rotation/revocation and needs no backfill.
    public Guid? PartyAlbumLinkId { get; set; }

    // The uploaded logical file. Unique — one moderation record per guest upload.
    public Guid FileItemId { get; set; }

    // Moderation state (see PartyUploadStatuses). Only "approved" is public/TV
    // visible; "pending" (awaiting owner approval), "hidden" (owner removed a
    // previously-visible item), "rejected" (owner declined a pending item), and
    // "removed_from_album" (owner removed the file from this album but kept the
    // provenance row) are all excluded from every public/TV surface.
    public string Status { get; set; } = PartyUploadStatuses.Approved;

    public DateTime UploadedAt { get; set; }

    // When the owner last moderated this item (approve/hide/reject). Null while
    // the item is in its initial auto-approved or pending state.
    public DateTime? ModeratedAt { get; set; }

    // The owner/user who performed the last moderation action. Null until moderated.
    public Guid? ModeratedByUserId { get; set; }
}

// Party upload moderation states. Kept as short string constants (matching the
// codebase style, e.g. MediaCategories) so the set is explicit and greppable.
public static class PartyUploadStatuses
{
    // Immediately visible on public party + TV (the default, low-friction mode).
    public const string Approved = "approved";

    // Awaiting owner approval (approval mode on). Not visible anywhere public/TV.
    public const string Pending = "pending";

    // Owner hid a previously-approved guest upload. Not visible; recoverable.
    public const string Hidden = "hidden";

    // Owner declined a pending guest upload. Not visible; recoverable.
    public const string Rejected = "rejected";

    // Owner removed the guest upload from the album membership. Not visible;
    // recoverable by re-adding the existing FileItem to the album.
    public const string RemovedFromAlbum = "removed_from_album";

    // The ONLY state surfaced through public party / TV.
    public static bool IsPublicVisible(string status) => status == Approved;
}

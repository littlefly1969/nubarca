namespace NubArca.Api.Domain;

// SHARE-COPY-01: a one-time, DETACHED album copy between two authenticated
// NubArca users.
//
// This is deliberately NOT a membership and shares no machinery with
// AlbumMembership. A live share grants bounded, revocable access to media that
// stays the owner's; a transfer hands over an independent DUPLICATE that becomes
// the recipient's own the moment they accept. Nothing about the source can reach
// an accepted copy afterwards — not an edit, not a revocation, not deleting the
// source album, not disabling the sender's account.
//
// IMMUTABLE SNAPSHOT
// ------------------
// The manifest (AlbumTransferItem) records the byte identity AND the display
// metadata of every item at SEND time. Acceptance reads nothing from the source
// album, so changes made after the send — rename, reorder, remove, trash,
// withdraw — cannot alter a pending copy, and the snapshot's content cannot be
// substituted after creation.
//
// RETENTION (the part that is easy to get wrong)
// ----------------------------------------------
// A pending transfer must still hold its bytes even if the sender permanently
// deletes every source file. BlobObject.ReferenceCount is DERIVED accounting,
// not the authority (see BlobReferenceAuditService): the authority is the
// enumerated set of tables that own a reference. So album_transfer_items owns a
// real reference per distinct blob, acquired at send and released on
// cancel/decline/expiry, AND it is registered in
// BlobReferenceAuditService.LoadAsync. Without that registration
// `repair-references` would recompute a lower count, zero the transfer's
// reference, and let the janitor delete bytes a pending copy still needs — and
// that command runs on every production deploy.
//
// The BlobObject FK is Restrict on top of the refcount, so even if accounting
// drifted PostgreSQL would refuse to drop a blob a pending transfer references.
public class AlbumTransfer
{
    // Random v4 GUID: 122 bits of entropy, non-sequential and
    // enumeration-resistant. Holding this id alone grants NO media access —
    // every route re-checks that the caller is the sender or the recipient.
    public Guid Id { get; set; }

    // The album the snapshot was taken FROM, recorded for the owner's own
    // history and for audit. Deliberately NOT a foreign key: the source album
    // may be deleted while a transfer is pending or long after one was accepted,
    // and neither Restrict (which would block a legitimate delete) nor Cascade
    // (which would erase the recipient's provenance) is the behaviour we want.
    // Never dereferenced when accepting.
    public Guid SourceAlbumId { get; set; }

    public Guid SenderUserId { get; set; }

    // The intended recipient. Only this user may accept or decline; only the
    // sender may cancel. Enforced in the service, not by the id being secret.
    public Guid RecipientUserId { get; set; }

    // Snapshot of the album's own metadata at send time. Copied to the
    // destination album on acceptance; never re-read from the source.
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    // The chosen cover, as a manifest item rather than a live FileItemId — the
    // source item may be gone by the time the recipient accepts. Null means
    // "derive it", exactly like Album.CoverFileItemId.
    public Guid? CoverTransferItemId { get; set; }

    // Denormalised so the recipient can see what they are being offered without
    // the manifest being readable to them. Shown before acceptance alongside the
    // album title and the sender's public account identifier.
    public int ItemCount { get; set; }

    public long TotalSizeBytes { get; set; }

    // One of AlbumTransferStates.
    public string State { get; set; } = AlbumTransferStates.Pending;

    // Set ONLY on acceptance, to the album created for the recipient. This is
    // what makes acceptance idempotent: a repeated accept finds the row already
    // accepted and returns the SAME album instead of creating a second one.
    // Not a foreign key — the recipient may later delete the copy they were
    // given, and that must not erase the record that the transfer completed.
    public Guid? CreatedAlbumId { get; set; }

    public DateTime CreatedAt { get; set; }

    // After this instant a pending transfer is dead: it can no longer be
    // accepted, and cleanup releases its blob references.
    public DateTime ExpiresAt { get; set; }

    // Set when the recipient accepts or declines.
    public DateTime? RespondedAt { get; set; }

    // Set when the sender cancels a pending transfer.
    public DateTime? CancelledAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

// One snapshotted item. Carries everything acceptance needs, so acceptance never
// touches the source album.
//
// What is deliberately NOT here is as important as what is: no PrivateVaultId,
// no ParentFolderId or source folder path, no person ids, no person names, no
// face assignments, no suggested groups, no private annotations, no album
// memberships, no share or Party links, no audit or download history, no
// MediaLibraryState, no source-owner account data. The recipient receives media,
// not the sender's private semantic layer.
public class AlbumTransferItem
{
    public Guid Id { get; set; }

    public Guid AlbumTransferId { get; set; }

    // Position within the snapshotted album. Dense and zero-based at send time.
    public int SortOrder { get; set; }

    // The physical bytes. This row OWNS one reference to them for as long as the
    // transfer is pending — that is what keeps a pending copy alive when the
    // sender deletes the source.
    public Guid BlobObjectId { get; set; }

    // Recorded for audit and for the sender's own view of what they sent. NEVER
    // dereferenced during acceptance: the whole point of the snapshot is that
    // acceptance does not depend on this row still existing.
    public Guid SourceFileItemId { get; set; }

    // Display and technical metadata needed to view the media, snapshotted.
    public string Name { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    // Safe capture metadata that is already part of the media itself.
    public DateTime EffectiveDateTaken { get; set; }
}

public static class AlbumTransferStates
{
    // Sent; the recipient has not answered and it has not expired. Holds blob
    // references. The only state that can become Accepted.
    public const string Pending = "pending";

    // The recipient accepted. CreatedAlbumId points at their own new album. The
    // copy is fully detached from here on; the transfer row survives only as
    // history.
    public const string Accepted = "accepted";

    // The recipient declined. Blob references released; no album created.
    public const string Declined = "declined";

    // The sender cancelled before the recipient answered. References released.
    public const string Cancelled = "cancelled";

    // The pending window elapsed. References released by cleanup.
    public const string Expired = "expired";

    // Acceptance could not complete for a reason that is not the recipient's
    // decision. References released; no partially visible album is ever left
    // behind, because acceptance is one transaction.
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All =
        [Pending, Accepted, Declined, Cancelled, Expired, Failed];

    // States in which the transfer still owns its blob references and still
    // occupies the (sender, recipient, source album) pending slot.
    public static bool IsLive(string? state) => state == Pending;

    // Terminal states. A transfer here never changes again.
    public static bool IsTerminal(string? state) =>
        state is Accepted or Declined or Cancelled or Expired or Failed;
}

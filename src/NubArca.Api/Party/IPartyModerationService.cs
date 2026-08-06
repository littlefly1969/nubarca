namespace NubArca.Api.Party;

// Owner-side moderation of anonymous party uploads for one album. Every method is
// owner-scoped; a missing/foreign album or a file that is not a guest upload of
// that album collapses to null/false so the HTTP layer maps it to a generic 404.
// This controls VISIBILITY only (PartyUploadItem.Status) — it never deletes the
// owner's stored file/blob.
public interface IPartyModerationService
{
    // Lists every guest upload for the owner's album (any status), newest first,
    // plus the album's current approval-mode. Returns null when the album is
    // missing/foreign (→ 404); an owned album with no guest uploads yields an
    // empty item list.
    Task<PartyUploadListDto?> ListAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    // Sets the moderation status of one guest upload (approved/hidden/rejected).
    // Records ModeratedAt + the acting user. Returns true when it was updated,
    // false when the album/upload was missing or foreign (→ 404).
    Task<bool> SetStatusAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId, string status,
        Guid moderatedByUserId, CancellationToken cancellationToken = default);

    // Best-effort lifecycle hook for normal album removal. If the file is a
    // guest upload for this owner/album, keep the provenance row and mark it as
    // removed from the album. Returns false only when no guest-upload row exists.
    Task<bool> MarkRemovedFromAlbumAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        Guid moderatedByUserId, CancellationToken cancellationToken = default);
}

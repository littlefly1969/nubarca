namespace NubArca.Api.Albums.Sharing;

// SHARE-COPY-01: the one-time detached album copy.
//
// Deliberately a SEPARATE service from IAlbumSharingService with no shared
// state. A live share and a copy answer different questions — "may this person
// look at my album" versus "here, this is yours now" — and keeping them apart is
// what stops a bug in one from silently granting the other. In particular there
// is no code path here that creates, reads or honours an AlbumMembership.
public interface IAlbumTransferService
{
    // ── Sender ──────────────────────────────────────────────────────────────

    // What would be copied, and what would stop it. Uses the SAME eligibility
    // predicate as SendAsync so a clean preview is never followed by a
    // surprising rejection. Null when the caller does not own the album.
    Task<AlbumTransferPreview?> PreviewAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    // Creates the immutable snapshot and acquires one blob reference per item.
    // From this moment the pending copy is independent of the source: later
    // renames, reorders, removals, withdrawals, trashing or even permanent
    // deletion of the source files cannot change what the recipient will get.
    Task<(AlbumTransferSendResult Result, SentAlbumTransferDto? Transfer, IReadOnlyList<AlbumTransferBlocker> Blockers)>
        SendAsync(
            Guid ownerUserId, Guid albumId, string? recipientEmail,
            CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentAlbumTransferDto>> ListSentAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Withdraws a pending offer and releases its blob references. Only the
    // sender may do this, and only while the offer is still pending — a copy
    // the recipient already accepted is theirs and can never be recalled.
    Task<AlbumTransferResponseResult> CancelAsync(
        Guid senderUserId, Guid transferId, CancellationToken cancellationToken = default);

    // ── Recipient ───────────────────────────────────────────────────────────

    Task<IReadOnlyList<ReceivedAlbumTransferDto>> ListReceivedAsync(
        Guid recipientUserId, CancellationToken cancellationToken = default);

    // Materialises the copy: a new album owned by the recipient, with
    // recipient-owned FileItem rows reusing the existing blobs. One transaction
    // under the recipient's tree lock, so there is never a partially visible
    // album. IDEMPOTENT: a repeated accept returns the SAME album rather than
    // creating a second one.
    Task<AlbumTransferAcceptance> AcceptAsync(
        Guid recipientUserId, Guid transferId, CancellationToken cancellationToken = default);

    Task<AlbumTransferResponseResult> DeclineAsync(
        Guid recipientUserId, Guid transferId, CancellationToken cancellationToken = default);

    // ── Maintenance ─────────────────────────────────────────────────────────

    // Marks elapsed pending offers expired and releases their blob references.
    // Idempotent and safe to run repeatedly. Returns how many were expired.
    Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);
}

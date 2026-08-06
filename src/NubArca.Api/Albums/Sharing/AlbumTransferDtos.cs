namespace NubArca.Api.Albums.Sharing;

// SHARE-COPY-01 transport shapes.
//
// Nothing here carries a StorageKey, physical path, BlobId, SHA, raw metadata,
// token hash, source FileItemId, or any part of the sender's private semantic
// layer (person ids, person names, face assignments, suggested groups, private
// annotations). The recipient is being offered MEDIA, not the sender's library.

// Why an album cannot be sent. Returned to the SENDER only — the recipient
// never learns anything about media that was not sent.
//
// CONST STRINGS, not a C# enum, matching AlbumRoles / AlbumMembershipStates /
// AlbumTransferStates. This codebase configures no JsonStringEnumConverter
// anywhere, so an enum here serialises as a NUMBER: the client's
// `case 'ContributedByAnotherUser'` never matches, every refusal silently falls
// through to the generic "no longer available" wording, and the owner is told
// their collaborator's photos went missing rather than that they belong to
// somebody else. Caught in the browser matrix, not by the type checker — the
// TypeScript side declared a string union the server never sent.
public static class AlbumTransferBlockReasons
{
    // A member contributed this item under SHARE-ALBUM-02. It stays theirs and
    // stays revocable; a detached copy would put it permanently beyond their
    // revocation, so it can never ride along in somebody else's copy.
    public const string ContributedByAnotherUser = "ContributedByAnotherUser";

    // In the owner's Private Vault. Vaulted media is excluded from every share
    // surface and must not leave the vault through a copy.
    public const string InPrivateVault = "InPrivateVault";

    // Soft-deleted (in Trash) but still holding its album row.
    public const string Trashed = "Trashed";

    // The album row outlived its file entirely.
    public const string Unavailable = "Unavailable";

    public static readonly IReadOnlyList<string> All =
        [ContributedByAnotherUser, InPrivateVault, Trashed, Unavailable];
}

// One reason the send was refused, with how many items hit it. Counts only —
// naming the blocked files would be the wrong trade for a contributor's item,
// and is not needed to explain the problem.
public sealed record AlbumTransferBlocker(string Reason, int ItemCount);

// What the sender sees BEFORE sending: exactly what would be copied, and
// exactly what stops it. Computed from the same predicate the send uses, so a
// clean preview cannot be followed by a surprising rejection.
public sealed record AlbumTransferPreview(
    string AlbumTitle,
    int EligibleItemCount,
    long EligibleSizeBytes,
    IReadOnlyList<AlbumTransferBlocker> Blockers)
{
    public bool CanSend => Blockers.Count == 0 && EligibleItemCount > 0;
}

public enum AlbumTransferSendResult
{
    Ok,
    // The caller does not own the album, or it does not exist. One result for
    // both so the route cannot be used to probe for album ids.
    AlbumNotFound,
    RecipientNotFound,
    RecipientIsSender,
    // Some item cannot be copied. Never a silent omission — see Blockers.
    ContainsIneligibleItems,
    EmptyAlbum,
    // A pending offer for this (album, recipient) already exists.
    AlreadyPending,
}

// What the SENDER sees about an offer they made.
public sealed record SentAlbumTransferDto(
    Guid Id,
    Guid SourceAlbumId,
    string Title,
    string RecipientDisplayName,
    // Masked, never the full address — same rule as the member list.
    string? RecipientEmailMask,
    int ItemCount,
    long TotalSizeBytes,
    string State,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RespondedAt,
    DateTime? CancelledAt);

// What the RECIPIENT sees about an offer BEFORE deciding. Deliberately the
// minimum needed to make an informed choice, per the slice contract: what it is
// called, how much of it there is, and who it is from.
//
// SourceAlbumId is absent: it is the sender's internal handle and tells the
// recipient nothing. So is anything about the media itself — a pending offer
// grants NO access to any byte, and the manifest is not readable until the
// recipient owns the copy.
public sealed record ReceivedAlbumTransferDto(
    Guid Id,
    string Title,
    string? Description,
    string SenderDisplayName,
    string? SenderEmailMask,
    int ItemCount,
    long TotalSizeBytes,
    string State,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    // Set once accepted, so the client can navigate straight to the new album.
    Guid? CreatedAlbumId);

public enum AlbumTransferResponseResult
{
    Ok,
    // Not addressed to this user, or no such transfer. One result for both:
    // a transfer id must never confirm its own existence to a stranger.
    NotFound,
    // Already answered. Accept is IDEMPOTENT — a repeat accept returns Ok with
    // the SAME album rather than this — so this is only for a genuine
    // contradiction, e.g. declining something already accepted.
    AlreadyResolved,
    Expired,
    Cancelled,
    // The sender's account was disabled after the offer was made. Disablement
    // may be a response to a compromised account, so an operation that account
    // originated must not be allowed to complete afterwards. Already-accepted
    // copies are untouched — those are the recipient's own albums.
    SenderUnavailable,
    // Accepting would push the recipient past their quota. Nothing is created:
    // acceptance is one transaction, so there is no partial album to clean up.
    QuotaExceeded,
}

public sealed record AlbumTransferAcceptance(
    AlbumTransferResponseResult Result,
    Guid? CreatedAlbumId,
    // Populated on QuotaExceeded so the recipient is told how much they would
    // need — logical bytes only, never physical or deduplicated figures.
    long? RequiredBytes = null,
    long? RemainingBytes = null);

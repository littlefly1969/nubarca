namespace NubArca.Api.ShareLinks;

public interface IShareLinkService
{
    // Returns the new share link's id + raw token (returned to caller once),
    // or null when the file does not exist / is foreign / is soft-deleted.
    Task<ShareLinkCreationResult?> CreateAsync(
        Guid ownerUserId,
        Guid fileItemId,
        DateTime? expiresAt,
        int? maxDownloads,
        CancellationToken cancellationToken = default);

    // Returns true if the link was found and is now revoked; false if no such
    // link exists for this owner (treat as 404 in HTTP layer).
    Task<bool> RevokeAsync(
        Guid ownerUserId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default);

    // Atomically validates + increments DownloadCount + sets LastAccessedAt.
    // Returns the file + its owner on success, or null when the token is
    // unknown / revoked / expired / exhausted.
    Task<ShareLinkConsumeResult?> ConsumeAsync(
        string token,
        CancellationToken cancellationToken = default);

    // Lists every share link (active, revoked, expired, exhausted) that the
    // owner created for the given file. Returns null when the file does not
    // exist / is foreign / is soft-deleted (caller maps to 404). An owned
    // active file with no links returns an empty list, not null.
    Task<IReadOnlyList<ShareLinkSummary>?> ListByFileAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default);

    // Owner-scoped global listing across every file the caller owns, paginated
    // and filterable by status. Always returns an envelope (never null) — an
    // owner with no links gets an empty page with Total = 0.
    Task<ShareLinkListResponse> ListForOwnerAsync(
        Guid ownerUserId,
        ShareLinkStatusFilter status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
}

public sealed record ShareLinkCreationResult(
    Guid Id,
    string Token,
    DateTime? ExpiresAt,
    int? MaxDownloads);

public sealed record ShareLinkConsumeResult(Guid FileItemId, Guid OwnerUserId);

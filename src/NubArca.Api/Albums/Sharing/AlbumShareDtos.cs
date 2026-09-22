using NubArca.Api.Domain;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// What a resolved public token grants. Built once at the seam and handed
/// downstream, so no endpoint walks from a token to an album a second time and
/// none of them can disagree about what the link allows.
/// </summary>
public sealed record AlbumShareAccess(
    Guid LinkId,
    Guid AlbumId,
    Guid OwnerUserId,
    bool UploadEnabled,
    bool AllowOriginalDownload,
    int MaxUploads,
    int UploadCount,
    bool RequireSecondFactor,
    /// <summary>Who proved themselves, when the second factor is on. Null otherwise.</summary>
    Guid? GuestId)
{
    /// <summary>Whether another file may be accepted right now.</summary>
    public bool HasUploadRoom => MaxUploads == 0 || UploadCount < MaxUploads;
}

/// <summary>
/// Why a token did not become a grant.
///
/// <para><see cref="NeedsSecondFactor"/> is deliberately NOT collapsed into
/// <see cref="NotFound"/>, unlike every other public Party refusal. A party
/// hides whether a token exists because a stranger has no business learning
/// that; here the caller already holds the link, so hiding it would only mean
/// showing a 404 to somebody the owner expects — and the page would have
/// nothing to offer them.</para>
/// </summary>
public enum AlbumShareResolution
{
    NotFound,
    NeedsSecondFactor,
    Granted,
}

public sealed record AlbumShareResolved(AlbumShareResolution Kind, AlbumShareAccess? Access)
{
    public static readonly AlbumShareResolved NotFound = new(AlbumShareResolution.NotFound, null);
    public static AlbumShareResolved NeedsSecondFactor() =>
        new(AlbumShareResolution.NeedsSecondFactor, null);
    public static AlbumShareResolved Granted(AlbumShareAccess access) =>
        new(AlbumShareResolution.Granted, access);
}

// --- OWNER SIDE ---------------------------------------------------------

/// <summary>
/// The owner's view of their album's link.
///
/// <para><c>Url</c> is DERIVED on read rather than stored: the raw token is not
/// in the database, so this is the only place it exists, and it exists only for
/// a caller who has already proved the album is theirs.</para>
/// </summary>
public sealed record AlbumShareLinkDto(
    Guid Id,
    string Url,
    bool Enabled,
    bool UploadEnabled,
    bool AllowOriginalDownload,
    bool RequireSecondFactor,
    string? Label,
    int MaxUploads,
    int UploadCount,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    IReadOnlyList<AlbumShareGuestDto> Guests);

public sealed record AlbumShareGuestDto(
    Guid Id, string Email, string? DisplayName, DateTime CreatedAt);

/// <summary>
/// A change to one link. Every field is nullable and omitted means UNCHANGED,
/// so a client that knows about two switches cannot turn off a third it has
/// never heard of by saving the form it does know.
/// </summary>
public sealed record AlbumShareUpdateRequest(
    bool? UploadEnabled = null,
    bool? AllowOriginalDownload = null,
    bool? RequireSecondFactor = null,
    string? Label = null,
    int? MaxUploads = null,
    DateTime? ExpiresAt = null);

public sealed record AlbumShareGuestRequest(string? Email, string? DisplayName);

// --- PUBLIC SIDE --------------------------------------------------------

/// <summary>
/// The album as somebody holding the link sees it.
///
/// <para>It names the album and says what may be done, and nothing else. No
/// owner, no storage key, no blob id, no party — an album that happens to have
/// an evening attached does not mention it here, because this link is not that
/// link and a visitor following one has not been given the other.</para>
/// </summary>
public sealed record AlbumSharePublicDto(
    string AlbumName,
    string? CoverUrl,
    int ItemCount,
    bool CanUpload,
    /// <summary>Whether a download hands over the real file or a safe rendition.</summary>
    bool CanDownloadOriginal,
    /// <summary>Null when the link has no ceiling; otherwise what is left.</summary>
    int? UploadsRemaining);

public sealed record AlbumShareItemDto(
    Guid Id, string ThumbnailUrl, string PreviewUrl, string DownloadUrl, bool IsVideo);

public sealed record AlbumShareItemsDto(
    IReadOnlyList<AlbumShareItemDto> Items, string? NextCursor);

public sealed record AlbumShareChallengeRequest(string? Email);
public sealed record AlbumShareVerifyRequest(string? Email, string? Code);

public enum AlbumShareUploadOutcome
{
    Accepted,
    Refused,
    UploadsDisabled,
    LimitReached,
}

public sealed record AlbumShareUploadResult(AlbumShareUploadOutcome Outcome)
{
    public string? ErrorCode => Outcome switch
    {
        AlbumShareUploadOutcome.UploadsDisabled => AlbumShareErrors.UploadsDisabled,
        AlbumShareUploadOutcome.LimitReached => AlbumShareErrors.UploadLimitReached,
        _ => null,
    };
}

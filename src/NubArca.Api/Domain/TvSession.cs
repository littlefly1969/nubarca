namespace NubArca.Api.Domain;

public class TvSession
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string SessionTokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? DeviceLabel { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>
    /// WHAT this television shows — deliberately not the same question as WHO it
    /// is.
    ///
    /// <para>Pairing answers the identity: the session token proves this device
    /// belongs to this owner, and it is a credential. This is ordinary mutable
    /// state beside it, so an owner can move a television between the general
    /// NubArca TV experience and a specific party without another PIN, another
    /// QR code and another walk to the television.</para>
    ///
    /// <para><see cref="TvDisplayAssignments.General"/> is the default and the
    /// only value a device paired before this existed can have — which is
    /// exactly the behaviour it already had.</para>
    /// </summary>
    public string DisplayAssignment { get; set; } = TvDisplayAssignments.General;

    /// <summary>
    /// The party this television is showing, when it is showing one. Null for
    /// <see cref="TvDisplayAssignments.General"/>, and a check constraint holds
    /// both halves of that: a party assignment always names a link, a general
    /// one never does.
    ///
    /// <para>It names the LINK rather than the album, because the link is what a
    /// party IS everywhere else in this feature: re-enabling party mode mints a
    /// new one, which is what keeps last year's greetings away from this year's
    /// television. A television assigned to a party that was revoked is
    /// therefore assigned to something that is over, and says so, rather than
    /// silently adopting whatever party the album has next.</para>
    /// </summary>
    public Guid? AssignedPartyAlbumLinkId { get; set; }

    // Personal Area PIN brute-force state, per paired session. Consecutive
    // failures accumulate; past the free-attempt threshold each further failure
    // sets a progressive PersonalPinLockedUntil cooldown (bounded — never a
    // permanent lockout). Reset on a successful unlock. Persisted so a client
    // restart cannot clear the throttle.
    public int PersonalPinFailedAttempts { get; set; }
    public DateTime? PersonalPinLockedUntil { get; set; }
}

/// <summary>
/// What a paired television is for. Wire values, so they are stable strings
/// rather than an enum whose ordinals a client could come to depend on.
/// </summary>
public static class TvDisplayAssignments
{
    /// The ordinary NubArca TV experience: the owner's albums, photos and
    /// slideshow. The default, and what every device paired before assignments
    /// existed keeps.
    public const string General = "general";

    /// One specific party, named by <see cref="TvSession.AssignedPartyAlbumLinkId"/>.
    public const string Party = "party";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([General, Party], StringComparer.Ordinal);

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

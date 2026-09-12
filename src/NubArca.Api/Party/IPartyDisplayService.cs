using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// A television's permission to SHOW the party it is assigned to.
///
/// <para>This is the third capability class in Party, beside the OWNER's cookie
/// and the GUEST's token, and it exists because a display is neither. The
/// public party token opens the hub, uploads, prints and the whole visitor
/// surface; a screen in the corner of a room needs one read and should hold
/// nothing else. So the television authenticates as a DEVICE, the server
/// resolves what it is assigned to, and what comes back grants exactly that.
/// </para>
///
/// <para>The client never names a party. It cannot: the assignment is
/// server-side state, and a link id is an internal identifier no caller is
/// trusted with.</para>
/// </summary>
public interface IPartyDisplayService
{
    /// <summary>
    /// Mints a display grant for the television presenting this session token.
    ///
    /// <para>Resolves the assignment server-side — session → `party` assignment
    /// → link — and refuses a television that is GENERAL, unassigned, revoked,
    /// expired, or pointed at a party that is no longer available. The raw
    /// token is returned ONCE and never stored; only its SHA-256 is.</para>
    ///
    /// <para>Minting revokes this television's previous grants, so a remount
    /// cannot leave a second usable credential behind — and it does so under a
    /// write lock on the television's own session row, so two mints of the
    /// same device arriving together are ORDERED rather than interleaved and
    /// the loser's revoke sees the winner's grant. Two different televisions
    /// lock two different rows and never wait on each other.</para>
    ///
    /// <para>The shell renews BEFORE expiry, by minting again: renewal is not a
    /// separate operation, and there is no way to extend a grant.</para>
    /// </summary>
    Task<PartyDisplayGrantResult> MintAsync(
        string? sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a raw grant to the party it may show, or null.
    ///
    /// <para>EXPIRY IS NOT THE REVOCATION BOUNDARY. Every one of these is
    /// re-read here, on every request: the grant is unrevoked and unexpired,
    /// its television still exists and is neither revoked nor expired, that
    /// television is still assigned to a PARTY, the assignment still names THIS
    /// link, and the party behind the link still resolves. Un-pairing a
    /// television or pointing it somewhere else therefore kills the grant in
    /// the same instant, long before <c>ExpiresAt</c> would.</para>
    /// </summary>
    Task<PartyAccess?> ResolveAsync(
        string? grantToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every live grant of one television. Idempotent. Called when the
    /// television's assignment changes or its session ends, so a grant minted
    /// for the previous party can never be replayed against the next one.
    /// </summary>
    Task<int> RevokeForSessionAsync(
        Guid tvSessionId, CancellationToken cancellationToken = default);
}

public enum PartyDisplayGrantError
{
    /// No session, or one that is revoked or expired. One generic answer.
    NoSession,

    /// The television is paired but not pointed at a party — or is pointed at
    /// one that has been revoked, ended or switched off. Both are the same
    /// answer, because a display has nothing to do in either case.
    NotAssigned,
}

/// <summary>
/// A minted grant. <c>Token</c> is the only time the raw value exists outside
/// the television that asked for it.
/// </summary>
public sealed record PartyDisplayGrantResult(
    string? Token,
    DateTime? ExpiresAt,
    PartyDisplayGrantError? Error)
{
    public static PartyDisplayGrantResult Ok(string token, DateTime expiresAt) =>
        new(token, expiresAt, null);
    public static PartyDisplayGrantResult Fail(PartyDisplayGrantError error) =>
        new(null, null, error);
}

/// What the television is handed. A lifetime and a secret, and nothing that
/// identifies the party — the grant IS the reference to it.
///
/// `ExpiresInSeconds` is the same lifetime as a DURATION, computed on the
/// server. The shell schedules its renewal from it rather than by subtracting
/// its own clock from `ExpiresAt`: a television whose clock is wrong by an hour
/// would otherwise renew in a loop, or let the grant lapse — the same reason
/// the control room is sent `displaySeenSecondsAgo` rather than a timestamp.
public sealed record PartyDisplayGrantDto(string Grant, DateTime ExpiresAt, int ExpiresInSeconds);

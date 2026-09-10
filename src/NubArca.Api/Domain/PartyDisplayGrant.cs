namespace NubArca.Api.Domain;

/// <summary>
/// A television's permission to SHOW one party, and nothing else.
///
/// <para>A display is not a guest. The public party token is a guest
/// capability — it opens the hub, uploads, prints and the whole visitor
/// surface — and handing one to a screen in the corner of a room would give it
/// powers it has no use for and no way to protect. This grant does one thing:
/// it reads the party its television is assigned to.</para>
///
/// <para>Same scheme as <see cref="TvPersonalUnlockGrant"/>, deliberately: a
/// 256-bit token minted server-side, returned exactly once, and stored only as
/// its SHA-256. A stolen database cannot impersonate a display.</para>
///
/// <para><b>Expiry is not the revocation boundary.</b> The lifetime keeps a
/// leaked token from being useful for ever; what actually decides validity is
/// re-read on EVERY use — the session must still be live, the television must
/// still be assigned to a party, that assignment must still be the link this
/// grant names, and the party itself must still be there. So un-pairing a
/// television, or pointing it at a different party, kills the grant in the same
/// instant rather than at <see cref="ExpiresAt"/>.</para>
/// </summary>
public class PartyDisplayGrant
{
    public Guid Id { get; set; }

    /// The television this grant was minted for. It is bound to the DEVICE, not
    /// to the owner: an owner with two televisions has two grants, and revoking
    /// one screen never darkens the other.
    public Guid TvSessionId { get; set; }

    /// The party this grant may show. Bound to the LINK rather than the album
    /// for the same reason the assignment is: re-enabling party mode mints a new
    /// link, and a new party is a new party.
    public Guid PartyAlbumLinkId { get; set; }

    /// SHA-256 hex of the raw token. The raw value is returned once, to the
    /// television that asked, and never stored.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

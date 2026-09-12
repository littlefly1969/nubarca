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
///
/// <para><b>It exists only while the game holds the screen</b>, and the
/// server enforces that rather than trusting the television to stop: a grant
/// is minted, and honoured on every display request, only while the party's
/// projected presentation is `game` — the same projection the control plane
/// sends. When the presentation returns to the native slideshow (game switched
/// off, party no longer live, a finished game's closing card over) a grant
/// still in someone's hands stops authorising at once; the next takeover mints
/// a fresh one, which revokes it. The shell renews it before <see cref="ExpiresAt"/> by
/// minting — which revokes this row — and re-mints on any display 401, so an
/// evening longer than one lifetime needs nobody at the television.</para>
///
/// <para>At most ONE row per television is unrevoked at any time. Minting
/// revokes the previous one under a write lock on the television's session
/// row, so two mints racing each other are ordered rather than interleaved
/// (see <c>PartyDisplayService.MintAsync</c>).</para>
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

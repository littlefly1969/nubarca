namespace NubArca.Api.Domain;

/// <summary>
/// The FESTA itself — the aggregate root of the Party product.
///
/// <para>Party is deliberately none of the three things it used to be confused
/// with. It is not <see cref="PartyAlbumLink"/>, which is a public capability
/// over a party and may be minted, revoked and rotated many times for the same
/// event. It is not <see cref="Album"/>, which is a collection of media the
/// owner may also use for anything else. And it is not "an album with
/// ShowOnTv": a party can be held without a television in the room.</para>
///
/// <para>Its media arrive through <see cref="PartyMediaSource"/> rather than a
/// <c>MediaAlbumId</c> column, so a party may draw on more than one album
/// without the schema having to change again. P1 gives semantics to exactly one
/// role — <see cref="PartyMediaSourceRoles.Main"/> — and the rest of the system
/// keeps working on <c>(ownerUserId, albumId)</c> exactly as before: the main
/// album is resolved ONCE at the public seam and handed downstream.</para>
///
/// <para><b>Status is descriptive in P1, not an access gate.</b> What a guest
/// may do is still decided by the capability they present (the link's
/// <c>Enabled</c>, <c>RevokedAt</c>, <c>ExpiresAt</c>) and by the owner's role.
/// Making the status a second, parallel gate would give two places the power to
/// close a party and no single answer to why one is closed.</para>
/// </summary>
public class Party
{
    public Guid Id { get; set; }

    /// <summary>
    /// Whose party it is. Every downstream query is still owner-scoped, so this
    /// is the same authority the album carries, stated once at the root.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// One of <see cref="PartyStatuses"/>. A validated application string rather
    /// than a PostgreSQL enum, matching <see cref="PartyUploadStatuses"/> and
    /// <see cref="PartyMessageStatuses"/>: a closed database enum turns adding a
    /// state into a migration on every installation, and the stored value stays
    /// readable in a plain query.
    /// </summary>
    public string Status { get; set; } = PartyStatuses.Draft;

    /// <summary>
    /// When the host says the event begins. SCHEDULING information: nothing
    /// automatic reads the clock and moves the party, because a party that
    /// started itself because a laptop's timezone was wrong is worse than one
    /// nobody pressed start on.
    /// </summary>
    public DateTime? EventStartsAt { get; set; }

    /// <summary>Set by an actual <c>StartLive</c> transition, never by a clock.</summary>
    public DateTime? LiveStartedAt { get; set; }

    /// <summary>Set by an actual <c>EndLive</c> transition, never by a clock.</summary>
    public DateTime? LiveEndedAt { get; set; }

    /// <summary>
    /// A hard stop for GUEST access to this party, independent of any one
    /// link's own expiry. Enforced at the public seam, so setting it closes
    /// every capability of the party at once rather than one QR at a time.
    /// Null — which is every party P1 can create — means the link's own rules
    /// are the whole answer, exactly as before.
    /// </summary>
    public DateTime? GuestAccessExpiresAt { get; set; }

    /// <summary>
    /// Reserved for the post-event library the guests keep. There is no library
    /// surface in P1, so nothing reads this yet; it is carried because the
    /// column belongs to the party rather than to whichever slice builds that
    /// surface, and it is deliberately NOT enforced anywhere, so no reader can
    /// mistake it for a live rule.
    /// </summary>
    public DateTime? LibraryAccessExpiresAt { get; set; }

    /// <summary>
    /// Optimistic concurrency, the same plain incrementing int
    /// <see cref="Album"/> uses and for the same reason: PostgreSQL's xmin
    /// cannot be exercised by the SQLite the endpoint tests run against, and a
    /// token no test can exercise is a token nobody can prove works.
    /// </summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The party's lifecycle states. Four, and no <c>Archived</c>: a state with no
/// transition that produces it and no behaviour that reads it is a column
/// nobody can explain later.
/// </summary>
public static class PartyStatuses
{
    /// <summary>Being prepared. Not announced.</summary>
    public const string Draft = "draft";

    /// <summary>Announced: guests may be given the QR.</summary>
    public const string Published = "published";

    /// <summary>The event is happening now.</summary>
    public const string Live = "live";

    /// <summary>The event is over.</summary>
    public const string Ended = "ended";

    public static bool IsKnown(string? status) =>
        status is Draft or Published or Live or Ended;
}

public enum PartyLifecycleAction
{
    Publish,
    StartLive,
    EndLive,
}

/// <summary>
/// The WHOLE of the party's lifecycle rule, as a pure function.
///
/// <para>Written the same way as <see cref="PartyMessageTransitions"/>: the
/// caller names an ACTION and the domain decides the target, so no endpoint or
/// service ever picks a status of its own. Three moves exist; everything else —
/// including a second <c>Publish</c> on something already published — is
/// refused, because a transition that quietly succeeds when it changed nothing
/// makes "did the host start the party" unanswerable.</para>
///
/// <para>The game's own lifecycle is deliberately absent. <c>Game.Start()</c>
/// and <c>Game.Finish()</c> move a match, not the evening around it: a host may
/// play three rounds during one party and the party is Live throughout.</para>
/// </summary>
public static class PartyLifecycle
{
    public static string? Target(string? current, PartyLifecycleAction action) =>
        (current, action) switch
        {
            (PartyStatuses.Draft, PartyLifecycleAction.Publish) => PartyStatuses.Published,
            (PartyStatuses.Published, PartyLifecycleAction.StartLive) => PartyStatuses.Live,
            (PartyStatuses.Live, PartyLifecycleAction.EndLive) => PartyStatuses.Ended,
            _ => null,
        };
}

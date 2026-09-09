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
/// <para><b>Status selects the SURFACE; it does not grant or revoke access.</b>
/// Whether a guest gets in is decided by the capability they present (the link's
/// <c>Enabled</c>, <c>RevokedAt</c>, <c>ExpiresAt</c>), by the owner's role and
/// by the party's own windows. What the status decides is which of the three
/// experiences they are shown — invitation, party, memories — and therefore
/// which capabilities are part of it; see <see cref="PartyGuestExperience"/>. A
/// status never revokes a token, and a token is never invalid merely because the
/// party has not started or has finished: the SAME QR carries a guest from the
/// invitation to the memories.</para>
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
    /// When the FULL guest experience ends — the party's own window, independent
    /// of any one link's expiry, so setting it closes every capability at once
    /// rather than one QR at a time. Null means the link's own rules are the
    /// whole answer.
    ///
    /// <para>It does not necessarily end the guest's visit: once the party is
    /// over, <see cref="LibraryAccessExpiresAt"/> may keep the memories open on
    /// the same QR. Read only through <see cref="PartyGuestExperience"/>.</para>
    /// </summary>
    public DateTime? GuestAccessExpiresAt { get; set; }

    /// <summary>
    /// When the MEMORIES stop being reachable — a different decision from when
    /// the guest experience does.
    ///
    /// <para>Null does not create a second window: the memories simply last as
    /// long as guest access does. A value may OUTLIVE
    /// <see cref="GuestAccessExpiresAt"/>, which is the whole point of the After
    /// surface — the same QR keeps working, and what it opens narrows to the
    /// album and a thank-you. It may also fall short of it, in which case the
    /// memories close first and the greeting stays.</para>
    ///
    /// <para>Read only through <see cref="PartyGuestExperience"/>, which is the
    /// one place either window is interpreted.</para>
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

/// <summary>
/// What an owner may type into a party, and how much of it.
///
/// <para>Kept beside the entity so the EF configuration, the API validator and
/// the client contract quote ONE set of numbers — the same reason
/// <see cref="PartySlideshowDefaults"/> lives where it does. Measured in
/// Unicode code points, the one unit .NET and a browser agree on exactly, so a
/// title of astral characters is counted the way the person typing it counts
/// it.</para>
/// </summary>
public static class PartyTextLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;

    /// <summary>
    /// Trimmed, or null when there was nothing but whitespace. Trimming is the
    /// whole normalisation: a party title is a name somebody chose, not a
    /// message body, so the aggressive format-character stripping
    /// <c>PartyMessageText</c> does would be wrong here.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool IsValidTitle(string? normalized) =>
        normalized is not null && CodePoints(normalized) <= MaxTitleLength;

    public static bool IsValidDescription(string? normalized) =>
        normalized is null || CodePoints(normalized) <= MaxDescriptionLength;

    private static int CodePoints(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }
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

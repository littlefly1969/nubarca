namespace NubArca.Api.Domain;

/// <summary>
/// One album a party draws its media from.
///
/// <para>This table is the reason <see cref="Party"/> has no
/// <c>MediaAlbumId</c>. A wedding has the photographer's album, the guests'
/// contributions and the couple's own selection; a column could hold exactly
/// one of them, and discovering that later is a migration on every
/// installation. A row per album costs nothing today and is the difference
/// between "add another album" and "change the schema again".</para>
///
/// <para><b>P1 implements one role.</b> Exactly one
/// <see cref="PartyMediaSourceRoles.Main"/> source per party is created, and
/// the public seam resolves it to the album every existing service already
/// works on. <see cref="Role"/> is a validated application string, not a
/// PostgreSQL enum, so <c>official</c>, <c>guest-contributions</c> and
/// <c>selected-memories</c> become code rather than schema when they are
/// actually built.</para>
/// </summary>
public class PartyMediaSource
{
    public Guid PartyId { get; set; }

    public Guid AlbumId { get; set; }

    /// <summary>One of <see cref="PartyMediaSourceRoles"/>.</summary>
    public string Role { get; set; } = PartyMediaSourceRoles.Main;

    /// <summary>
    /// Presentation order within a role. A tie-break the host controls, so two
    /// sources added in the same second have a stable order rather than
    /// whichever the database happened to return.
    /// </summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// What an album IS to a party. Only <see cref="Main"/> carries behaviour in
/// P1; the others are named here so the vocabulary is one list rather than a
/// string typed in three services, and are deliberately not accepted yet —
/// validation that let an unimplemented role be stored would produce parties no
/// resolver can read.
/// </summary>
public static class PartyMediaSourceRoles
{
    /// <summary>
    /// The party's own album: what guests see, upload into, print from and
    /// search. Every party has exactly one.
    /// </summary>
    public const string Main = "main";

    /// <summary>The roles P1 will actually store.</summary>
    public static bool IsSupported(string? role) => role == Main;
}

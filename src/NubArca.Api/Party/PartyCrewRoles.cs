using NubArca.Api.Access;

namespace NubArca.Api.Party;

/// <summary>
/// What a collaborator may do on a party, as a closed vocabulary.
///
/// <para><b>These are not permissions.</b> They are never added to
/// <c>PermissionCatalog</c>, never appear on a role, and can never be held by a
/// <c>User</c>. The prefix says so on every line: a key beginning
/// <c>party-crew.</c> is party-local authority and means nothing outside one
/// party.</para>
///
/// <para>Each one names a JOB rather than an endpoint, because the point is to
/// be able to say "the director runs the evening but never reads the guest
/// list" and have that be a true statement about the system.</para>
/// </summary>
public static class PartyCrewCapabilities
{
    /// The party's own facts: its name, its date, its description.
    public const string DetailsManage = "party-crew.details.manage";

    /// Moving the evening on: start it, end it.
    public const string LifecycleManage = "party-crew.lifecycle.manage";

    /// What the guests read: the invitation and the other typed sections.
    public const string ExperienceManage = "party-crew.experience.manage";

    /// <summary>
    /// Reading the guest list — WITH the names, addresses and notes on it.
    ///
    /// <para>This is the PII key, and it is the one the director deliberately
    /// does not hold. Everything a director needs to run the room is an
    /// aggregate; nothing about conducting an evening requires knowing who is
    /// allergic to shellfish.</para>
    /// </summary>
    public const string GuestsRead = "party-crew.guests.read";

    /// Writing the guest list and handing out personal invitations.
    public const string InvitationsManage = "party-crew.invitations.manage";

    /// The door: recording who arrived, and correcting it.
    public const string AttendanceManage = "party-crew.attendance.manage";

    /// Whether guests may contribute photographs at all, and their limits.
    public const string ContributionsConfigure = "party-crew.contributions.configure";

    /// Deciding what a guest sent becomes public. Separate from configuring the
    /// channel, for the reason the owner product already separates them: closing
    /// the channel must never lock somebody out of the queue it filled.
    public const string ContributionsModerate = "party-crew.contributions.moderate";

    /// Preparing the game: the deck, the settings.
    public const string ActivitiesManage = "party-crew.activities.manage";

    /// Running the game, live, in front of people.
    public const string ActivitiesControl = "party-crew.activities.control";

    /// The screen in the room.
    public const string ScreensManage = "party-crew.screens.manage";

    /// The printer by the door.
    public const string PrintManage = "party-crew.print.manage";

    /// <summary>Every capability the product knows, for validation.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DetailsManage, LifecycleManage, ExperienceManage,
        GuestsRead, InvitationsManage, AttendanceManage,
        ContributionsConfigure, ContributionsModerate,
        ActivitiesManage, ActivitiesControl,
        ScreensManage, PrintManage,
    };

    public static bool IsKnown(string? key) => key is not null && All.Contains(key);

    /// <summary>
    /// The OWNER permission a crew capability also requires.
    ///
    /// <para>A capability is never more than a filter over what the host may
    /// already run. If the host's own role loses <c>party.games</c>, the
    /// director's <c>activities.control</c> stops working on the next request —
    /// with nobody re-pairing and no grant rewritten. Delegation cannot exceed
    /// its source.</para>
    ///
    /// <para>The keys with no entry require only <see cref="Permissions.PartyAccess"/>,
    /// which every crew request checks regardless.</para>
    /// </summary>
    public static string? OwnerPermissionFor(string capabilityKey) => capabilityKey switch
    {
        ContributionsConfigure => Permissions.PartyContributions,
        // Moderating what guests already left is party.access, exactly as it is
        // for the owner: the host who closed the channel keeps their queue.
        ContributionsModerate => null,
        ActivitiesManage or ActivitiesControl => Permissions.PartyGames,
        PrintManage => Permissions.PartyPrint,
        _ => null,
    };
}

/// <summary>
/// The five roles, their presets, and which of them an owner may actually pick.
///
/// <para><b>Presets, not an ACL editor.</b> A per-collaborator capability
/// editor would make every party's delegation a bespoke configuration nobody
/// could review, and would make "what can a director do" unanswerable. A role
/// is one decision, and changing it replaces the whole set.</para>
///
/// <para>Three of the five are <b>predisposed</b>: they exist in the domain,
/// they have presets, the resolver honours them and the database accepts them —
/// but <see cref="IsAssignable"/> is false, so the owner surface does not offer
/// them. Turning DJ on later is a product decision, not a migration.</para>
/// </summary>
public static class PartyCrewRoles
{
    public const string CoOrganizer = "co_organizer";
    public const string Director = "director";
    public const string Dj = "dj";
    public const string Reception = "reception";
    public const string Honoree = "honoree";

    /// <summary>
    /// The co-organizer: everything the owner delegates, which is everything
    /// about running this party and nothing about owning it.
    ///
    /// <para>What is absent is absent by construction rather than by a check
    /// somewhere: there is no capability for changing the main media source,
    /// duplicating, tearing down, or managing collaborators, so no preset can
    /// contain one.</para>
    /// </summary>
    private static readonly string[] CoOrganizerPreset =
    [
        PartyCrewCapabilities.DetailsManage,
        PartyCrewCapabilities.LifecycleManage,
        PartyCrewCapabilities.ExperienceManage,
        PartyCrewCapabilities.GuestsRead,
        PartyCrewCapabilities.InvitationsManage,
        PartyCrewCapabilities.AttendanceManage,
        PartyCrewCapabilities.ContributionsConfigure,
        PartyCrewCapabilities.ContributionsModerate,
        PartyCrewCapabilities.ActivitiesManage,
        PartyCrewCapabilities.ActivitiesControl,
        PartyCrewCapabilities.ScreensManage,
        PartyCrewCapabilities.PrintManage,
    ];

    /// <summary>
    /// The director: runs the evening, and cannot read who is at it.
    ///
    /// <para>No <see cref="PartyCrewCapabilities.GuestsRead"/>, no
    /// <see cref="PartyCrewCapabilities.InvitationsManage"/>, no
    /// <see cref="PartyCrewCapabilities.AttendanceManage"/>. A director sees the
    /// aggregates the regia needs — how many are present, what is playing — and
    /// never a name, an address, a telephone number, an RSVP or a dietary note.
    /// Hiding the tab would not be that; not holding the key is.</para>
    /// </summary>
    private static readonly string[] DirectorPreset =
    [
        PartyCrewCapabilities.LifecycleManage,
        // MODERATING is not CONFIGURING. A director decides what stays up
        // tonight; whether guests may upload at all, and whether what they
        // upload needs approving first, is the host's standing decision about
        // their own party and their own library. The surface hides those
        // switches from a role that does not hold this, rather than the role
        // growing to fit the surface.
        PartyCrewCapabilities.ContributionsModerate,
        PartyCrewCapabilities.ActivitiesManage,
        PartyCrewCapabilities.ActivitiesControl,
        PartyCrewCapabilities.ScreensManage,
        PartyCrewCapabilities.PrintManage,
    ];

    /// Predisposed: music and the screen, nothing else.
    private static readonly string[] DjPreset =
    [
        PartyCrewCapabilities.ActivitiesControl,
        PartyCrewCapabilities.ScreensManage,
    ];

    /// Predisposed: the door, and the list it is checked against.
    private static readonly string[] ReceptionPreset =
    [
        PartyCrewCapabilities.GuestsRead,
        PartyCrewCapabilities.AttendanceManage,
    ];

    /// Predisposed: the person the party is for, shaping what it says.
    private static readonly string[] HonoreePreset =
    [
        PartyCrewCapabilities.DetailsManage,
        PartyCrewCapabilities.ExperienceManage,
        PartyCrewCapabilities.ContributionsModerate,
        PartyCrewCapabilities.ActivitiesManage,
    ];

    private static readonly Dictionary<string, string[]> Presets = new(StringComparer.Ordinal)
    {
        [CoOrganizer] = CoOrganizerPreset,
        [Director] = DirectorPreset,
        [Dj] = DjPreset,
        [Reception] = ReceptionPreset,
        [Honoree] = HonoreePreset,
    };

    /// <summary>Roles the owner surface offers in THIS release.</summary>
    private static readonly HashSet<string> Assignable =
        new(StringComparer.Ordinal) { CoOrganizer, Director };

    /// <summary>Every role the domain knows, assignable or not.</summary>
    public static IReadOnlyCollection<string> All => Presets.Keys;

    public static bool IsKnown(string? roleKey) => roleKey is not null && Presets.ContainsKey(roleKey);

    /// <summary>
    /// Whether an OWNER may choose this role today. The resolver still honours a
    /// non-assignable role that somehow exists, because refusing to authorize a
    /// row the database holds would be a worse failure than not offering it.
    /// </summary>
    public static bool IsAssignable(string? roleKey) => roleKey is not null && Assignable.Contains(roleKey);

    /// <summary>The capabilities a role grants. Empty for an unknown role — never everything.</summary>
    public static IReadOnlyList<string> Preset(string roleKey) =>
        Presets.TryGetValue(roleKey, out var preset) ? preset : [];
}

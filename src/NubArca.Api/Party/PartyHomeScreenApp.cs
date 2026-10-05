using NubArca.Api.HomeScreen;

namespace NubArca.Api.Party;

/// <summary>
/// A party on a guest's home screen: the web app manifest of one party and the
/// icon drawn from the party's cover.
///
/// <para>ONE PARTY, ONE APP, ITS OWN SCOPE. Every party's app lives under its
/// own canonical path, <c>/party/app/&lt;key&gt;/</c>, the key a one-way digest
/// of the party's id: that path is the app's id and its scope. Scopes of two
/// parties never overlap, so installing one party never claims another's
/// links — Android hands a link to the installed app whose scope contains it,
/// and judges "already installed" by it too, which is why a scope of all of
/// <c>/party/</c> made every party after the first look installed.</para>
///
/// <para>The id authorises nothing: no link's token is in it, it survives a
/// link rotated or revoked, and it is the same for the party's page and every
/// invitation to it — they are one app, whose pages (the invitation leading
/// into the party included) all stay inside its scope. The START URL carries
/// the link the app was added from, under the canonical path
/// (<c>&lt;scope&gt;party/&lt;token&gt;</c> or
/// <c>&lt;scope&gt;party/invite/&lt;token&gt;</c>), because relaunching needs
/// it: an invitation keeps no session of its own, and a party's page is opened
/// by its own link.</para>
///
/// <para>The app opens the link and nothing else: no permission rides on it
/// that the link does not carry, and nothing about it is stored. The manifest,
/// the icon and the identity rules are <see cref="HomeScreenApp"/>'s, shared
/// with an album shared by link.</para>
/// </summary>
public static class PartyHomeScreenApp
{
    /// <summary>
    /// The party's app: its stable, non-authorising identity — a one-way
    /// digest of the party's id — which is also the root of its scope.
    /// </summary>
    public static string AppPath(Guid partyId) => HomeScreenApp.AppPath("party", "nubarca-party-app", partyId);

    /// <summary>The app's scope: its own path, and nothing of any other party's.</summary>
    public static string Scope(Guid partyId) => AppPath(partyId) + "/";

    /// <summary>
    /// The link the app was added from, under its canonical path:
    /// <paramref name="linkPath"/> is the link's own path (<c>/party/&lt;token&gt;</c>).
    /// </summary>
    public static string StartUrl(Guid partyId, string linkPath) => AppPath(partyId) + linkPath;

    /// <summary>
    /// The manifest of the party's app, opening <paramref name="linkPath"/>,
    /// named after the party, with icons on <paramref name="iconBase"/>. The
    /// party's version is the icons' cache key: choosing a new cover spends one.
    /// </summary>
    public static string Manifest(Guid partyId, string linkPath, string title, string iconBase, int version) =>
        HomeScreenApp.Manifest(
            AppPath(partyId), StartUrl(partyId, linkPath), title, iconBase,
            version.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

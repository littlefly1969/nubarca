using NubArca.Api.HomeScreen;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// An album shared by link, kept on a visitor's home screen: one album, one
/// app, its own scope — the rules of <see cref="HomeScreenApp"/>.
///
/// <list type="bullet">
/// <item><b>id</b> <c>/album/app/&lt;key&gt;</c>, the key a one-way digest of
/// the album's id. Never the token: a token is a capability that is rotated
/// and revoked, and an app's identity must outlive both and authorise
/// nothing. The same album behind a rotated link is the same app.</item>
/// <item><b>scope</b> <c>/album/app/&lt;key&gt;/</c> — never all of
/// <c>/album/</c>, so one installed album never claims another's links, and
/// never under <c>/party/</c>, so an album and a party never claim each
/// other's.</item>
/// <item><b>start_url</b> <c>/album/app/&lt;key&gt;/open/&lt;token&gt;</c>:
/// the link itself under the app's path, because relaunching needs the
/// capability. Nothing else is granted to the app: a revoked link closes it, a
/// rotated one leaves it on an address that opens nothing, and a protected
/// album still asks for the second factor the link asks for.</item>
/// </list>
/// </summary>
public static class AlbumShareHomeScreenApp
{
    public static string AppPath(Guid albumId) => HomeScreenApp.AppPath("album", "nubarca-album-app", albumId);

    public static string Scope(Guid albumId) => AppPath(albumId) + "/";

    public static string StartUrl(Guid albumId, string token) =>
        $"{AppPath(albumId)}/open/{Uri.EscapeDataString(token)}";

    /// <summary>
    /// The album's app, named after it, with its icon drawn from
    /// <paramref name="coverFileItemId"/> — the picture the shared page opens
    /// on — so choosing another cover spends a new icon address.
    /// </summary>
    public static string Manifest(Guid albumId, string token, string albumName, Guid? coverFileItemId) =>
        HomeScreenApp.Manifest(
            AppPath(albumId),
            StartUrl(albumId, token),
            albumName,
            $"/api/album-share/{Uri.EscapeDataString(token)}/app-icon",
            coverFileItemId is Guid cover ? cover.ToString("N")[..12] : "brand");
}

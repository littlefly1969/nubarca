using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
/// that the link does not carry, and nothing about it is stored. There is no
/// service worker and no cached page to show yesterday's party.</para>
/// </summary>
public static class PartyHomeScreenApp
{
    public const string ManifestContentType = "application/manifest+json";

    // The party pages' own fixed dark surface, and the product's.
    private const string Background = "#0a0f1a";

    public static bool IsIconSize(int size) => size is 192 or 512;

    /// <summary>
    /// The party's app: its stable, non-authorising identity — a one-way
    /// digest of the party's id — which is also the root of its scope.
    /// </summary>
    public static string AppPath(Guid partyId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"nubarca-party-app:{partyId:N}"));
        return "/party/app/" + Convert.ToHexStringLower(digest)[..32];
    }

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
    public static string Manifest(Guid partyId, string linkPath, string title, string iconBase, int version)
    {
        JsonObject Icon(int size, bool maskable) => new()
        {
            ["src"] = $"{iconBase}/{size}?v={version}{(maskable ? "&maskable=true" : "")}",
            ["sizes"] = $"{size}x{size}",
            ["type"] = "image/png",
            ["purpose"] = maskable ? "maskable" : "any",
        };
        return new JsonObject
        {
            ["id"] = AppPath(partyId),
            ["name"] = title,
            ["short_name"] = title,
            ["start_url"] = StartUrl(partyId, linkPath),
            ["scope"] = Scope(partyId),
            ["display"] = "standalone",
            ["background_color"] = Background,
            ["theme_color"] = Background,
            ["icons"] = new JsonArray(Icon(192, false), Icon(512, false), Icon(512, true)),
        }.ToJsonString();
    }

    /// <summary>The product's own icon, for a party with no picture of its own to show.</summary>
    public static string BrandIcon(int size, bool maskable) =>
        maskable ? "/brand/nubarca-pwa-maskable-512.png" : $"/brand/nubarca-pwa-{size}.png";

    /// <summary>
    /// The centre square of <paramref name="picture"/> — an already
    /// metadata-stripped derivative — as a PNG of <paramref name="size"/>
    /// pixels, carrying no metadata of its own. Full-bleed, so it is a maskable
    /// icon as it is. Null when the picture cannot be read.
    /// </summary>
    public static async Task<byte[]?> RenderIconAsync(Stream picture, int size, CancellationToken cancellationToken)
    {
        try
        {
            using var image = await Image.LoadAsync<Rgb24>(picture, cancellationToken);
            var side = Math.Min(image.Width, image.Height);
            image.Mutate(x => x
                .Crop(new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side))
                .Resize(size, size));
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;
            using var output = new MemoryStream();
            await image.SaveAsPngAsync(output, cancellationToken);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }
}

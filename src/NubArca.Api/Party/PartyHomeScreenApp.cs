using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Party;

/// <summary>
/// A party on a guest's home screen: the web app manifest of one party link and
/// the icon drawn from the party's cover.
///
/// <para>The app opens the link it was added from — the party's public page or
/// a group's personal invitation — and nothing else: no permission rides on it
/// that the link itself does not carry, and nothing about it is stored. Every
/// launch asks the server for the party as it is now; there is no service
/// worker and no cached page to show yesterday's party.</para>
///
/// <para>Its scope is every party page, so an invitation leads into its party
/// inside the same app window — and, on an iPhone, in the same storage, where
/// the guest's anonymous party cookie lives.</para>
///
/// <para>Its IDENTITY is the party's, never a link's: a link's token is a
/// capability, and an app id must authorise nothing, survive a link rotated or
/// revoked, and be the same for every invitation to one party. Two parties on
/// one phone are two apps; a party's public page and its invitations are two
/// apps of that party (one opens the party, the other the group's
/// invitation). The START URL keeps the token, because relaunching needs it:
/// an invitation keeps no session of its own, and a party's page is opened by
/// its own link.</para>
/// </summary>
public static class PartyHomeScreenApp
{
    public const string ManifestContentType = "application/manifest+json";

    private const string Scope = "/party/";

    // The party pages' own fixed dark surface, and the product's.
    private const string Background = "#0a0f1a";

    public static bool IsIconSize(int size) => size is 192 or 512;

    /// <summary>
    /// The app's stable, non-authorising identity: a one-way digest of the
    /// party's id — the same for every link and every invitation to it.
    /// </summary>
    public static string AppId(Guid partyId, bool invitation)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"nubarca-party-app:{partyId:N}"));
        var key = Convert.ToHexStringLower(digest)[..32];
        return invitation ? $"/party/app/{key}/invitation" : $"/party/app/{key}";
    }

    /// <summary>
    /// The manifest of the app <paramref name="appId"/> that opens
    /// <paramref name="startPath"/>, named after the party, with icons on
    /// <paramref name="iconBase"/>. The party's version is the icons' cache
    /// key: choosing a new cover spends one.
    /// </summary>
    public static string Manifest(string appId, string startPath, string title, string iconBase, int version)
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
            ["id"] = appId,
            ["name"] = title,
            ["short_name"] = title,
            ["start_url"] = startPath,
            ["scope"] = Scope,
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

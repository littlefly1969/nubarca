using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.HomeScreen;

/// <summary>
/// What every public page that can be kept on a home screen shares — a party,
/// an album shared by link: the app's identity and scope, its manifest and its
/// icon. Which link opens what, and who may, stays with each surface.
///
/// <para>ONE THING, ONE APP, ITS OWN SCOPE. An app lives under its own
/// canonical path, <c>/&lt;area&gt;/app/&lt;key&gt;/</c>, the key a one-way
/// digest of the thing's id under a purpose string: that path is the app's id,
/// and with a trailing slash its scope. Two apps' scopes never overlap, so
/// installing one never claims another's links — Android hands a link to the
/// installed app whose scope contains it and judges "already installed" by it,
/// which is why a scope of a whole area (<c>/party/</c>) made every party after
/// the first look installed.</para>
///
/// <para>The id authorises nothing: no link's token is in it, it survives a
/// link rotated or revoked, and the digest does not give the thing's id back.
/// The START URL is the link under the canonical path, because relaunching
/// needs it — and so a revoked or rotated link closes the app too.</para>
///
/// <para>There is no service worker and no cached page: every launch asks the
/// server for the thing as it is now, through the link.</para>
/// </summary>
public static class HomeScreenApp
{
    public const string ManifestContentType = "application/manifest+json";

    // The public pages' own fixed dark surface, and the product's.
    private const string Background = "#0a0f1a";

    public static bool IsIconSize(int size) => size is 192 or 512;

    /// <summary>
    /// The stable, non-authorising root of a thing's app:
    /// <c>/&lt;area&gt;/app/&lt;32 hex of SHA-256("&lt;purpose&gt;:&lt;id&gt;")&gt;</c>.
    /// </summary>
    public static string AppPath(string area, string purpose, Guid id)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{id:N}"));
        return $"/{area}/app/" + Convert.ToHexStringLower(digest)[..32];
    }

    /// <summary>
    /// The manifest of the app at <paramref name="appPath"/> (its id; its scope
    /// with a trailing slash), opening <paramref name="startUrl"/>, named
    /// <paramref name="name"/>, with icons on <paramref name="iconBase"/>.
    /// <paramref name="iconVersion"/> is the icons' cache key: a new picture
    /// must spend a new one, so a browser fetches it again.
    /// </summary>
    public static string Manifest(string appPath, string startUrl, string name, string iconBase, string iconVersion)
    {
        JsonObject Icon(int size, bool maskable) => new()
        {
            ["src"] = $"{iconBase}/{size}?v={iconVersion}{(maskable ? "&maskable=true" : "")}",
            ["sizes"] = $"{size}x{size}",
            ["type"] = "image/png",
            ["purpose"] = maskable ? "maskable" : "any",
        };
        return new JsonObject
        {
            ["id"] = appPath,
            ["name"] = name,
            ["short_name"] = name,
            ["start_url"] = startUrl,
            ["scope"] = appPath + "/",
            ["display"] = "standalone",
            ["background_color"] = Background,
            ["theme_color"] = Background,
            ["icons"] = new JsonArray(Icon(192, false), Icon(512, false), Icon(512, true)),
        }.ToJsonString();
    }

    /// <summary>The product's own icon, for an app with no picture of its own to show.</summary>
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

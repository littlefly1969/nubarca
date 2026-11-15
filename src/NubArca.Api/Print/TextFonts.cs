using SixLabors.Fonts;

namespace NubArca.Api.Print;

/// <summary>
/// The few typefaces an owner may choose for words of their own: the line in a
/// framed print's band and the text of a lithophane. One table, mirrored by the
/// browser (<c>frontend/src/text/textFonts.ts</c>), and the browser draws its
/// preview with the very same files (served from <c>/fonts/text/</c>), so the
/// preview's letters are the paper's and the STL's.
///
/// <see cref="Default"/> is the face these words were always drawn with, so a
/// request that names no font comes out exactly as before.
/// </summary>
public static class TextFonts
{
    public const string Default = "nubarca";

    /// <summary>A choosable face: its wire id and the file it is drawn from, under the output's Assets/fonts.</summary>
    public sealed record Entry(string Id, string File);

    public static readonly IReadOnlyList<Entry> All =
    [
        new(Default, "SpaceGrotesk-Bold.ttf"),
        new("serif", Path.Combine("text", "DMSerifDisplay-Regular.ttf")),
        new("hand", Path.Combine("text", "Kalam-Bold.ttf")),
        new("marker", Path.Combine("text", "CaveatBrush-Regular.ttf")),
        new("script", Path.Combine("text", "DancingScript.ttf")),
    ];

    /// <summary>True when <paramref name="id"/> is absent (the default) or a face of the table.</summary>
    public static bool IsKnown(string? id) => id is null || All.Any(x => x.Id == id);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, FontFamily> Loaded = [];

    /// <summary>The family for <paramref name="id"/> (null: the default), loaded once per process.</summary>
    public static FontFamily Family(string? id, string? assetRoot = null)
    {
        var entry = All.FirstOrDefault(x => x.Id == (id ?? Default))
            ?? throw new ArgumentException("Unknown text font.", nameof(id));
        var path = Path.Combine(assetRoot ?? AppContext.BaseDirectory, "Assets", "fonts", entry.File);
        lock (Gate)
        {
            if (!Loaded.TryGetValue(path, out var family))
            {
                family = new FontCollection().Add(path);
                Loaded[path] = family;
            }
            return family;
        }
    }
}

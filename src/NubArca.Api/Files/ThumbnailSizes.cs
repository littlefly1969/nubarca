namespace NubArca.Api.Files;

// Centralises the supported thumbnail sizes. Add new entries here and the
// service + endpoint pick them up automatically.
public static class ThumbnailSizes
{
    public const int DefaultSmallMaxEdge = 768;
    public const string Small = "small";

    // Slice 59: medium preview for the gallery lightbox so the lightbox does
    // not download the full-res original. The default is 1920 px (Full HD
    // landscape for 16:9 sources), and operators can override it via
    // MediaDerivatives:MediumPreviewMaxEdge.
    public const string Medium = "medium";

    // Video poster. Its exact canvas is centralised in VideoPosterSpec and is
    // shared by real FFmpeg extraction, the fallback provider and persistence.
    // Stored using the existing FileThumbnail row model so cleanup, owner
    // scoping, and dedup behave identically to image thumbnails.
    public const string Poster = "poster";

    // Six 480x270 cells in one horizontal JPEG sprite. The web gallery animates
    // the cells on hover and the TV browser on focus without fetching the full
    // video. Stored as one derivative to keep request/row/refcount overhead low.
    public const string VideoPreviewStrip = "video-preview-strip";

    // Bounding-box edge length in pixels. Aspect ratio is preserved when the
    // source is rectangular; if both dimensions already fit, no upscale occurs.
    // For Poster the edge is informational only — providers produce the exact
    // VideoPosterSpec canvas.
    private static readonly IReadOnlyDictionary<string, int> Edges =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Small] = DefaultSmallMaxEdge,
            [Medium] = 1920,
            [Poster] = VideoPosterSpec.DefaultWidth,
            [VideoPreviewStrip] =
                VideoPreviewStripSpec.DefaultFrameWidth
                * VideoPreviewStripSpec.DefaultFrameCount,
        };

    public static bool IsKnown(string? size) =>
        !string.IsNullOrWhiteSpace(size) && Edges.ContainsKey(size);

    public static int GetEdge(string size) => Edges[size];

    public static string Normalize(string size) => Edges.Keys.First(k =>
        string.Equals(k, size, StringComparison.OrdinalIgnoreCase));
}

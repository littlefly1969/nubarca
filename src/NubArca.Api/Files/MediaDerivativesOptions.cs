namespace NubArca.Api.Files;

// Slice 100: backend selection + tuning for image derivative generation.
// Bound to the "MediaDerivatives" configuration section. Resource limits
// (dimension/pixel/byte caps, the EnableThumbnails kill-switch) stay in
// ImageProcessingOptions — this is purely about WHICH backend renders and how.
//
// Defaults are conservative and preserve the historical behaviour
// (quality 80). `auto` prefers the fast libvips backend when its native
// library is present and falls back to ImageSharp otherwise.
public sealed class MediaDerivativesOptions
{
    public const string SectionName = "MediaDerivatives";
    public const int MinDimension = 64;
    public const int MaxDimension = 8192;
    public const int MinMediumPreviewMaxEdge = 256;
    public const int MaxMediumPreviewMaxEdge = 8192;

    // Preferred backend: "imagesharp" | "vips" | "auto" (default). "auto" uses
    // vips when available (and VipsEnabled), else ImageSharp. "vips" forces the
    // preference to vips but still falls back per FallbackToImageSharp. Unknown
    // values are treated as "auto".
    public string ImageBackend { get; set; } = ImageDerivativeBackendNames.Auto;

    // When the preferred backend is unavailable, throws, times out, cannot
    // decode, or produces output that violates the no-upscale/bounding-box
    // contract, retry the render with the ImageSharp backend. Strongly
    // recommended; turning it off means a vips failure surfaces as a derivative
    // failure diagnostic with no second attempt.
    public bool FallbackToImageSharp { get; set; } = true;

    // Master switch for the vips backend. When false, vips is never used even
    // if ImageBackend=vips (the pipeline behaves exactly as before).
    public bool VipsEnabled { get; set; } = true;

    // libvips worker-thread count for a single thumbnail operation. 0 leaves the
    // libvips default (number of CPU cores). Lower it on small/shared hosts to
    // bound CPU spikes; raise it on dedicated boxes. Applied once at startup.
    public int VipsConcurrency { get; set; }

    // JPEG quality for each size. Defaults preserve the historical value (80)
    // so output bytes — and therefore content-addressed derived blobs — are
    // unchanged for the ImageSharp backend. Changing a quality changes the
    // bytes (a new dedup bucket), not correctness.
    public int SmallQuality { get; set; } = 80;
    public int MediumQuality { get; set; } = 80;

    // Unified gallery geometry. These defaults are the production contract,
    // while operators may change them from .env without rebuilding the image.
    // Every renderer/provider/persistence validator reads these same values.
    public int SmallMaxEdge { get; set; } = ThumbnailSizes.DefaultSmallMaxEdge;
    public int PosterWidth { get; set; } = VideoPosterSpec.DefaultWidth;
    public int PosterHeight { get; set; } = VideoPosterSpec.DefaultHeight;
    public int VideoPreviewFrameWidth { get; set; } = VideoPreviewStripSpec.DefaultFrameWidth;
    public int VideoPreviewFrameHeight { get; set; } = VideoPreviewStripSpec.DefaultFrameHeight;

    // Bounding-box max edge for medium image previews. Aspect ratio is
    // preserved by the renderer and sources are not upscaled.
    public int MediumPreviewMaxEdge { get; set; } = 1920;

    // Hard ceiling on a single backend render call. Guards against a
    // pathological input pinning a worker thread. On timeout the render is
    // abandoned (and falls back to ImageSharp when enabled). 0 disables the
    // timeout.
    public int RenderTimeoutSeconds { get; set; } = 30;

    public int QualityFor(string size) => string.Equals(size, ThumbnailSizes.Medium, StringComparison.Ordinal)
        ? Clamp(MediumQuality)
        : Clamp(SmallQuality);

    public int EdgeFor(string size) => size switch
    {
        ThumbnailSizes.Small => ClampDimension(SmallMaxEdge),
        ThumbnailSizes.Medium => Math.Clamp(
            MediumPreviewMaxEdge, MinMediumPreviewMaxEdge, MaxMediumPreviewMaxEdge),
        ThumbnailSizes.Poster => PosterSize.Width,
        ThumbnailSizes.VideoPreviewStrip => VideoPreviewStripSize.Width,
        _ => ThumbnailSizes.GetEdge(size),
    };

    public (int Width, int Height) PosterSize => (
        ClampDimension(PosterWidth),
        ClampDimension(PosterHeight));

    public (int FrameWidth, int FrameHeight, int FrameCount, int Width, int Height)
        VideoPreviewStripSize
    {
        get
        {
            var frameWidth = ClampDimension(VideoPreviewFrameWidth);
            var frameHeight = ClampDimension(VideoPreviewFrameHeight);
            const int frameCount = VideoPreviewStripSpec.DefaultFrameCount;
            return (frameWidth, frameHeight, frameCount, frameWidth * frameCount, frameHeight);
        }
    }

    private static int Clamp(int quality) => Math.Clamp(quality, 1, 100);
    private static int ClampDimension(int value) => Math.Clamp(value, MinDimension, MaxDimension);
}

// Stable backend-selection vocabulary for the ImageBackend config knob. The
// concrete backend NAMES recorded in diagnostics live in DerivativeBackends.
public static class ImageDerivativeBackendNames
{
    public const string ImageSharp = "imagesharp";
    public const string Vips = "vips";
    public const string Auto = "auto";
}

namespace NubArca.Api.Files;

/// <summary>
/// Generates a JPEG poster frame for a video file. Implementations may draw
/// synthetic art (no native deps) or extract a real frame via an external
/// process (FFmpeg). Both must return a <see cref="VideoPosterResult"/> (JPEG
/// bytes + the SOURCE that actually produced them) or null to signal "I
/// cannot produce a poster" so the caller can fall back.
///
/// The <paramref name="openBlobContent"/> factory is provided for
/// implementations that need the raw video bytes (e.g. FFmpeg). Implementations
/// that do not need video content (e.g. Synthetic) must NOT call it, so that
/// the blob stream is never opened unnecessarily for large video files.
/// </summary>
public interface IVideoPosterProvider
{
    Task<VideoPosterResult?> TryGetPosterAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken);

    // Generates one horizontal sprite containing six representative frames.
    // Implementations without real video decoding return null. The optional
    // duration avoids a second probe when ffprobe metadata is already present.
    Task<VideoPreviewStripResult?> TryGetPreviewStripAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        double? durationSeconds,
        CancellationToken cancellationToken);
}

// Slice 95: poster bytes + provenance. The source is persisted on the
// FileThumbnail row so synthetic placeholders are distinguishable from real
// frames (and can be selectively regenerated when a real provider is enabled
// later). The FFmpeg provider falls back internally to the synthetic one, so
// provenance must travel with the RESULT, not the provider type.
public sealed record VideoPosterResult(MemoryStream Content, string Source);

public sealed record VideoPreviewStripResult(
    MemoryStream Content,
    int Width,
    int Height,
    int FrameCount);

public static class VideoPosterSpec
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;
}

public static class VideoPreviewStripSpec
{
    public const int DefaultFrameCount = 6;
    public const int DefaultFrameWidth = 480;
    public const int DefaultFrameHeight = 270;
}

// Stable poster-source vocabulary (slice 95). "embedded" (a cover art frame
// from the container) and "browser" (client-captured) are reserved for future
// providers; "unknown" marks rows created before provenance was recorded.
public static class VideoPosterSources
{
    public const string Synthetic = "synthetic";
    public const string Ffmpeg = "ffmpeg";
    public const string Embedded = "embedded";
    public const string Browser = "browser";
    public const string Unknown = "unknown";
}

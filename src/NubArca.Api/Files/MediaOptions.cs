namespace NubArca.Api.Files;

public class MediaOptions
{
    // Which poster provider to use. "synthetic" (default) draws a dark-backdrop
    // play-triangle image without any external dependency. "ffmpeg" invokes an
    // external ffmpeg process to extract a real video frame, falling back to
    // synthetic on any failure (missing binary, non-zero exit, timeout, etc.).
    public string VideoPosterProvider { get; set; } = "synthetic";

    // Path to the ffmpeg executable. Used only when VideoPosterProvider=ffmpeg.
    // Defaults to "ffmpeg" (looked up from PATH).
    public string FfmpegPath { get; set; } = "ffmpeg";

    // Path to the ffprobe executable. Used when VideoMetadataProvider=ffprobe.
    // Defaults to "ffprobe" (looked up from PATH).
    public string FfprobePath { get; set; } = "ffprobe";

    // Which video-metadata provider to use. "none" (default) disables video
    // probing entirely (the backfill/CLI/post-ingest do no work). "ffprobe"
    // invokes an external ffprobe process to read container/stream metadata.
    public string VideoMetadataProvider { get; set; } = "none";

    // Maximum seconds to wait for ffprobe. On timeout the probe is abandoned and
    // recorded as a transient failure (retryable by a later backfill).
    public int VideoProbeTimeoutSeconds { get; set; } = 10;

    // Maximum bytes accepted from ffprobe stdout (the JSON report). Reports
    // larger than this are treated as a failure.
    public int VideoProbeMaxOutputBytes { get; set; } = 4 * 1024 * 1024; // 4 MB

    // True when a real (external) video-metadata provider is configured.
    public bool VideoMetadataProbeEnabled
        => string.Equals(VideoMetadataProvider, "ffprobe", StringComparison.OrdinalIgnoreCase);

    // Maximum seconds to wait for ffmpeg to produce a poster frame.
    // If ffmpeg does not finish within this window it is killed and the
    // synthetic fallback is used.
    public int VideoPosterTimeoutSeconds { get; set; } = 10;

    // Maximum seconds to wait for the six-frame preview strip. Strip extraction
    // opens six independently-seeked inputs, so its cost is bounded by six
    // nearby keyframe decodes rather than by the full video duration.
    public int VideoPreviewStripTimeoutSeconds { get; set; } = 45;

    // Seek offset (seconds) passed to ffmpeg with -ss before the input.
    // A non-zero offset avoids black frames at the start of many videos.
    public int VideoPosterSeekSeconds { get; set; } = 3;

    // Maximum bytes accepted from ffmpeg stdout. Frames larger than this
    // are treated as invalid and the synthetic fallback is used.
    public int VideoPosterMaxOutputBytes { get; set; } = 20 * 1024 * 1024; // 20 MB

    // ---- HLS playback derivatives (video-hls slice 1) ----------------------
    // Which HLS transcoder to use. "none" (default) disables HLS generation
    // entirely (the job/CLI do no work); "ffmpeg" invokes an external ffmpeg
    // process to produce the two-rendition fMP4 HLS ladder. Enabling this in
    // practice also requires Media:VideoMetadataProvider=ffprobe — the copy-vs-
    // encode decision and the audio-track mapping need probe data (a source
    // whose probe fields are absent is probed on the fly via the configured
    // IVideoMetadataExtractor; the "none" no-op extractor cannot provide them).
    public string VideoHlsProvider { get; set; } = "none";

    // True when a real (external) HLS transcoder is configured.
    public bool VideoHlsEnabled
        => string.Equals(VideoHlsProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase);

    // Maximum seconds to wait for one full HLS transcode. Unlike the poster
    // frame grab this is a whole-file job (minutes for long videos on a weak
    // host), so the ceiling is generous. On timeout the run is abandoned and
    // recorded as a failed derivative (retryable with --force).
    public int VideoHlsTimeoutSeconds { get; set; } = 30 * 60;

    // Target HLS segment duration in seconds. Encoded renditions force a
    // keyframe on this cadence so segment boundaries stay aligned.
    public int VideoHlsSegmentSeconds { get; set; } = 4;

    // The "high" rendition is capped at this height. A source that is already
    // H.264 (+ AAC / no audio) at or below the cap is stream-copied into
    // segments instead of re-encoded (near-zero CPU).
    public int VideoHlsHighMaxHeight { get; set; } = 1080;

    // Height of the always-encoded "low" rendition (the poor-remote-link rung).
    // Sources at or below this height get a single-rendition ladder.
    public int VideoHlsLowHeight { get; set; } = 480;

    // Capped-VBR ceilings (kbit/s) for the encoded renditions.
    public int VideoHlsHighMaxRateKbps { get; set; } = 5000;
    public int VideoHlsLowMaxRateKbps { get; set; } = 1000;

    // x264 CRF for the encoded renditions (lower = better quality & bigger
    // files; the maxrate caps above still bound the peaks). In practice CRF is
    // the GOVERNING quality constraint — for most content the encoder lands
    // well under the rate ceiling, so raising the ceiling alone changes
    // nothing; lower the CRF to actually raise quality.
    public int VideoHlsHighCrf { get; set; } = 23;
    public int VideoHlsLowCrf { get; set; } = 26;

    // AAC bitrate (kbit/s) for encoded audio.
    public int VideoHlsAudioBitrateKbps { get; set; } = 128;
}

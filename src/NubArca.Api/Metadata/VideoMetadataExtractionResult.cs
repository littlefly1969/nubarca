namespace NubArca.Api.Metadata;

// Normalized, typed output of video metadata probing (ffprobe). Produced by
// IVideoMetadataExtractor and mapped onto a BlobMetadata row by the video
// backfill path.
//
// Unlike the image EXIF result there is no raw-document field: the raw ffprobe
// JSON can echo the (GUID) temp input path and arbitrary container tags, so it
// is deliberately NOT persisted — only these curated typed fields are.
public sealed record VideoMetadataExtractionResult
{
    // One of MetadataStatuses.{Completed,Skipped,Failed}. Skipped doubles as
    // "unsupported / not a probe-able video".
    public required string Status { get; init; }

    // One of MetadataErrorCodes.* or null.
    public string? ErrorCode { get; init; }

    // Bumped when probe/mapping logic changes so a future backfill can find
    // rows produced by an older extractor.
    public int Version { get; init; }

    // Pixel dimensions of the (first) video stream.
    public int? Width { get; init; }
    public int? Height { get; init; }

    // Container/stream duration in seconds (fractional).
    public double? DurationSeconds { get; init; }

    // Codec short names (e.g. "h264", "hevc", "aac").
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }

    // Average frame rate (fps).
    public double? FrameRate { get; init; }

    // Video (or container) bit rate in bits per second.
    public long? VideoBitrate { get; init; }

    // Audio track shape.
    public bool HasAudio { get; init; }
    public int? AudioChannels { get; init; }
    public int? AudioSampleRate { get; init; }

    // Display rotation in degrees, normalized to [0,360).
    public int? Rotation { get; init; }

    // When the video was shot, in the convention photographs use: the camera's
    // WALL-CLOCK time kept as a UTC-kind value, with its offset beside it when
    // the file says it. From Apple's com.apple.quicktime.creationdate (local
    // time with its offset) when present; otherwise the container's
    // creation_time, which is true UTC and carries no offset.
    public DateTime? CreationTime { get; init; }

    // "+02:00" — only when the file states the offset (Apple's creationdate).
    public string? CreationTimeOffset { get; init; }

    // Which of the two CreationTime came from: QuickTimeCreationDateSource or
    // ContainerCreationTimeSource. Stored as BlobMetadata.DateTakenSource.
    public string? CreationTimeSource { get; init; }

    // Where it was shot, from the ISO 6709 location the phone wrote (Apple's
    // com.apple.quicktime.location.ISO6709, Android's "location"). Owner-
    // private like a photograph's GPS: it never leaves the owner's own views.
    public double? GpsLatitude { get; init; }
    public double? GpsLongitude { get; init; }
    public double? GpsAltitude { get; init; }

    // The device, as the phone named it.
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? Software { get; init; }

    public const string QuickTimeCreationDateSource = "quicktime_creationdate";
    public const string ContainerCreationTimeSource = "video_creation_time";

    public static VideoMetadataExtractionResult ForStatus(string status, string? errorCode, int version)
        => new() { Status = status, ErrorCode = errorCode, Version = version };
}

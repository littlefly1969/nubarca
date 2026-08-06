namespace NubArca.Api.Domain;

// Immutable, blob-derived metadata. Because it is computed purely from the
// file bytes, it is the SAME for every FileItem (and every user) that
// references the same content-addressed blob — so it lives next to the blob,
// not the FileItem. Exactly one row per BlobObject (unique FK).
//
// "Immutable" here means "from the user's perspective": no user operation
// (rename, move, edit) ever changes these values. They are written once when a
// blob is first ingested. A future "strong edit" that changes the downloadable
// bytes produces a NEW blob with a NEW sha256 and its own BlobMetadata row;
// existing blobs are never mutated in place.
public class BlobMetadata
{
    public Guid Id { get; set; }

    public Guid BlobObjectId { get; set; }

    // Byte size of the underlying blob (mirrors BlobObject.SizeBytes; kept here
    // so the metadata row is self-describing for the extraction pipeline).
    public long SizeBytes { get; set; }

    // Content type detected from the bytes (e.g. "image/jpeg"). Null when we
    // could not sniff it — the FileItem still carries the client-supplied MIME.
    public string? DetectedContentType { get; set; }

    // Coarse media bucket derived from the bytes / MIME: image, video, audio,
    // document, other. Never "unknown" once written — that string is reserved
    // for the effective-metadata fallback on pre-metadata-model files.
    public string MediaCategory { get; set; } = MediaCategories.Other;

    // Detected container/codec format name when known (e.g. "JPEG", "PNG").
    public string? DetectedFormat { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public long? PixelCount { get; set; }

    // Whether a thumbnail has been produced for this blob's bytes.
    public string ThumbnailStatus { get; set; } = MetadataStatuses.Pending;

    // State of the EMBEDDED-metadata extraction pipeline (EXIF/IPTC/XMP). In
    // this slice it is always "pending" — deep extraction is Slice 54's job.
    public string ExtractionStatus { get; set; } = MetadataStatuses.Pending;

    // Machine-readable error code from a failed extraction attempt; null on
    // success / pending.
    public string? ExtractionErrorCode { get; set; }

    public DateTime? ExtractedAt { get; set; }

    // Version of the extractor that produced the embedded fields below, so a
    // future backfill (slice 55) can re-run only rows from an older extractor.
    public int? ExtractionVersion { get; set; }

    // Internal raw structured embedded-metadata document (JSON object keyed by
    // directory → tag → description). INTERNAL ONLY — never serialized to a
    // normal DTO; only curated typed fields below leave the metadata service.
    public string? RawMetadataJson { get; set; }

    // ---- Curated, typed embedded image metadata (slice 54) -----------------
    // All blob-derived (computed from immutable bytes), all nullable. The
    // exhaustive long tail lives in RawMetadataJson; these are the common
    // fields worth querying. Sensitive fields (GPS coordinates, serial
    // numbers) are stored here but MUST NOT be exposed by normal DTOs — that
    // gate lives in the metadata service until the privacy slice (57).

    // Date/time.
    public DateTime? DateTaken { get; set; }
    public string? DateTakenSource { get; set; }
    public string? DateTakenOffset { get; set; }

    // Orientation (EXIF 1..8).
    public int? Orientation { get; set; }

    // Camera / device.
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? LensMake { get; set; }
    public string? LensModel { get; set; }
    public string? Software { get; set; }
    public string? BodySerialNumber { get; set; }   // SENSITIVE
    public string? LensSerialNumber { get; set; }   // SENSITIVE

    // Capture settings.
    public int? IsoSpeed { get; set; }
    public double? FNumber { get; set; }
    public string? ExposureTime { get; set; }
    public double? FocalLength { get; set; }
    public int? FocalLength35mm { get; set; }
    public string? ExposureBias { get; set; }
    public string? ExposureProgram { get; set; }
    public string? MeteringMode { get; set; }
    public string? Flash { get; set; }
    public string? WhiteBalance { get; set; }

    // Color / image.
    public string? ColorSpace { get; set; }
    public bool HasIccProfile { get; set; }
    public string? IccProfileName { get; set; }

    // GPS (SENSITIVE: stored for exhaustive extraction, never in a normal DTO).
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }

    // ---- Video probe fields (ffprobe) --------------------------------------
    // Blob-derived, computed once from the immutable bytes. All nullable. The
    // pixel dimensions of a video reuse Width/Height/PixelCount above (they are
    // NULL for videos until the probe runs). These fields are owner-curated
    // (none are privacy-sensitive on their own) — DateTaken carries the
    // container creation time when present (same field the image path uses).

    // Container/stream duration in seconds (fractional).
    public double? DurationSeconds { get; set; }

    // Codec short names as reported by ffprobe (e.g. "h264", "hevc", "aac").
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }

    // Average frame rate (frames per second), from avg_frame_rate.
    public double? FrameRate { get; set; }

    // Bit rate of the video stream (or container) in bits per second.
    public long? VideoBitrate { get; set; }

    // Audio track shape.
    public bool HasAudio { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }

    // Display rotation in degrees (0/90/180/270), from the stream side-data /
    // rotate tag. Normalized to [0,360).
    public int? Rotation { get; set; }

    // ---- Video-probe extraction lifecycle (independent from the image EXIF
    // pipeline above, which keys off ExtractionStatus/ExtractionVersion) ------
    public string VideoExtractionStatus { get; set; } = MetadataStatuses.Pending;
    public string? VideoExtractionErrorCode { get; set; }
    public int? VideoExtractionVersion { get; set; }
    public DateTime? VideoExtractedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

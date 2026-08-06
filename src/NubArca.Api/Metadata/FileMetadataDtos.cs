namespace NubArca.Api.Metadata;

// Effective, owner-scoped display metadata for one file. Combines the shared,
// read-only blob-derived facts with this FileItem's private user metadata.
//
// Deliberately carries NO storage internals: no StorageKey, no sha256, no
// BlobObjectId, no OwnerUserId, no physical path, and no raw embedded-metadata
// document. Only curated fields cross this boundary.
public sealed record FileMetadataResponse(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    BlobDerivedMetadata Blob,
    UserMetadataView User,
    EffectiveMetadata Effective);

// Resolved view of metadata that the SPA renders directly (slice 56). Combines
// the layered sources with explicit precedence rules. Carries NO sensitive
// embedded fields: GPS coordinates and serial numbers are NEVER promoted from
// embedded metadata into Effective — only the user's own LocationOverride is
// surfaced, and a HasGps boolean (from the existing Blob block) signals that
// embedded coordinates exist internally without revealing them.
public sealed record EffectiveMetadata(
    // Display name: user-provided Title if set, else the file's logical Name.
    string DisplayName,
    // Always non-null: user DateTakenOverride > embedded DateTaken > CreatedAt.
    DateTime DateTaken,
    // One of EffectiveDateTakenSources.{User, Embedded, Uploaded}.
    string DateTakenSource,
    // User-provided location text only; embedded GPS coordinates are never
    // included here (only HasGps on the Blob block signals their presence).
    string? Location);

public static class EffectiveDateTakenSources
{
    public const string User = "user";
    public const string Embedded = "embedded";
    public const string Uploaded = "uploaded";

    // Single source of truth for the layered effective-capture-date precedence:
    //   user DateTakenOverride → embedded blob DateTaken → upload time.
    // Used both for the metadata response and to populate the denormalized
    // FileItem.EffectiveDateTaken column so the two never drift.
    public static (DateTime Value, string Source) Compute(
        DateTime? userOverride, DateTime? embeddedDate, DateTime createdAt)
    {
        if (userOverride is DateTime u)
        {
            return (u, User);
        }
        if (embeddedDate is DateTime e)
        {
            return (e, Embedded);
        }
        return (createdAt, Uploaded);
    }
}

// Shared across every reference to the same deduplicated blob; read-only from
// any user operation.
public sealed record BlobDerivedMetadata(
    string MediaCategory,
    string? DetectedContentType,
    string? DetectedFormat,
    int? Width,
    int? Height,
    long? PixelCount,
    string ThumbnailStatus,
    string ExtractionStatus,
    EmbeddedImageMetadata? Embedded,
    // Present only for a video blob once probing completed. Owner-curated: none
    // of these are privacy-sensitive on their own (unlike GPS/serials), so all
    // are exposed. The container creation time is NOT here — it flows into the
    // shared DateTaken/Effective capture date instead.
    VideoMetadata? Video = null);

// Curated, safe subset of probed video metadata (ffprobe). Present only when
// VideoExtractionStatus == completed for a video blob. Dimensions come from the
// shared Width/Height on the Blob block. All fields are non-sensitive.
public sealed record VideoMetadata(
    double? DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    double? FrameRate,
    long? VideoBitrate,
    bool HasAudio,
    int? AudioChannels,
    int? AudioSampleRate,
    int? Rotation);

// Curated, safe subset of embedded image metadata (slice 54). Present only
// when extraction completed for an image blob. DELIBERATELY OMITS sensitive /
// privacy-relevant fields: GPS coordinates (only a HasGps boolean is exposed),
// serial numbers, software, lens make, owner/artist/copyright, and the raw
// metadata document. Those stay internal until the privacy slice (57).
public sealed record EmbeddedImageMetadata(
    DateTime? DateTaken,
    string? DateTakenSource,
    int? Orientation,
    string? CameraMake,
    string? CameraModel,
    string? LensModel,
    int? Iso,
    double? Aperture,
    string? ExposureTime,
    double? FocalLength,
    string? ColorSpace,
    bool HasGps);

// Owner-private annotations/overrides for this specific FileItem. Never shared
// across users even when the blob is deduplicated.
public sealed record UserMetadataView(
    string? Title,
    string? Description,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool Favorite,
    DateTime? DateTakenOverride,
    string? LocationOverride);

// Full-replace update of the editable user-metadata fields. Omitted/null
// fields are CLEARED (PATCH here means "patch the user-metadata document",
// not "merge individual fields"), which keeps the semantics unambiguous.
public sealed record UpdateFileMetadataRequest(
    string? Title,
    string? Description,
    IReadOnlyList<string>? Tags,
    int? Rating,
    bool? Favorite,
    DateTime? DateTakenOverride,
    string? LocationOverride);

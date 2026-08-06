namespace NubArca.Api.Files;

// Stable, sanitized vocabulary for media-derivative diagnostics. These strings
// land in derivative_diagnostics rows, Admin stats aggregates, CLI output, and
// operator SQL — so they are treated as a public contract: snake_case, additive
// only, never a raw exception string. Mirrors the MetadataErrorCodes style.

// Lifecycle status of a logical derivative target. "succeeded" is intentionally
// absent: success is the presence of a file_thumbnails row (and the absence of
// a diagnostic row), so it is never persisted here.
public static class DerivativeStatuses
{
    // Recorded but not yet attempted (reserved; the backfill records concrete
    // outcomes, not bare placeholders).
    public const string Pending = "pending";

    // Deliberately not produced for a reason that is not a failure (e.g. the
    // EnableThumbnails kill-switch is off).
    public const string Skipped = "skipped";

    // The target is not eligible for batch generation (e.g. media-library
    // excluded). Lazy on-request generation may still serve it.
    public const string NotEligible = "not_eligible";

    // Attempted, failed, and worth retrying (storage hiccup, source bytes
    // temporarily unavailable). Carries a backoff via NextRetryAt.
    public const string FailedTransient = "failed_transient";

    // Attempted and failed deterministically for the same bytes (corrupt /
    // unsupported / over a safety limit). Skipped by default; retried only by
    // an explicit forced run.
    public const string FailedPermanent = "failed_permanent";

    // Generation was cancelled (slice yield / shutdown). NEVER persisted as a
    // failure — the constant exists for completeness only.
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) => status is
        Pending or Skipped or NotEligible or FailedTransient or FailedPermanent or Cancelled;

    // Whether a row blocks the DEFAULT backfill from re-attempting the target.
    // Transient rows block only until NextRetryAt; everything non-transient that
    // is not a fresh pending placeholder blocks until an explicit retry.
    public static bool IsBlocking(string? status) => status is
        FailedPermanent or NotEligible or Skipped;
}

// Stable machine-readable failure/skip reasons. Prefer these structured codes
// over free-form text; Message (if any) is sanitized and bounded.
public static class DerivativeErrorCodes
{
    // The bytes are not a format the decoder supports at all.
    public const string UnsupportedFormat = "unsupported_format";

    // Header-only Identify could not recognise the image.
    public const string IdentifyFailed = "identify_failed";

    // Identify succeeded but full decode threw (corrupt / truncated / an
    // unsupported sub-format).
    public const string DecodeFailed = "decode_failed";

    // Source byte size exceeds the configured input cap.
    public const string TooLargeBytes = "too_large_bytes";

    // Width or height exceeds the configured dimension cap.
    public const string TooLargeDimensions = "too_large_dimensions";

    // Width × height exceeds the configured pixel cap (decompression-bomb gate).
    public const string TooManyPixels = "too_many_pixels";

    // The source has no usable dimensions (reserved for future detectors).
    public const string NoDimensions = "no_dimensions";

    // The source BlobObject row (or its bytes) is missing.
    public const string SourceBlobMissing = "source_blob_missing";

    // Excluded from the media library by an owner folder rule.
    public const string MediaLibraryExcluded = "media_library_excluded";

    // Generic "not eligible" (e.g. thumbnail generation disabled).
    public const string NotEligible = "not_eligible";

    // Generation was cancelled. Never persisted as a failure.
    public const string Cancelled = "cancelled";

    // Writing/storing the derived blob failed.
    public const string StorageError = "storage_error";

    // Persisting the file_thumbnails row failed (non-race).
    public const string DbError = "db_error";

    // A generation step exceeded its time budget (reserved for future backends).
    public const string Timeout = "timeout";

    // Anything else, sanitized to a code.
    public const string Unknown = "unknown";
}

// Generator backend identity, so a future native/Rust/FFmpeg backend can
// selectively re-attempt rows recorded by an older generator.
public static class DerivativeBackends
{
    public const string ImageSharp = "imagesharp";
    // Slice 100: high-performance libvips image backend.
    public const string Vips = "vips";
    public const string Synthetic = "synthetic";
    public const string Ffmpeg = "ffmpeg";
}

public static class DerivativeGenerators
{
    // Bump when the generation pipeline changes in a way that warrants
    // re-attempting previously-failed targets.
    public const int ImageVersion = 1;
    public const int PosterVersion = 1;
    public const int VideoPreviewStripVersion = 1;
}

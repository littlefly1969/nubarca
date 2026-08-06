namespace NubArca.Api.Metadata;

// Sanitized, machine-readable error codes for embedded-metadata extraction.
// We NEVER store raw exception text (it can echo file bytes / paths) — only
// one of these stable codes lands in BlobMetadata.ExtractionErrorCode.
public static class MetadataErrorCodes
{
    // The bytes are not a format the extractor can read at all.
    public const string UnsupportedFormat = "unsupported_format";

    // An I/O problem occurred reading the blob stream.
    public const string IoError = "io_error";

    // Any other unexpected failure during extraction.
    public const string Unexpected = "unexpected_error";

    // Extraction succeeded but the raw metadata document exceeded the size cap
    // and was replaced with a truncation marker. Not a hard failure.
    public const string RawTruncated = "raw_truncated";

    // The external probe tool (ffprobe) exited non-zero — the input was not a
    // parseable media container. A content (not environment) outcome.
    public const string ProbeFailed = "probe_failed";

    // The external probe tool did not finish within its timeout window.
    public const string Timeout = "timeout";

    // No video-metadata provider is configured (Media:VideoMetadataProvider =
    // "none"). An environment/config state, NOT a content failure.
    public const string ProviderDisabled = "provider_disabled";
}

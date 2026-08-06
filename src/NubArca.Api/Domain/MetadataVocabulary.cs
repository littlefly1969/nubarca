namespace NubArca.Api.Domain;

// Coarse media buckets derived from a blob's bytes / MIME type. Stored as a
// short string on BlobMetadata.MediaCategory.
public static class MediaCategories
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Document = "document";
    public const string Other = "other";

    // Effective-metadata fallback only: used when a FileItem predates the
    // metadata model and has no BlobMetadata row, so we genuinely don't know.
    public const string Unknown = "unknown";

    public static string FromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return Other;
        }

        var mime = mimeType.Trim().ToLowerInvariant();
        if (mime.StartsWith("image/", StringComparison.Ordinal)) return Image;
        if (mime.StartsWith("video/", StringComparison.Ordinal)) return Video;
        if (mime.StartsWith("audio/", StringComparison.Ordinal)) return Audio;
        if (mime.StartsWith("text/", StringComparison.Ordinal)
            || mime == "application/pdf"
            || mime.StartsWith("application/msword", StringComparison.Ordinal)
            || mime.StartsWith("application/vnd.openxmlformats", StringComparison.Ordinal))
        {
            return Document;
        }
        return Other;
    }
}

// State values for the thumbnail and embedded-metadata-extraction pipelines.
// Stored as short strings on BlobMetadata.{ThumbnailStatus,ExtractionStatus}.
public static class MetadataStatuses
{
    // Not yet attempted.
    public const string Pending = "pending";

    // Finished successfully (thumbnail produced / extraction done).
    public const string Generated = "generated";
    public const string Completed = "completed";

    // Deliberately not produced (e.g. non-image has no thumbnail).
    public const string Skipped = "skipped";

    // Attempted and failed; see ExtractionErrorCode.
    public const string Failed = "failed";

    // Effective-metadata fallback only: a pre-metadata-model file with no
    // BlobMetadata row, so the real status is unknown.
    public const string Unknown = "unknown";
}

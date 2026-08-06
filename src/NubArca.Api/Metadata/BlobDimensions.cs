namespace NubArca.Api.Metadata;

/// <summary>
/// Sanitizes detected image dimensions before they are written to the
/// <c>blob_metadata</c> row. The table's CHECK constraints require Width and
/// Height to be NULL or strictly positive (<c>ck_blob_metadata_width_positive</c>
/// / <c>ck_blob_metadata_height_positive</c>) and PixelCount to be non-negative.
/// Some malformed images make the detection layer report a non-positive
/// dimension (e.g. <c>Height = 0</c>); writing that verbatim throws a
/// deterministic <c>DbUpdateException</c> (23514) that fails the whole import.
/// Both ingest paths — the admin batch import and the per-file upload path —
/// funnel detected dimensions through here so the rule lives in exactly one
/// place and the two stay in sync.
/// </summary>
public static class BlobDimensions
{
    /// <summary>
    /// Coerces non-positive or null dimensions to NULL. PixelCount is computed
    /// only when BOTH sanitized dimensions are present and positive, in 64-bit
    /// to avoid int32 overflow on very large images.
    /// </summary>
    public static (int? Width, int? Height, long? PixelCount) Normalize(int? width, int? height)
    {
        var w = width is int rawW && rawW > 0 ? rawW : (int?)null;
        var h = height is int rawH && rawH > 0 ? rawH : (int?)null;
        var pixelCount = w is int pw && h is int ph ? (long)pw * ph : (long?)null;
        return (w, h, pixelCount);
    }
}

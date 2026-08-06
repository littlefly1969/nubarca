namespace NubArca.Api.Metadata;

// Writes a small, explicit, safe metadata value INTO image bytes (slice 66).
// Unlike the stripper (which removes everything), the writer preserves the
// existing image + its other metadata and sets exactly one curated field.
//
// This slice supports only DateTaken (EXIF DateTimeOriginal / DateTimeDigitized)
// and only for JPEG, where ImageSharp's ExifProfile round-trips reliably
// through the MetadataExtractor read path. Other formats / fields are
// deferred and surface as UnsupportedImageFormatException (415-mapped).
//
// Implementations MUST be safe on arbitrary input: every decode/encode
// failure becomes UnsupportedImageFormatException or ImageProcessingLimitException.
// They MUST NOT throw raw library exceptions to callers, and MUST NOT mutate
// the input stream.
public interface IImageMetadataWriter
{
    // True when this writer can bake a DateTaken value into the given
    // server-detected content type. Used by FileItemService to reject
    // unsupported formats before opening the blob bytes.
    bool SupportsDateTaken(string? detectedContentType);

    // Decodes the input, sets the EXIF capture-date tags to `dateTakenUtc`
    // (treated as a wall-clock value; EXIF has no timezone field), preserves
    // all other image data + metadata, and re-encodes to a fresh in-memory
    // stream positioned at 0. The caller owns the returned stream.
    Task<MemoryStream> WriteDateTakenAsync(
        Stream input,
        string detectedContentType,
        DateTime dateTakenUtc,
        CancellationToken cancellationToken = default);
}

// The caller requested a strong metadata operation that needs a user-supplied
// value which is not present (e.g. write-datetaken with no DateTaken override
// set on the file). Mapped to HTTP 400 by the endpoint.
public sealed class MetadataOperationInputMissingException : Exception
{
    public MetadataOperationInputMissingException(string message) : base(message) { }
}

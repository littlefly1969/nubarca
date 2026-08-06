namespace NubArca.Api.Metadata;

// Produces a metadata-stripped copy of an image (slice 58). The stripper
// receives the original bytes and returns a buffered stream of new bytes
// with EXIF / IPTC / XMP / ICC / format-text metadata removed. The original
// stream is never modified.
//
// Implementations MUST be safe to call on arbitrary user input — every
// decode/encode failure becomes either UnsupportedImageFormatException
// (415-mapped) or ImageProcessingLimitException (415-mapped). They MUST
// NOT throw raw library exceptions to callers.
public interface IImageMetadataStripper
{
    // Quick predicate. Used by the FileItemService to reject non-image /
    // unsupported formats before opening the blob bytes.
    bool IsSupported(string? detectedContentType);

    // Decodes the input, removes all metadata the library exposes, and
    // re-encodes to a fresh in-memory stream positioned at 0. The caller
    // owns the returned stream and must dispose it.
    Task<MemoryStream> StripAsync(
        Stream input,
        string detectedContentType,
        CancellationToken cancellationToken = default);
}

// Caller asked to strip a file whose server-detected content type is not in
// the supported set. Mapped to HTTP 415 by the endpoint.
public sealed class UnsupportedImageFormatException : Exception
{
    public string? ContentType { get; }

    public UnsupportedImageFormatException(string? contentType)
        : base(BuildMessage(contentType))
    {
        ContentType = contentType;
    }

    private static string BuildMessage(string? contentType)
        => string.IsNullOrEmpty(contentType)
            ? "Stripping metadata for files of unknown type is not supported."
            : $"Stripping metadata for files of type '{contentType}' is not supported.";
}

// Decoding or re-encoding the image would exceed the configured resource
// caps (`ImageProcessingOptions`). Mapped to HTTP 415 by the endpoint.
public sealed class ImageProcessingLimitException : Exception
{
    public ImageProcessingLimitException(string message) : base(message) { }
}

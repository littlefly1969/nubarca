namespace NubArca.Api.Metadata;

public interface IEmbeddedMetadataExtractor
{
    // Parses embedded metadata (EXIF / GPS / IPTC / XMP / ICC / MakerNotes /
    // format chunks) from an image byte stream and returns normalized typed
    // fields plus an internal raw structured document.
    //
    // MUST NOT throw for corrupt / unsupported / oversized metadata: every
    // failure path returns a result with a safe Status + sanitized ErrorCode
    // so the upload pipeline can always complete. The caller owns the stream.
    ImageMetadataExtractionResult Extract(Stream imageStream);
}

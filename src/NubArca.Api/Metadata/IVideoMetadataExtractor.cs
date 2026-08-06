namespace NubArca.Api.Metadata;

public interface IVideoMetadataExtractor
{
    // Probes a video blob for container/stream metadata (duration, dimensions,
    // codecs, frame rate, bitrate, audio shape, rotation, creation time).
    //
    // The blob content is provided through an open-delegate (the implementation
    // may need a seekable temp copy, e.g. ffprobe). MUST NOT throw for corrupt /
    // unsupported / oversized input or a missing/failed external tool: every
    // failure path returns a result with a safe Status + sanitized ErrorCode so
    // the backfill can always complete. The caller owns nothing after the call.
    Task<VideoMetadataExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken);
}

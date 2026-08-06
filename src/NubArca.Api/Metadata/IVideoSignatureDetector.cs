namespace NubArca.Api.Metadata;

// Slice 62: header-only video sniffer. Reads a small number of leading bytes
// from a blob stream and returns a normalized content type + container name
// when the bytes match a known signature, or null when the stream is not a
// supported video. Never throws — any read failure resolves to null so the
// upload path can fall through to the "non-image, non-video" branch.
//
// No native dependencies. Recognises:
//   * MP4 / MOV: looks for the "ftyp" box at offset 4 and inspects the brand
//     starting at offset 8. Brands beginning with isom/iso2/mp4/avc1/M4V/M4A/dash
//     → "video/mp4"; brand "qt  " (with spaces) → "video/quicktime".
//   * WebM / Matroska: looks for the EBML magic (1A 45 DF A3) at offset 0 and
//     attempts to find the "webm" or "matroska" DocType string inside the
//     header (any of the first 64 bytes). Both map to "video/webm" because
//     mainstream browsers play webm; raw Matroska is best-effort.
//
// Ogg is intentionally NOT included in this slice — distinguishing an audio
// Ogg from a video Ogg requires parsing the codec stream which is beyond
// the magic-bytes scope. Future slice may add it.
public interface IVideoSignatureDetector
{
    Task<VideoSignature?> InspectAsync(Stream stream, CancellationToken cancellationToken = default);
}

public sealed record VideoSignature(string ContentType, string Container);

namespace NubArca.Api.Security;

// Content-type policy for serving stored bytes (slice 54.2). Client-supplied
// MIME is untrusted: it could be text/html or a fake image/* used to coax a
// browser into rendering/executing attacker content. Only a SERVER-DETECTED
// image type from a small allowlist is trusted; everything else collapses to
// application/octet-stream. Always pair with X-Content-Type-Options: nosniff
// and (for downloads) Content-Disposition: attachment.
public static class SafeContentType
{
    public const string Fallback = "application/octet-stream";

    public const string NoSniffHeader = "X-Content-Type-Options";
    public const string NoSniffValue = "nosniff";

    private static readonly HashSet<string> TrustedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/tiff",
    };

    // Slice 62: server-detected video types safe to serve under the
    // /api/files/{id}/video endpoint. Browsers will render these inside a
    // <video> element only when the codecs inside the container are also
    // supported, but the container-level MIME is what governs how the byte
    // stream is dispatched to the media stack.
    // Public array form so EF queries can translate a Contains() over it
    // (admin HLS backfill candidate scan); the HashSet below stays the fast
    // path for per-request checks.
    public static readonly string[] TrustedVideoTypeList =
    [
        "video/mp4", "video/webm", "video/quicktime",
    ];

    private static readonly HashSet<string> TrustedVideoTypes =
        new(TrustedVideoTypeList, StringComparer.OrdinalIgnoreCase);

    // Returns the content type safe to serve. `detectedContentType` is the
    // server-sniffed type from BlobMetadata (null when detection failed or the
    // blob is not a recognized image). A trusted image type is served as-is so
    // authorized image previews/lightbox still render; anything else — including
    // every client-supplied MIME — becomes application/octet-stream.
    public static string ForServing(string? detectedContentType)
        => detectedContentType is not null && TrustedImageTypes.Contains(detectedContentType)
            ? detectedContentType.ToLowerInvariant()
            : Fallback;

    // Slice 62: video-playback-aware content type. Used by the authenticated
    // /api/files/{id}/video endpoint, which is only reached when the blob is
    // a server-detected video. Returns the trusted detected video MIME, or
    // application/octet-stream if for any reason the detection slot doesn't
    // match the allowlist.
    public static string ForServingVideo(string? detectedContentType)
        => detectedContentType is not null && TrustedVideoTypes.Contains(detectedContentType)
            ? detectedContentType.ToLowerInvariant()
            : Fallback;

    public static bool IsTrustedVideo(string? detectedContentType)
        => detectedContentType is not null && TrustedVideoTypes.Contains(detectedContentType);

    // A video the server can PREPARE with ffmpeg (poster frame, hover strip,
    // HLS ladder) — a deliberately broader notion than IsTrustedVideo.
    //
    // IsTrustedVideo answers "may I hand these ORIGINAL bytes to a browser as
    // this MIME type?", and only a header-sniffed mp4/webm/quicktime qualifies.
    // That is the right gate for the direct-stream path, but it wrongly excludes
    // perfectly real videos in legacy containers (AVI/DivX/MJPEG/DV): the sniffer
    // does not recognise them, so ~640 real videos got no poster and no HLS even
    // though ffprobe had parsed them fine.
    //
    // This predicate instead asks "did the SERVER itself confirm a decodable
    // video stream?" — true when the header sniff already trusts it, OR when the
    // ffprobe pipeline COMPLETED and reported an actual video codec. Both signals
    // are server-derived, never client-supplied: a spoofed upload (e.g. text sent
    // as video/mp4) fails ffprobe, so VideoExtractionStatus is not `completed`
    // and VideoCodec stays null — it is excluded exactly as before.
    //
    // SAFETY: use this ONLY where the response is ffmpeg-PRODUCED output (a JPEG
    // poster/strip, HLS segments). Never use it to serve original bytes — that
    // path must keep IsTrustedVideo, because the Content-Type it picks comes from
    // the sniffed type.
    public static bool IsServerConfirmedVideo(
        string? detectedContentType, string? videoExtractionStatus, string? videoCodec)
        => IsTrustedVideo(detectedContentType)
            || (string.Equals(videoExtractionStatus, "completed", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(videoCodec));

    // Allow-list check used as a cheap pre-gate on UPLOAD (anonymous party
    // upload). The client MIME is untrusted — a pass here is necessary but not
    // sufficient; callers still confirm the SERVER-detected media category.
    public static bool IsTrustedImage(string? contentType)
        => contentType is not null && TrustedImageTypes.Contains(contentType);
}

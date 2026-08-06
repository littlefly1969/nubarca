namespace NubArca.Api.Domain;

// Video-hls slice 1: lifecycle row for the HLS playback derivative of ONE
// source blob. Keyed by BlobObjectId (unique) — NOT FileItemId — because the
// transcode is expensive and every FileItem sharing the same content-addressed
// bytes must share one ladder. The physical files (master.m3u8 + per-rendition
// playlists/segments) are NOT tracked here: their location is derived
// deterministically from the source blob's sha256 (HlsDerivativeStorage), the
// same invariant original blobs follow. Like every derived artifact the files
// are regenerable cache — a missing directory with a "ready" row simply
// triggers regeneration.
//
// Status semantics mirror the AI-substrate convention: a MISSING row means
// implicit pending (nothing attempted yet — rows are never pre-materialized
// for every video); "pending" means generation has been claimed/enqueued;
// "ready" means the ladder was published; "failed" is a content-related
// failure retried only via an explicit --force.
public class BlobHlsDerivative
{
    public Guid Id { get; set; }

    public Guid BlobObjectId { get; set; }

    public string Status { get; set; } = VideoHlsStatuses.Pending;

    // Sanitized machine-readable code for a failed run (never raw tool
    // output); null when pending/ready.
    public string? ErrorCode { get; set; }

    // Version of the transcoder pipeline that produced (or failed) this row,
    // so a future backfill can re-run only rows from an older pipeline.
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadyAt { get; set; }
}

public static class VideoHlsStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

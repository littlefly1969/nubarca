namespace NubArca.Api.Ai.Video;

// VSEM-01: the ONE place that turns "authoritative video metadata has just been
// persisted for this blob" into a segmentation job.
//
// Explicit completion seam, not polling: the video-metadata backfill calls this
// after the probe result is committed, so segmentation can never run against a
// blob whose duration and video-stream fields are not yet known.
public interface IVideoSemanticSegmentationScheduler
{
    // Best-effort. Returns true when a job was enqueued. Never throws for a
    // scheduling problem — a hiccup here must not fail the metadata probe that
    // just succeeded.
    Task<bool> TryScheduleForBlobAsync(Guid blobObjectId, CancellationToken cancellationToken = default);
}

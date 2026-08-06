namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: post-segmentation scheduling seam. Implemented by the real job-queue
// scheduler; faked in tests.
public interface IVideoFaceAnalysisScheduler
{
    // Enqueue a single-blob face-analysis job for a freshly COMPLETED manifest.
    // Returns false (without throwing) when the capability is disabled, the
    // intended face profile is unusable, or the queue is unavailable — the
    // bounded backfill remains the durable source of truth.
    Task<bool> TryScheduleForBlobAsync(
        Guid blobObjectId, int segmentationVersion, CancellationToken cancellationToken = default);
}

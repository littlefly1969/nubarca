namespace NubArca.Api.Ai.Video;

// VSEM-02: post-segmentation scheduling seam. Implemented by the real
// job-queue scheduler; faked in tests.
public interface IVideoSemanticEmbeddingScheduler
{
    // Enqueue a single-blob embedding job for a freshly COMPLETED manifest.
    // Returns false (without throwing) when the capability is disabled, the
    // intended profile is unusable, or the queue is unavailable — the bounded
    // backfill remains the durable source of truth.
    Task<bool> TryScheduleForBlobAsync(
        Guid blobObjectId, int segmentationVersion, CancellationToken cancellationToken = default);
}

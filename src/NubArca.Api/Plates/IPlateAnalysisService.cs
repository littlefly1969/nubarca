using NubArca.Api.Jobs;

namespace NubArca.Api.Plates;

// Owner-private ALPR analysis orchestration for the Plates surface. The API side
// (RequestAnalysisAsync) only ENQUEUES a job; the actual detection+OCR runs on
// the worker (AnalyzeAsync, invoked by PlateAnalysisJobHandler). Every method is
// owner-scoped; a foreign/missing image resolves to null (endpoint → 404).
public interface IPlateAnalysisService
{
    // Requests analysis for an owner's PlateImage. If an analysis is already
    // queued/running for the image, returns that job (idempotent) instead of
    // enqueuing a second. Null when the image is missing/foreign.
    Task<PlateAnalysisJobSummary?> RequestAnalysisAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default);

    // Latest analysis summary for polling. Null when the image is missing/foreign.
    Task<PlateAnalysisSummary?> GetLatestSummaryAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default);

    // Summary + latest detections for the image detail DTO.
    Task<(PlateAnalysisSummary Summary, IReadOnlyList<PlateDetectionDto> Detections)> LoadForDetailAsync(
        Guid ownerUserId, Guid plateImageId, string plateImageStatus, CancellationToken cancellationToken = default);

    // Latest-detection counts for a set of images (for the list DTO).
    Task<IReadOnlyDictionary<Guid, int>> CountDetectionsForImagesAsync(
        Guid ownerUserId, IReadOnlyList<Guid> plateImageIds, CancellationToken cancellationToken = default);

    // Worker entry point: runs the ALPR pipeline for one PlateAnalysisJob and
    // persists the outcome. Missing/terminal jobs are no-ops (safe). Cancellation
    // is never recorded as a permanent failure.
    Task AnalyzeAsync(Guid analysisJobId, JobContext context, CancellationToken cancellationToken = default);
}

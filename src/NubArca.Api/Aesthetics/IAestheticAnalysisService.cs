using NubArca.Api.Jobs;

namespace NubArca.Api.Aesthetics;

// Owner-private orchestration of HumanAesExpert analysis: manual, bounded batch
// enqueue (one image = one run = one durable job), run history reads, cancel,
// retry, and the worker-side execution. All model I/O goes through
// IAestheticModelClient; no inference ever runs in an API request.
public interface IAestheticAnalysisService
{
    // Manually request analysis of up to MaximumBatchItems lab items. Enforces
    // the batch cap, filters to allowed capabilities, and collapses a duplicate
    // live run for the same (item, profile, capabilities). Returns per-item
    // enqueued/skipped results. When the feature is disabled, every item is
    // skipped with a controlled reason and NO job is created.
    Task<AestheticAnalysisBatchResultDto> RequestAnalysisAsync(
        Guid ownerUserId, IReadOnlyList<Guid> itemIds, IReadOnlyList<string>? capabilities,
        CancellationToken cancellationToken = default);

    Task<AestheticRunDto?> GetRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default);

    // Cooperatively cancel a queued/running run (best-effort background-job
    // cancel + terminal run status). Returns false when missing/terminal.
    Task<bool> CancelRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default);

    // Re-run a FAILED/CANCELLED run: creates a NEW run (history preserved) with
    // the same capability set + profile and enqueues it. Returns the new run, or
    // null when the source run is missing or the feature is unavailable.
    Task<AestheticRunDto?> RetryRunAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken = default);

    // Worker entry point: drive one run to a terminal state. Idempotent + safe on
    // a missing/terminal run. Never throws for a content/model failure (records a
    // safe code); rethrows only OperationCanceledException so the processor marks
    // the background job cancelled.
    Task AnalyzeAsync(Guid runId, JobContext context, CancellationToken cancellationToken = default);
}

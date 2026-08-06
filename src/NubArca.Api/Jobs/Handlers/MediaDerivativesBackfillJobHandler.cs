using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

// Drives the existing MediaDerivativesBackfillService (slice 63).
public sealed class MediaDerivativesBackfillJobHandler : IJobHandler
{
    private readonly MediaDerivativesBackfillService _service;

    public MediaDerivativesBackfillJobHandler(MediaDerivativesBackfillService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaDerivativesBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MediaDerivativesBackfillJobPayload>(context.PayloadJson)
            ?? new MediaDerivativesBackfillJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var options = new MediaDerivativesBackfillOptions
        {
            Limit = payload.Limit,
            MissingOnly = payload.MissingOnly,
            FailedOnly = payload.FailedOnly,
            DryRun = payload.DryRun,
            // FailedOnly is the legacy alias for the forced-retry path.
            RetryFailed = payload.RetryFailed || payload.FailedOnly,
            TargetFileItemId = payload.FileItemId,
        };

        // Scheduler v2: run ONE cooperative slice. The service resumes from the
        // job's checkpoint, polls ShouldYield at safe per-item boundaries, and
        // reports live counts. When work remains, re-queue this same row as the
        // next slice so a higher-priority foreground import can run in between.
        var result = await _service.RunAsync(
            options, context.Log, cancellationToken,
            progress: (processed, total, message, ct) =>
                context.ReportProgressAsync(processed, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice => context.ShouldYield(processedThisSlice));

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
    }
}

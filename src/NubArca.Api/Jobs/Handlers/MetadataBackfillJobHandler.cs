using System.Text.Json;
using NubArca.Api.Metadata;

namespace NubArca.Api.Jobs.Handlers;

// Drives the existing MetadataBackfillService (slice 55). No business logic is
// duplicated here — the handler only translates the validated job payload into
// MetadataBackfillOptions and forwards progress.
public sealed class MetadataBackfillJobHandler : IJobHandler
{
    private readonly MetadataBackfillService _service;

    public MetadataBackfillJobHandler(MetadataBackfillService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MetadataEmbeddedBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MetadataBackfillJobPayload>(context.PayloadJson)
            ?? new MetadataBackfillJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var options = new MetadataBackfillOptions
        {
            Limit = payload.Limit,
            FailedOnly = payload.FailedOnly,
            DryRun = payload.DryRun,
            TargetBlobObjectId = payload.BlobObjectId,
        };

        // Scheduler v2: run ONE cooperative slice. The service resumes from the
        // job's checkpoint, polls ShouldYield at safe per-blob boundaries (after
        // each extraction is committed), and reports live counts. When work
        // remains, re-queue this same row as the next slice so a higher-priority
        // foreground import can run in between.
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

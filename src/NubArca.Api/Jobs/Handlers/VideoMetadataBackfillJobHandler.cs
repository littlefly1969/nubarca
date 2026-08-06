using System.Text.Json;
using NubArca.Api.Metadata;

namespace NubArca.Api.Jobs.Handlers;

// Drives VideoMetadataBackfillService (ffprobe video probing). Mirrors
// MetadataBackfillJobHandler: no business logic here — it only translates the
// validated payload into options, runs one cooperative slice, and re-queues a
// continuation when work remains. No-ops safely when the provider is disabled
// (the candidate query still runs but ReExtractVideoMetadataAsync's no-op
// extractor marks rows skipped) — post-ingest only enqueues when it is enabled.
public sealed class VideoMetadataBackfillJobHandler : IJobHandler
{
    private readonly VideoMetadataBackfillService _service;

    public VideoMetadataBackfillJobHandler(VideoMetadataBackfillService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MetadataVideoBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoMetadataBackfillJobPayload>(context.PayloadJson)
            ?? new VideoMetadataBackfillJobPayload();

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

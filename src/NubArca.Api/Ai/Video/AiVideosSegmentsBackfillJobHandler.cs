using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video;

// VSEM-01: drives VideoSemanticSegmentationBackfillService. No business logic
// here — it validates the payload, translates it into options, runs ONE
// cooperative slice and re-queues a continuation when work remains.
//
// Gating mirrors the other AI handlers: cancellation and a disabled capability
// no-op cleanly (no rows, no FFmpeg, no failure). The feature is off by
// default, so an accidentally enqueued job on a stock deployment does nothing.
public sealed class AiVideosSegmentsBackfillJobHandler : IJobHandler
{
    private readonly IOptions<VideoSemanticSegmentationOptions> _options;
    private readonly VideoSemanticSegmentationBackfillService _service;

    public AiVideosSegmentsBackfillJobHandler(
        IOptions<VideoSemanticSegmentationOptions> options,
        VideoSemanticSegmentationBackfillService service)
    {
        _options = options;
        _service = service;
    }

    public string JobType => JobTypes.AiVideosSegmentsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoSemanticSegmentsJobPayload>(context.PayloadJson)
            ?? new VideoSemanticSegmentsJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        if (payload.SegmentationVersion is int version && version <= 0)
        {
            throw new ArgumentException("SegmentationVersion must be a positive integer.");
        }

        if (context.IsCancellationRequested)
        {
            await AiSkeletonJob.NoOpAsync(context, "cancelled");
            return;
        }

        if (!_options.Value.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        var options = new VideoSemanticBackfillOptions
        {
            Limit = payload.Limit,
            FailedOnly = payload.FailedOnly,
            DryRun = payload.DryRun,
            TargetBlobObjectId = payload.BlobObjectId,
            SegmentationVersion = payload.SegmentationVersion,
        };

        var result = await _service.RunAsync(
            options, context.Log, cancellationToken,
            progress: (processed, total, message, ct) =>
                context.ReportProgressAsync(processed, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice => context.ShouldYield(processedThisSlice),
            jobId: context.JobId);

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
    }
}

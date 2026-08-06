using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

// Admin console: drives the bulk HLS pre-warm. The service pages candidates
// with a keyset cursor and reports progress per blob, so the Admin Jobs
// dashboard shows counts and the worker heartbeat renews the lease during the
// (potentially very long) run. Cancellation propagates at item boundaries and
// never records a failure.
public sealed class VideoHlsBackfillJobHandler : IJobHandler
{
    private readonly VideoHlsBackfillService _service;

    public VideoHlsBackfillJobHandler(VideoHlsBackfillService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaVideoHlsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoHlsBackfillJobPayload>(context.PayloadJson)
            ?? new VideoHlsBackfillJobPayload();
        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var result = await _service.RunAsync(
            new VideoHlsBackfillOptions
            {
                Limit = payload.Limit,
                RetryFailed = payload.RetryFailed,
                Force = payload.Force,
                DryRun = payload.DryRun,
            },
            context.Log,
            (current, total, message, ct) => context.ReportProgressAsync(current, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice => context.ShouldYield(processedThisSlice),
            cancellationToken: cancellationToken);

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
    }
}

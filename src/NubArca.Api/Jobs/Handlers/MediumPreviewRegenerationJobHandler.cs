using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

public sealed class MediumPreviewRegenerationJobHandler : IJobHandler
{
    private readonly MediumPreviewRegenerationService _service;

    public MediumPreviewRegenerationJobHandler(MediumPreviewRegenerationService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediumPreviewRegenerate;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MediumPreviewRegenerationJobPayload>(context.PayloadJson)
            ?? new MediumPreviewRegenerationJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var result = await _service.RunAsync(
            new MediumPreviewRegenerationOptions(payload.Limit),
            context.Log,
            cancellationToken,
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

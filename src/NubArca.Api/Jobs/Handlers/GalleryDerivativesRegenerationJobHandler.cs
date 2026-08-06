using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

public sealed class GalleryDerivativesRegenerationJobHandler : IJobHandler
{
    private readonly GalleryDerivativesRegenerationService _service;

    public GalleryDerivativesRegenerationJobHandler(
        GalleryDerivativesRegenerationService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaGalleryDerivativesRegenerate;

    public async Task ExecuteAsync(
        JobContext context,
        CancellationToken cancellationToken)
    {
        var payload =
            JsonSerializer.Deserialize<GalleryDerivativesRegenerationJobPayload>(
                context.PayloadJson)
            ?? new GalleryDerivativesRegenerationJobPayload();

        if (payload.Limit is <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }
        if (payload.BatchSize is <= 0)
        {
            throw new ArgumentException("BatchSize must be a positive integer.");
        }

        var result = await _service.RunAsync(
            new GalleryDerivativesRegenerationOptions
            {
                Sizes = payload.Sizes,
                Force = payload.Force,
                DryRun = payload.DryRun,
                Limit = payload.Limit,
                BatchSize = payload.BatchSize,
            },
            context.Log,
            cancellationToken,
            progress: (processed, total, message, ct) =>
                context.ReportProgressAsync(processed, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice =>
                context.ShouldYield(processedThisSlice));

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
    }
}

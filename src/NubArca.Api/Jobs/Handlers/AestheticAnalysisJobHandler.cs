using System.Text.Json;
using NubArca.Api.Aesthetics;

namespace NubArca.Api.Jobs.Handlers;

// Thin adapter: deserializes the AestheticAnalysisJobPayload and delegates the
// HumanAesExpert analysis to the owner-private AestheticAnalysisService. All
// lifecycle/outcome writes live in the service; this handler only routes.
// Cancellation propagates as an OperationCanceledException so the processor
// marks the job cancelled (never a permanent failure); a missing/terminal run is
// a safe no-op.
public sealed class AestheticAnalysisJobHandler : IJobHandler
{
    private readonly IAestheticAnalysisService _analysis;

    public AestheticAnalysisJobHandler(IAestheticAnalysisService analysis) => _analysis = analysis;

    public string JobType => JobTypes.AestheticsAnalyze;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AestheticAnalysisJobPayload>(context.PayloadJson);
        if (payload is null || payload.RunId == Guid.Empty)
        {
            throw new ArgumentException("AestheticAnalysisJobPayload.RunId is required.");
        }

        await _analysis.AnalyzeAsync(payload.RunId, context, cancellationToken);
    }
}

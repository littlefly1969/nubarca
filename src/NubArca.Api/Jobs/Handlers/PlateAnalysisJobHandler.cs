using System.Text.Json;
using NubArca.Api.Plates;

namespace NubArca.Api.Jobs.Handlers;

// Thin adapter: deserializes the PlateAnalysisJobPayload and delegates the ALPR
// work to the owner-private PlateAnalysisService. All lifecycle/outcome writes
// live in the service; this handler only routes. Cancellation propagates as an
// OperationCanceledException so the processor marks the job cancelled (never a
// permanent failure); a missing/terminal domain job is a safe no-op.
public sealed class PlateAnalysisJobHandler : IJobHandler
{
    private readonly IPlateAnalysisService _analysis;

    public PlateAnalysisJobHandler(IPlateAnalysisService analysis) => _analysis = analysis;

    public string JobType => JobTypes.PlatesAnalyze;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PlateAnalysisJobPayload>(context.PayloadJson);
        if (payload is null || payload.AnalysisJobId == Guid.Empty)
        {
            throw new ArgumentException("PlateAnalysisJobPayload.AnalysisJobId is required.");
        }

        await _analysis.AnalyzeAsync(payload.AnalysisJobId, context, cancellationToken);
    }
}

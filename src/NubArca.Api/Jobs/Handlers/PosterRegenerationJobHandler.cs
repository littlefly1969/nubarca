using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

// Drives PosterRegenerationService (replace synthetic placeholder posters with
// real ffmpeg frames). Single-pass: the service loads its candidate set once and
// processes each exactly once, so a video whose ffmpeg extraction falls back to
// synthetic is NOT reselected within the run (no infinite loop — unlike a naive
// re-query-per-slice would risk). The run is long but the worker's own heartbeat
// renews the lease while ExecuteAsync runs; progress is reported for the Admin
// Jobs dashboard and respects cancellation at each item boundary.
public sealed class PosterRegenerationJobHandler : IJobHandler
{
    private readonly PosterRegenerationService _service;

    public PosterRegenerationJobHandler(PosterRegenerationService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaPostersRegenerate;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PosterRegenerationJobPayload>(context.PayloadJson)
            ?? new PosterRegenerationJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var options = new PosterRegenerationOptions
        {
            Force = payload.Force,
            Limit = payload.Limit,
            DryRun = payload.DryRun,
        };

        await _service.RunAsync(
            options, context.Log, cancellationToken,
            progress: (current, total, message, ct) =>
                context.ReportProgressAsync(current, total, message, ct));
    }
}

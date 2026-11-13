using System.Text.Json;
using NubArca.Api.Lithophanes;

namespace NubArca.Api.Jobs.Handlers;

public sealed class LithophaneGenerateJobHandler(LithophaneService service) : IJobHandler
{
    public string JobType => JobTypes.LithophaneGenerate;
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<LithophaneJobPayload>(context.PayloadJson);
        if (payload is null || payload.ProjectId == Guid.Empty) throw new ArgumentException("A project id is required.");
        try
        {
            await service.RunSliceAsync(payload.ProjectId, context, cancellationToken);
            if (await service.CancellationRequestedAsync(context.JobId, cancellationToken))
                throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            if (await service.CancellationRequestedAsync(context.JobId, cancellationToken))
                throw new OperationCanceledException(cancellationToken);
            // The job processor persists exception messages. Never let an IO,
            // decoder or database exception put a path or key in that field.
            throw new LithophaneException("generation_failed");
        }
    }
}

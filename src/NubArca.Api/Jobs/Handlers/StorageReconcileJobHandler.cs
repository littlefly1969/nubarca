using System.Text.Json;
using NubArca.Api.Storage;

namespace NubArca.Api.Jobs.Handlers;

// Drives the existing StorageReconciliationService (slice 65). Dry-run is the
// default; destructive orphan deletion only runs when the payload explicitly
// sets DeleteOrphans.
public sealed class StorageReconcileJobHandler : IJobHandler
{
    private readonly StorageReconciliationService _service;

    public StorageReconcileJobHandler(StorageReconciliationService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.StorageReconcile;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<StorageReconcileJobPayload>(context.PayloadJson)
            ?? new StorageReconcileJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        var options = new StorageReconciliationOptions
        {
            // Deleting orphans implies a non-dry-run; otherwise honour the flag.
            DryRun = !payload.DeleteOrphans || payload.DryRun,
            DeleteOrphans = payload.DeleteOrphans,
            Limit = payload.Limit,
        };

        await _service.RunAsync(options, context.Log, cancellationToken);
    }
}

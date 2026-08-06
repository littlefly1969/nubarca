using System.Text.Json;
using NubArca.Api.Admin;

namespace NubArca.Api.Jobs.Handlers;

// Slice 81: drives a server-side import run. The payload references the
// AdminImportRun row by id; the service re-validates config + paths and
// performs the recursive import via the normal file-creation pipeline. No
// business logic is duplicated here. Idempotent/retry-safe: re-running an
// import dedupes/conflicts already-imported files rather than corrupting them.
public sealed class AdminImportJobHandler : IJobHandler
{
    private readonly IAdminImportService _service;

    public AdminImportJobHandler(IAdminImportService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.AdminImport;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AdminImportJobPayload>(context.PayloadJson);
        if (payload is null || payload.ImportRunId == Guid.Empty)
        {
            throw new ArgumentException("AdminImportJobPayload.ImportRunId is required.");
        }

        // Slice 91: the import now runs fully through Background Jobs v2 — the
        // JobContext supplies the log sink, cooperative cancellation, and
        // generic progress. The run row holds import-specific diagnostics only.
        await _service.ExecuteRunAsync(payload.ImportRunId, context, cancellationToken);
    }
}

using System.Text.Json;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Jobs.Handlers;

// Thin adapter: the MailImportRun row is the import's source of truth; the
// runner re-validates owner, source and archive, then resumes from the
// persisted manifest. Cancellation propagates as OperationCanceledException
// (the processor records the job cancelled, never failed).
public sealed class MailImportJobHandler : IJobHandler
{
    private readonly MailImportRunner _runner;

    public MailImportJobHandler(MailImportRunner runner) => _runner = runner;

    public string JobType => JobTypes.MailImport;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MailImportJobPayload>(context.PayloadJson);
        if (payload is null || payload.ImportRunId == Guid.Empty)
        {
            throw new ArgumentException("MailImportJobPayload.ImportRunId is required.");
        }

        await _runner.ExecuteAsync(payload.ImportRunId, context, cancellationToken);
    }
}

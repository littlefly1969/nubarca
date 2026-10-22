using System.Text.Json;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Jobs.Handlers;

// Thin adapter for MailTextBackfillService: one owner per job, resumed from
// the checkpoint (the last message id handled).
public sealed class MailTextsBackfillJobHandler : IJobHandler
{
    private readonly MailTextBackfillService _backfill;

    public MailTextsBackfillJobHandler(MailTextBackfillService backfill) => _backfill = backfill;

    public string JobType => JobTypes.MailTextsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MailTextsBackfillJobPayload>(context.PayloadJson);
        if (payload is null || payload.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("MailTextsBackfillJobPayload.OwnerUserId is required.");
        }

        await _backfill.RunAsync(payload.OwnerUserId, context, cancellationToken);
    }
}

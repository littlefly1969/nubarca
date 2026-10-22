using System.Text.Json;
using NubArca.Api.Access;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Jobs.Handlers;

// Thin adapter for MailTextBackfillService: one owner per job, resumed from
// the checkpoint (the last message id handled). An owner without mail.access
// gets nothing done: the job ends, and the next search re-queues it once the
// permission is back.
public sealed class MailTextsBackfillJobHandler : IJobHandler
{
    private readonly MailTextBackfillService _backfill;
    private readonly IUserPermissionService _permissions;

    public MailTextsBackfillJobHandler(MailTextBackfillService backfill, IUserPermissionService permissions)
    {
        _backfill = backfill;
        _permissions = permissions;
    }

    public string JobType => JobTypes.MailTextsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MailTextsBackfillJobPayload>(context.PayloadJson);
        if (payload is null || payload.OwnerUserId == Guid.Empty)
        {
            throw new ArgumentException("MailTextsBackfillJobPayload.OwnerUserId is required.");
        }

        var effective = await _permissions.GetCurrentAsync(payload.OwnerUserId, cancellationToken);
        if (!effective.Has(Permissions.MailAccess))
        {
            return;
        }

        await _backfill.RunAsync(payload.OwnerUserId, context, cancellationToken);
    }
}

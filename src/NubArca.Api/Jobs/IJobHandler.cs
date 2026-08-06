namespace NubArca.Api.Jobs;

// A typed handler for one job type. Handlers reuse existing services and must
// be idempotent or safely retryable — the processor may invoke a handler more
// than once for the same logical work (retries, lease-expiry recovery).
//
// The handler receives a JobContext: job id, flag-only payload, a safe log
// sink, a cooperative-cancellation signal, and a throttled progress helper.
// Handlers MUST NOT log paths, storage keys, raw metadata, or tokens.
public interface IJobHandler
{
    string JobType { get; }

    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Shared no-op completion used by the Phase 0C skeleton handlers. Logs a safe,
// content-free reason token and reports a 0/0 progress message, then returns so
// the processor marks the job succeeded. Writes NO domain rows.
internal static class AiSkeletonJob
{
    public static async Task NoOpAsync(JobContext context, string reason)
    {
        context.Log($"ai: {reason} — skeleton no-op (Phase 0C performs no inference).");
        await context.ReportProgressAsync(0, 0, reason, context.CancellationToken);
    }
}

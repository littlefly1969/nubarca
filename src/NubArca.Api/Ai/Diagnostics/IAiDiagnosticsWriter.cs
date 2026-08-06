namespace NubArca.Api.Ai.Diagnostics;

// Writes aggregate-only AI diagnostics. The API is deliberately narrow: callers
// pass a sanitized reason CODE (and at most a short sanitized message), never an
// exception, payload, vector, SHA, storage key, or path — so those structurally
// cannot reach the table. Phase 0B does not auto-emit diagnostics on every
// resolve (that would spam); this exists for explicit, occasional use.
public interface IAiDiagnosticsWriter
{
    // Record that a capability's provider was unavailable (transient/environment
    // condition, target kind "provider"). reasonCode is a short controlled token
    // such as AiUnavailableReasons.*. There is deliberately NO free-text/message
    // parameter, so stack traces, payloads, paths, and secrets cannot enter a
    // diagnostic row through this path.
    Task RecordProviderUnavailableAsync(
        string capability,
        Guid? profileId,
        string reasonCode,
        CancellationToken cancellationToken = default);
}

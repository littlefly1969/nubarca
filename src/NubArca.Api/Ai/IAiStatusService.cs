namespace NubArca.Api.Ai;

// Lightweight, owner/admin-safe status snapshot. Aggregate counts + per-
// capability availability only. Exposes profile STABLE KEYS (never GUIDs),
// dimensions, and distance metrics — NEVER raw vectors, blob ids, SHA, storage
// keys, or physical paths.
public interface IAiStatusService
{
    Task<AiStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record AiStatus(
    bool Enabled,
    string DefaultProvider,
    int ModelCount,
    int ProfileCount,
    IReadOnlyList<AiCapabilityStatus> Capabilities);

public sealed record AiCapabilityStatus(
    string Capability,
    bool Available,
    string? UnavailableReason,
    string? DefaultProfileKey,
    int? Dimension,
    string? DistanceMetric);

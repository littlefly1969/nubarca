using NubArca.Api.Ai.Backends;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Resolution;

// Outcome of resolving a capability/profile to a backend. Carries only safe,
// non-internal facts: availability, sanitized reason, provider key, the
// profile's STABLE KEY (never its GUID), expected dimension and distance metric.
// Never carries raw vectors or internal identifiers.
public sealed class AiResolution
{
    public required bool IsAvailable { get; init; }
    public required string Capability { get; init; }
    public string Provider { get; init; } = AiProviders.None;
    public string? UnavailableReason { get; init; }
    public string? ProfileKey { get; init; }
    public int? Dimension { get; init; }
    public string? DistanceMetric { get; init; }

    public static AiResolution Available(string capability, string provider, AiProfile profile) => new()
    {
        IsAvailable = true,
        Capability = capability,
        Provider = provider,
        ProfileKey = profile.Key,
        Dimension = profile.Dimension,
        DistanceMetric = profile.DistanceMetric,
    };

    public static AiResolution Unavailable(
        string capability, string reason, string provider = AiProviders.None, AiProfile? profile = null) => new()
    {
        IsAvailable = false,
        Capability = capability,
        Provider = provider,
        UnavailableReason = reason,
        ProfileKey = profile?.Key,
        Dimension = profile?.Dimension,
        DistanceMetric = profile?.DistanceMetric,
    };
}

// Typed resolution: a usable backend plus its resolution facts. Backend is null
// whenever the capability is unavailable.
public sealed class AiBackendResolution<T> where T : class, IAiBackend
{
    public required AiResolution Resolution { get; init; }
    public T? Backend { get; init; }

    public bool IsAvailable => Backend is not null && Resolution.IsAvailable;

    public static AiBackendResolution<T> Available(T backend, AiResolution resolution) =>
        new() { Backend = backend, Resolution = resolution };

    public static AiBackendResolution<T> Unavailable(AiResolution resolution) =>
        new() { Backend = null, Resolution = resolution };
}

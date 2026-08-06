using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Resolution;

public sealed class AiBackendResolver : IAiBackendResolver
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiProfileRegistry _registry;
    private readonly IReadOnlyList<IAiBackend> _backends;

    public AiBackendResolver(
        IOptions<AiOptions> options,
        IAiProfileRegistry registry,
        IEnumerable<IAiBackend> backends)
    {
        _options = options;
        _registry = registry;
        _backends = backends.ToList();
    }

    public async Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
        string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
    {
        if (!_options.Value.Enabled)
        {
            return Unavailable<T>(capability, AiUnavailableReasons.Disabled);
        }

        var profile = await _registry.GetDefaultProfileAsync(capability, cancellationToken);
        if (profile is null)
        {
            return Unavailable<T>(capability, AiUnavailableReasons.NoDefaultProfile);
        }

        return await ResolveProfileAsync<T>(profile, capability, cancellationToken);
    }

    public async Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
        string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
    {
        var profile = await _registry.GetProfileByKeyAsync(profileKey, cancellationToken);
        if (profile is null)
        {
            // Capability unknown when the profile does not exist.
            return Unavailable<T>(string.Empty, AiUnavailableReasons.ProfileNotFound);
        }

        if (!_options.Value.Enabled)
        {
            return Unavailable<T>(profile.Capability, AiUnavailableReasons.Disabled, profile: profile);
        }

        return await ResolveProfileAsync<T>(profile, profile.Capability, cancellationToken);
    }

    public async Task<AiResolution> GetCapabilityAvailabilityAsync(
        string capability, CancellationToken cancellationToken = default)
    {
        return capability switch
        {
            AiCapabilities.ImageEmbedding =>
                (await ResolveForCapabilityAsync<IImageEmbedder>(capability, cancellationToken)).Resolution,
            AiCapabilities.DocumentEmbedding =>
                (await ResolveForCapabilityAsync<ITextEmbedder>(capability, cancellationToken)).Resolution,
            AiCapabilities.DocumentExtraction =>
                (await ResolveForCapabilityAsync<ITextExtractor>(capability, cancellationToken)).Resolution,
            AiCapabilities.FaceDetection =>
                (await ResolveForCapabilityAsync<IFaceDetector>(capability, cancellationToken)).Resolution,
            AiCapabilities.FaceEmbedding =>
                (await ResolveForCapabilityAsync<IFaceEmbedder>(capability, cancellationToken)).Resolution,
            AiCapabilities.Tagging =>
                (await ResolveForCapabilityAsync<IAiTagger>(capability, cancellationToken)).Resolution,
            AiCapabilities.Captioning =>
                (await ResolveForCapabilityAsync<IImageCaptioner>(capability, cancellationToken)).Resolution,
            _ => _options.Value.Enabled
                ? AiResolution.Unavailable(capability, AiUnavailableReasons.CapabilityUnsupported)
                : AiResolution.Unavailable(capability, AiUnavailableReasons.Disabled),
        };
    }

    private async Task<AiBackendResolution<T>> ResolveProfileAsync<T>(
        AiProfile profile, string capability, CancellationToken cancellationToken) where T : class, IAiBackend
    {
        if (!profile.Enabled)
        {
            return Unavailable<T>(capability, AiUnavailableReasons.ProfileDisabled, profile: profile);
        }

        var model = await _registry.GetModelAsync(profile.AiModelId, cancellationToken);
        if (model is null || !model.Enabled)
        {
            return Unavailable<T>(capability, AiUnavailableReasons.ModelUnavailable, profile: profile);
        }

        var provider = model.Provider;

        // "none" is an environment/config state, not a content failure: callers
        // must treat this as a no-op and never write skipped/failed status rows.
        if (string.Equals(provider, AiProviders.None, StringComparison.Ordinal))
        {
            return Unavailable<T>(capability, AiUnavailableReasons.ProviderNone, provider, profile);
        }

        var backend = _backends.FirstOrDefault(b =>
            string.Equals(b.Provider, provider, StringComparison.Ordinal)
            && b.Supports(capability)
            && b is T);

        if (backend is T typed)
        {
            // Backend matched, but is its environment ready for this profile?
            // (e.g. ONNX model files present). Not ready = unavailable, never a
            // content failure — callers no-op and write no per-blob status rows.
            var readiness = typed.CheckReadiness(profile);
            if (!readiness.IsReady)
            {
                return Unavailable<T>(
                    capability, readiness.Reason ?? AiUnavailableReasons.BackendNotReady, provider, profile);
            }

            return AiBackendResolution<T>.Available(typed, AiResolution.Available(capability, provider, profile));
        }

        var anyForProvider = _backends.Any(b => string.Equals(b.Provider, provider, StringComparison.Ordinal));
        var reason = anyForProvider
            ? AiUnavailableReasons.CapabilityUnsupported
            : AiUnavailableReasons.ProviderUnavailable;
        return Unavailable<T>(capability, reason, provider, profile);
    }

    private static AiBackendResolution<T> Unavailable<T>(
        string capability, string reason, string provider = AiProviders.None, AiProfile? profile = null)
        where T : class, IAiBackend
        => AiBackendResolution<T>.Unavailable(
            AiResolution.Unavailable(capability, reason, provider, profile));
}

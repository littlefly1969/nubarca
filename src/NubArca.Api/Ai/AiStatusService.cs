using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai;

public sealed class AiStatusService : IAiStatusService
{
    // Backend-served capabilities surfaced in status. Face clustering is a
    // derived job (not a backend capability) and is intentionally omitted here.
    private static readonly string[] BackendCapabilities =
    {
        AiCapabilities.ImageEmbedding,
        AiCapabilities.DocumentExtraction,
        AiCapabilities.DocumentEmbedding,
        AiCapabilities.FaceDetection,
        AiCapabilities.FaceEmbedding,
        AiCapabilities.Tagging,
        AiCapabilities.Captioning,
    };

    private readonly IOptions<AiOptions> _options;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiBackendResolver _resolver;

    public AiStatusService(
        IOptions<AiOptions> options,
        IAiProfileRegistry registry,
        IAiBackendResolver resolver)
    {
        _options = options;
        _registry = registry;
        _resolver = resolver;
    }

    public async Task<AiStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var models = await _registry.ListModelsAsync(cancellationToken: cancellationToken);
        var profiles = await _registry.ListProfilesAsync(cancellationToken: cancellationToken);

        var capabilities = new List<AiCapabilityStatus>(BackendCapabilities.Length);
        foreach (var capability in BackendCapabilities)
        {
            // SEARCH-SEM-01: report the profile the RUNTIME actually uses.
            //
            // Photo similarity resolves Ai:PhotoSimilarityProfileKey and faces
            // resolve Ai:FaceProfileKey; only capabilities without such a
            // setting fall back to the capability default. Reporting the
            // capability default unconditionally was actively misleading in
            // production: it named the deterministic dev/test profile
            // (dimension 32) while semantic search was in fact running on the
            // configured 1152-dimension SigLIP2 profile, which reads as "AI is
            // running on the dev backend" to an operator checking `ai status`.
            var configuredKey = ConfiguredProfileKeyFor(capability, options);
            var resolution = configuredKey is null
                ? await _resolver.GetCapabilityAvailabilityAsync(capability, cancellationToken)
                : await _resolver.GetProfileAvailabilityAsync(capability, configuredKey, cancellationToken);
            capabilities.Add(new AiCapabilityStatus(
                capability,
                resolution.IsAvailable,
                resolution.UnavailableReason,
                resolution.ProfileKey,
                resolution.Dimension,
                resolution.DistanceMetric));
        }

        return new AiStatus(
            options.Enabled,
            options.Provider,
            models.Count,
            profiles.Count,
            capabilities);
    }

    // The capabilities whose runtime profile is pinned by configuration rather
    // than by the capability default. Anything not listed here genuinely does
    // use the capability default, so it keeps reporting that.
    private static string? ConfiguredProfileKeyFor(string capability, AiOptions options)
    {
        var key = capability switch
        {
            AiCapabilities.ImageEmbedding => options.PhotoSimilarityProfileKey,
            AiCapabilities.FaceDetection => options.FaceProfileKey,
            AiCapabilities.FaceEmbedding => options.FaceProfileKey,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}

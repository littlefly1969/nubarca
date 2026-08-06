using NubArca.Api.Ai.Backends;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Resolution;

// Profile-driven backend resolution. The provider is decided by the PROFILE's
// model (AiModel.Provider), not by a single global setting — so different
// capabilities/profiles can use different providers, and new providers plug in
// without schema changes.
//
// Resolution NEVER throws for an unavailable provider/disabled AI/missing
// profile: it returns an unavailable result with a sanitized reason. Callers
// treat unavailable as a no-op and must not record per-blob skipped/failed
// status for it.
public interface IAiBackendResolver
{
    // Resolve the typed backend for a capability's default profile.
    Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
        string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend;

    // Resolve the typed backend for a specific profile (by stable key).
    Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
        string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend;

    // Availability-only query for a capability's default profile (no backend
    // handed out). Answers: available?, sanitized reason, provider, expected
    // dimension, distance metric.
    Task<AiResolution> GetCapabilityAvailabilityAsync(
        string capability, CancellationToken cancellationToken = default);

    // SEARCH-SEM-01: availability-only query for a SPECIFIC profile key under a
    // known capability. Needed because several runtime paths deliberately do
    // NOT use the capability default — photo similarity honours
    // Ai:PhotoSimilarityProfileKey and faces honour Ai:FaceProfileKey — so
    // reporting the capability default described a profile the product was not
    // actually using. Reporting-only; resolution semantics are unchanged.
    //
    // A DEFAULT implementation, expressed purely in terms of the two members
    // above: an empty key means "no pin", which is exactly the capability
    // default. Implementers therefore need no change, and the fallback is the
    // previous behaviour rather than a hole.
    async Task<AiResolution> GetProfileAvailabilityAsync(
        string capability, string profileKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return await GetCapabilityAvailabilityAsync(capability, cancellationToken);
        }

        var key = profileKey.Trim();
        return capability switch
        {
            AiCapabilities.ImageEmbedding =>
                (await ResolveForProfileKeyAsync<IImageEmbedder>(key, cancellationToken)).Resolution,
            AiCapabilities.DocumentEmbedding =>
                (await ResolveForProfileKeyAsync<ITextEmbedder>(key, cancellationToken)).Resolution,
            AiCapabilities.DocumentExtraction =>
                (await ResolveForProfileKeyAsync<ITextExtractor>(key, cancellationToken)).Resolution,
            AiCapabilities.FaceDetection =>
                (await ResolveForProfileKeyAsync<IFaceDetector>(key, cancellationToken)).Resolution,
            AiCapabilities.FaceEmbedding =>
                (await ResolveForProfileKeyAsync<IFaceEmbedder>(key, cancellationToken)).Resolution,
            AiCapabilities.Tagging =>
                (await ResolveForProfileKeyAsync<IAiTagger>(key, cancellationToken)).Resolution,
            AiCapabilities.Captioning =>
                (await ResolveForProfileKeyAsync<IImageCaptioner>(key, cancellationToken)).Resolution,
            _ => await GetCapabilityAvailabilityAsync(capability, cancellationToken),
        };
    }
}

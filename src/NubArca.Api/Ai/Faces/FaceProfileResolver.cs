using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Shared EXPLICIT face-profile selection for the detection + embedding backfills
// and the CLI. Precedence: explicit payload/--profile key > configured
// Ai:FaceProfileKey > the face-embedding capability default profile. The face
// PACKAGE (detector + recognizer) is modeled as a SINGLE face-embedding-
// capability AiProfile, so detection and embedding resolve the SAME profile and
// write rows under one consistent ProfileId. No "latest model" heuristic;
// switching models is a config change + restart.
public static class FaceProfileResolver
{
    public static Task<AiBackendResolution<IFaceDetector>> ResolveDetectorAsync(
        IAiBackendResolver resolver, string? payloadProfileKey, string? configuredProfileKey,
        CancellationToken cancellationToken = default)
        => ResolveAsync<IFaceDetector>(resolver, payloadProfileKey, configuredProfileKey, cancellationToken);

    public static Task<AiBackendResolution<IFaceEmbedder>> ResolveEmbedderAsync(
        IAiBackendResolver resolver, string? payloadProfileKey, string? configuredProfileKey,
        CancellationToken cancellationToken = default)
        => ResolveAsync<IFaceEmbedder>(resolver, payloadProfileKey, configuredProfileKey, cancellationToken);

    private static async Task<AiBackendResolution<T>> ResolveAsync<T>(
        IAiBackendResolver resolver, string? payloadProfileKey, string? configuredProfileKey,
        CancellationToken cancellationToken) where T : class, IAiBackend
    {
        var key = !string.IsNullOrWhiteSpace(payloadProfileKey)
            ? payloadProfileKey
            : (!string.IsNullOrWhiteSpace(configuredProfileKey) ? configuredProfileKey : null);

        return key is not null
            ? await resolver.ResolveForProfileKeyAsync<T>(key!, cancellationToken)
            : await resolver.ResolveForCapabilityAsync<T>(AiCapabilities.FaceEmbedding, cancellationToken);
    }
}

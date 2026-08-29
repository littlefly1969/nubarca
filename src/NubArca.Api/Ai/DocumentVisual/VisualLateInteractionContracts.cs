using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.DocumentVisual;

/// One dense candidate on its way through the pipeline.
///
/// Public because two stages hand it to each other — the dense pass produces
/// it, the late reranker reorders it — and a private shape would force one of
/// them to reach into the other.
public sealed record DocumentVisualCandidate(Guid VisualUnitId, Guid FileItemId, double Score);

/// A SEQUENCE of vectors for one input, and the identity that produced it.
///
/// The contract is stated in terms every late-interaction family satisfies —
/// ColPali, ColQwen, ColSmol, and whatever replaces them — because the thing
/// NubArca depends on is the SHAPE, not the checkpoint. A model that produces
/// per-patch vectors scored by max-similarity fits here; the specific family is
/// a configuration choice that a measurement decides.
public sealed record MultiVectorEmbeddingResult(
    IReadOnlyList<float[]> Vectors,
    int Dimension,
    string ProfileKey)
{
    public int VectorCount => Vectors.Count;
}

/// Whether a late-interaction provider can run here, right now.
public sealed record VisualProviderReadiness(bool Ready, string? Reason)
{
    public static readonly VisualProviderReadiness Available = new(true, null);

    public static VisualProviderReadiness NotReady(string reason) => new(false, reason);
}

/// A BOUNDED MULTI-VECTOR PROVIDER, deliberately model-agnostic.
///
/// NubArca is not hardcoded to ColPali, and this interface is where that
/// promise lives. The stable concept is:
///
///     query -> sequence of normalized vectors
///     page  -> sequence of normalized vectors
///     score = Σ_i max_j dot(Q_i, D_j)
///
/// Everything else — which family, which kernel, whether a specialised
/// multi-vector ANN engine sits behind it — is replaceable optimisation. What is
/// not replaceable is owner authorization, and nothing in this interface can
/// touch it: an implementation is handed bytes and a profile, and holds no
/// database, no owner id, no credentials and no network route it did not have
/// before.
///
/// Every result is validated against the profile and the configured ceilings
/// before it is stored or scored. A model whose output exceeds the declared
/// layout FAILS THE PROFILE; vectors are never truncated to fit, because a
/// MaxSim over a truncated page is a confident score for a document that does
/// not exist.
public interface IVisualLateInteractionProvider
{
    string Provider { get; }

    VisualProviderReadiness CheckReadiness(AiProfile profile);

    Task<MultiVectorEmbeddingResult> EmbedImageAsync(
        AiProfile profile, ReadOnlyMemory<byte> imageBytes, CancellationToken cancellationToken = default);

    Task<MultiVectorEmbeddingResult> EmbedQueryAsync(
        AiProfile profile, string query, CancellationToken cancellationToken = default);
}

/// Raised when a provider's output does not satisfy the contract. Carries a
/// sanitized reason token and never a model message, which can contain a path.
public sealed class VisualLateInteractionException : Exception
{
    public VisualLateInteractionException(string reasonCode)
        : base($"Late-interaction provider unavailable: {reasonCode}")
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

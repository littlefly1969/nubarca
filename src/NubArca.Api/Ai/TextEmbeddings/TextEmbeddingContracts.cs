using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.TextEmbeddings;

/// What a piece of text IS to the retrieval model.
///
/// Retrieval embedding models are asymmetric: the same sentence embedded as a
/// question and as a document lands in different places, and several of them
/// require a literal prefix (`query: `, `passage: `) to say which. That prefix
/// is MODEL syntax, so RAG must not be the layer that knows it — RAG states the
/// semantic intent and the provider applies whatever its profile's model needs.
///
/// The alternative, letting callers pass pre-decorated text, was rejected: it
/// puts one model's spelling into every call site and makes changing models a
/// search-and-replace across the retrieval code instead of a profile change.
public enum TextEmbeddingInputKind
{
    /// A question someone asked.
    Query,

    /// A passage from the corpus, to be found later.
    Passage,
}

/// One embedded text. The vector is finite, of the profile's dimension, and
/// normalized when the profile says so.
public sealed record TextEmbeddingResult(float[] Vector, int Dimension, string DistanceMetric);

/// Whether a provider can serve a profile right now, and why not when it
/// cannot. `Reason` is a sanitized token (see RagFailureReasons) — never a
/// model path, a native error or a stack trace.
public readonly record struct TextEmbeddingReadiness(bool IsReady, string? Reason)
{
    public static TextEmbeddingReadiness Ready { get; } = new(true, null);

    public static TextEmbeddingReadiness NotReady(string reason) => new(false, reason);
}

/// LOCAL text embedding, behind a profile.
///
/// There is no hosted implementation and no fallback to one. Embedding is how
/// NubArca decides what to send to a chat model; routing that decision through
/// a third party would mean the whole corpus — including, later, a person's own
/// documents — crosses a boundary to work out what may cross a boundary. A
/// missing model file is an availability condition with a reason code, and
/// retrieval degrades to lexical.
public interface ITextEmbeddingProvider
{
    /// Provider key (see AiProviders), matched against the profile's model.
    string Provider { get; }

    TextEmbeddingReadiness CheckReadiness(AiProfile profile);

    Task<TextEmbeddingResult> EmbedAsync(
        AiProfile profile,
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken = default);
}

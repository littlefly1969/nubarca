using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag;

/// The name of a retrieval domain.
///
/// A domain is a body of knowledge with ONE privacy story, and the policy that
/// governs it lives in RagDomainRegistry rather than here — this type is the
/// key, not the authority. Keeping domains separate rather than filtering one
/// index means a feature asks for the domain it is allowed to use, and cannot
/// widen its reach by passing a different argument.
public sealed record RagDomainKey(string Value)
{
    public static RagDomainKey ProductHelp { get; } = new(RagDomains.ProductHelp);
    public static RagDomainKey NubArcaRepository { get; } = new(RagDomains.NubArcaRepository);

    public override string ToString() => Value;
}

/// One retrieval request.
///
/// The DOMAIN is part of the query and is chosen by the calling feature, never
/// by the browser: `/api/help/ai/chat` has no field for it, and the Help service
/// passes `product-help` as a constant.
///
/// `OwnerUserId` is present and unused. Every system domain ignores it, and a
/// future owner-scoped domain will REQUIRE it — the field exists now so the
/// authorization question is asked at the contract rather than bolted onto a
/// retriever that already shipped without it.
public sealed record RagQuery(
    RagDomainKey Domain,
    string Text,
    Guid? OwnerUserId,
    int MaxEvidence,
    int MaxCharacters)
{
    /// System-domain shorthand. There is no owner to pass, and spelling `null`
    /// at every call site would only make the interesting case less visible.
    public RagQuery(RagDomainKey domain, string text, int maxEvidence, int maxCharacters)
        : this(domain, text, null, maxEvidence, maxCharacters)
    {
    }
}

/// Which retrieval paths actually produced a result.
///
/// Reported rather than hidden. "Help got worse this week" and "the embedding
/// model stopped loading after the last deploy" are the same event seen from
/// two ends, and only one of them is diagnosable — so a degraded run says it is
/// degraded, and says why.
public static class RagRetrievalModes
{
    public const string Lexical = "lexical";
    public const string Vector = "vector";
    public const string Hybrid = "hybrid";

    /// Lexical only, because semantic retrieval was not available. The suffix
    /// is the sanitized reason (see RagFailureReasons).
    public static string LexicalFallback(string reason) => $"lexical-fallback-{reason}";
}

/// One retrieved passage, with the metadata that made it retrievable.
///
/// Structured rather than a bare string because ranking needs the fields — a
/// how-to question should reach a user guide's section heading before it reaches
/// a sentence in a technical reference — and because the answer's citation
/// should be able to name a section rather than only a file.
///
/// The trailing fields are DIAGNOSTIC provenance: which revision this came
/// from, and how each retrieval path ranked it. They exist so `rag query` can
/// explain a ranking to a person, and they are not part of any HTTP response.
public sealed record RagEvidence(
    string Id,
    RagDomainKey Domain,
    string Path,
    string Title,
    string Section,
    string Text,
    string Feature,
    string SourceKind,
    string Audience,
    string Intent,
    string Language,
    double Score,
    string SourceKey = "",
    string Revision = "",
    int? LexicalRank = null,
    int? VectorRank = null,
    int FusionRank = 0);

/// Why a retrieval returned what it did.
///
/// The distinction between `None` and `Unavailable` is operational: nobody can
/// fix "the corpus has no good answer for that question", and an operator CAN
/// fix "this installation has no index for its revision". Both are logged as a
/// category, without the query text.
public enum RagRetrievalOutcome
{
    /// Evidence that cleared the confidence gate.
    Strong,

    /// The domain is healthy and nothing in it answers this well enough.
    None,

    /// The domain itself cannot answer — missing index, revision mismatch.
    Unavailable,
}

/// What one retrieval produced, and how.
public sealed record RagRetrievalResult(
    RagDomainKey Domain,
    RagRetrievalOutcome Outcome,
    IReadOnlyList<RagEvidence> Evidence,
    string Mode,
    string? EmbeddingProfileKey = null,
    string? Revision = null,
    string? Reason = null)
{
    public bool HasStrongEvidence => Outcome == RagRetrievalOutcome.Strong && Evidence.Count > 0;

    public static RagRetrievalResult None(RagDomainKey domain, string mode, string? revision = null)
        => new(domain, RagRetrievalOutcome.None, Array.Empty<RagEvidence>(), mode, null, revision,
            RagFailureReasons.NoStrongEvidence);

    public static RagRetrievalResult Unavailable(RagDomainKey domain, string reason)
        => new(domain, RagRetrievalOutcome.Unavailable, Array.Empty<RagEvidence>(),
            RagRetrievalModes.Lexical, null, null, reason);
}

/// What a domain's index currently holds. Aggregate counts and a revision —
/// never a chunk, never a vector, never a path outside the repository-relative
/// source key.
public sealed record RagDomainStatus(
    RagDomainKey Domain,
    bool IsAvailable,
    string? Revision,
    long Sources,
    long Chunks,
    string? EmbeddingProfileKey,
    long Embeddings,
    long Vectors,
    bool SemanticAvailable,
    string? Reason);

/// Retrieval over a NAMED domain.
///
/// The MODEL does not retrieve. It is handed a bounded set of evidence that this
/// service selected, and it has no way to ask for more — no tool, no callback,
/// no second round trip. That is what keeps "what may be sent" a finite,
/// reviewable set rather than whatever a model decides to look up.
///
/// One query names ONE domain. There is deliberately no "search everything":
/// cross-domain retrieval would mean a Product Help answer could be grounded on
/// repository evidence by accident, and the domain policy would have nothing
/// left to govern.
public interface IRagRetriever
{
    Task<RagRetrievalResult> RetrieveAsync(RagQuery query, CancellationToken cancellationToken = default);

    Task<RagDomainStatus> GetStatusAsync(RagDomainKey domain, CancellationToken cancellationToken = default);
}

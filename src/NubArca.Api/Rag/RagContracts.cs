namespace NubArca.Api.Rag;

/// The name of a retrieval domain.
///
/// A domain is a body of knowledge with ONE privacy story. `product-help` is
/// public, build-time and release-pinned; a future private domain would be
/// owner-scoped and permission-gated. Keeping them as separate domains rather
/// than as filters over one index means a feature asks for the domain it is
/// allowed to use, and cannot accidentally widen its reach by passing a
/// different argument.
public sealed record RagDomainKey(string Value)
{
    public static RagDomainKey ProductHelp { get; } = new("product-help");

    public override string ToString() => Value;
}

/// One retrieval request.
///
/// The DOMAIN is part of the query and is chosen by the calling feature, never
/// by the browser: `/api/help/ai/chat` has no field for it, and the Help service
/// passes `product-help` as a constant.
public sealed record RagQuery(
    RagDomainKey Domain,
    string Text,
    int MaxEvidence,
    int MaxCharacters);

/// One retrieved passage, with the metadata that made it retrievable.
///
/// Structured rather than a bare string because ranking needs the fields — a
/// how-to question should reach a user guide's section heading before it reaches
/// a sentence in a technical reference — and because the answer's citation
/// should be able to name a section rather than only a file.
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
    double Score);

/// Why a retrieval returned what it did.
///
/// The distinction between `None` and `Unavailable` is operational: nobody can
/// fix "the corpus has no good answer for that question", and an operator CAN
/// fix "this installation has no corpus for its revision". Both are logged as a
/// category, without the query text.
public enum RagRetrievalOutcome
{
    /// Evidence that cleared the confidence gate.
    Strong,

    /// The domain is healthy and nothing in it answers this well enough.
    None,

    /// The domain itself cannot answer — missing corpus, revision mismatch.
    Unavailable,
}

public sealed record RagResult(RagRetrievalOutcome Outcome, IReadOnlyList<RagEvidence> Evidence)
{
    public static RagResult None { get; } = new(RagRetrievalOutcome.None, Array.Empty<RagEvidence>());
    public static RagResult Unavailable { get; }
        = new(RagRetrievalOutcome.Unavailable, Array.Empty<RagEvidence>());
}

/// Retrieval over one domain.
///
/// The MODEL does not retrieve. It is handed a bounded set of evidence that this
/// service selected, and it has no way to ask for more — no tool, no callback,
/// no second round trip. That is what keeps "what may be sent" a finite,
/// reviewable set rather than whatever a model decides to look up.
public interface IRagRetriever
{
    RagDomainKey Domain { get; }

    bool IsAvailable { get; }

    /// The revision this domain's knowledge was built from, or null when there
    /// is no usable knowledge.
    string? Revision { get; }

    RagResult Retrieve(RagQuery query);
}

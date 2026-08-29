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
    public static RagDomainKey UserDocuments { get; } = new(RagDomains.UserDocuments);

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

    /// A SERVER-ONLY NARROWING of an owner-scoped question to specific files.
    ///
    /// It exists for one caller: the visual candidate expansion, which finds
    /// documents that LOOK like the question and then asks the ordinary private
    /// text retrieval what those documents actually say. Without it that second
    /// pass would have to re-rank the owner's whole corpus and hope the right
    /// file surfaced, which is the thing visual retrieval was supposed to fix.
    ///
    /// THIS IS A NARROWING AND NEVER A WIDENING. It is applied to a corpus that
    /// has ALREADY passed `OwnerDocumentEligibility.EligibleChunks`; it does not
    /// replace any part of it. A file id belonging to another owner, to a
    /// deleted file or to a vaulted one is not in that corpus at all, so it
    /// matches nothing — the allowlist can only remove candidates, never reach
    /// one.
    ///
    /// It narrows CANDIDATES, not the index. Building an index from three
    /// documents would change what BM25's term rarity is computed over, collapse
    /// the scores and make the evidence gate reject the very chunk the narrowing
    /// went looking for. See `RagLexicalIndex.Search`.
    ///
    /// AND IT IS NOT REACHABLE FROM A REQUEST. There is no `fileIds` field on
    /// any DTO, no query-string parameter, and no configuration key that reaches
    /// here. The only value it ever holds is a list the server itself just
    /// derived from this same owner's eligible visual index. A browser cannot
    /// name a file to search, which is what stops "narrow to these documents"
    /// from becoming "read these documents".
    ///
    /// Null means unscoped, which is what every other caller wants. An EMPTY
    /// list means "no files qualify" and is honoured as such — it must not be
    /// treated as null, or a visual pass that found nothing would silently widen
    /// back to the whole library.
    public IReadOnlyCollection<Guid>? AllowedFileItemIds { get; init; }
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
    int FusionRank = 0,

    /// WHOSE knowledge this is, for an owner-scoped domain. Null for every
    /// system domain, which belongs to the installation rather than a person.
    ///
    /// INTERNAL PROVENANCE, exactly like `Revision` and the rank fields: it
    /// exists so AssistantRagPolicy can check the evidence itself rather than
    /// trust that whoever retrieved it used the right owner, and it is never
    /// part of an HTTP response, a citation, a log line or a prompt. An owner id
    /// reaching a model would be a stable identifier for a person attached to
    /// text about them.
    Guid? OwnerUserId = null);

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

    /// `ownerUserId` is required for an owner-scoped domain and ignored by every
    /// system one. Optional in the signature so the system callers stay honest
    /// about having no owner, rather than passing `Guid.Empty` and making the
    /// interesting case invisible.
    Task<RagDomainStatus> GetStatusAsync(
        RagDomainKey domain, Guid? ownerUserId = null, CancellationToken cancellationToken = default);
}

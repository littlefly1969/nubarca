namespace NubArca.Api.Rag;

/// Operator configuration for document retrieval.
///
/// Bound from the "Rag" section (env `Rag__MaxQueryCharacters`, …). Semantic
/// retrieval is OFF by default and everything here is CLAMPED: a deployment may
/// make a bound tighter, and there is no value that removes one. An unbounded
/// candidate set is not a tuning option, it is a way to turn one question into
/// an unbounded amount of work.
///
/// Evidence bounds are deliberately NOT here. They belong to the CALLER — the
/// document assistant states how many chunks and how many characters it is
/// willing to send (AssistantBoundsOptions) — and a second, independent limit
/// would be a second place for the answer to "how much may leave" to be wrong.
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// PER-DOMAIN semantic settings, keyed by domain
    /// (`Rag__Domains__user-documents__SemanticEnabled=true`).
    ///
    /// There is no installation-wide switch: an owner-private corpus is
    /// somebody's own documents, and running a model over them is a decision
    /// made about that corpus, by name.
    ///
    /// Ordinal-ignore-case because a domain key is a configuration key here and
    /// an operator typing `User-Documents` should reach the domain they meant.
    public Dictionary<string, RagDomainSemanticOptions> Domains { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public int MaxQueryCharacters { get; set; } = 400;
    public int MaxLexicalCandidates { get; set; } = 60;
    public int MaxVectorCandidates { get; set; } = 60;
    public int MaxFusedCandidates { get; set; } = 60;

    /// Ceiling on how large an in-memory lexical index may become. A corpus
    /// past this reports unavailable instead of quietly consuming the host:
    /// somebody pointing the indexer at the wrong tree should get a refusal.
    public int MaxIndexedChunks { get; set; } = 200_000;

    /// Cosine floor at which a purely SEMANTIC hit counts as strong evidence on
    /// its own.
    ///
    /// Deliberately high, and deliberately not the main gate. Cosine scores are
    /// not calibrated across models — the same 0.7 means "closely related" for
    /// one checkpoint and "both are text" for another — so the evidence decision
    /// stays anchored on the lexical gate, which IS calibrated against a golden
    /// set, and this only lets an unmistakable semantic match through.
    ///
    /// This is the FALLBACK: a text-embedding catalog entry that carries its
    /// own `MinimumVectorScore` (RagTextEmbeddingModels) wins for retrievals
    /// scored by its profile. The 0.80 default was set for multilingual-e5-small.
    public double MinimumVectorScore { get; set; } = 0.80;

    public int EffectiveQueryCharacters => Math.Clamp(MaxQueryCharacters, 1, 2000);
    public int EffectiveLexicalCandidates => Math.Clamp(MaxLexicalCandidates, 1, 500);
    public int EffectiveVectorCandidates => Math.Clamp(MaxVectorCandidates, 1, 500);
    public int EffectiveFusedCandidates => Math.Clamp(MaxFusedCandidates, 1, 500);
    public int EffectiveMaxIndexedChunks => Math.Clamp(MaxIndexedChunks, 1, 2_000_000);
    public double EffectiveMinimumVectorScore => Math.Clamp(MinimumVectorScore, 0.0, 1.0);
}

/// What ONE domain says about semantic retrieval. Unset means off.
public sealed class RagDomainSemanticOptions
{
    public bool? SemanticEnabled { get; set; }

    public string? TextEmbeddingProfileKey { get; set; }
}

namespace NubArca.Api.Rag;

/// Operator configuration for the RAG substrate.
///
/// Bound from the "Rag" section (env `Rag__SemanticEnabled`, …). Semantic
/// retrieval is OFF by default and everything here is CLAMPED: a deployment may
/// make a bound tighter, and there is no value that removes one. An unbounded
/// candidate set is not a tuning option, it is a way to turn one question into
/// an unbounded amount of work.
///
/// Evidence bounds are deliberately NOT here. They belong to the CALLER — Help
/// already states how many chunks and how many characters it is willing to send
/// (AssistantHelpOptions) — and a second, independent limit would be a second
/// place for the answer to "how much may leave" to be wrong.
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// INSTALLATION-WIDE default for whether vector retrieval is attempted.
    /// False keeps the substrate purely lexical, which is a complete and
    /// supported configuration rather than a degraded one — Product Help shipped
    /// that way.
    ///
    /// A default, not the answer: see `Domains` and RagSemanticProfileResolver.
    public bool SemanticEnabled { get; set; } = false;

    /// INSTALLATION-WIDE default AiProfile key for text embeddings (e.g.
    /// `rag-text-multilingual-e5-small-v1`). Empty means semantic retrieval has
    /// no profile and is unavailable, with a reason code rather than a guess at
    /// which profile was meant.
    public string? TextEmbeddingProfileKey { get; set; }

    /// PER-DOMAIN semantic settings, keyed by domain
    /// (`Rag__Domains__product-help__SemanticEnabled=true`).
    ///
    /// One switch for the whole substrate stopped being enough the moment it was
    /// measured. `multilingual-e5-small` moves Product Help's MRR from 0.938 to
    /// 0.969 and moves the repository's Recall@5 from 0.800 DOWN to 0.700: a
    /// general-purpose multilingual sentence model asked to discriminate among
    /// 23,745 chunks of mostly C# returns plausible neighbours that are wrong.
    /// Those are not two opinions about one setting, they are two domains that
    /// want different answers.
    ///
    /// Ordinal-ignore-case because a domain key is a configuration key here and
    /// an operator typing `Product-Help` should reach the domain they meant.
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
    public double MinimumVectorScore { get; set; } = 0.80;

    public int EffectiveQueryCharacters => Math.Clamp(MaxQueryCharacters, 1, 2000);
    public int EffectiveLexicalCandidates => Math.Clamp(MaxLexicalCandidates, 1, 500);
    public int EffectiveVectorCandidates => Math.Clamp(MaxVectorCandidates, 1, 500);
    public int EffectiveFusedCandidates => Math.Clamp(MaxFusedCandidates, 1, 500);
    public int EffectiveMaxIndexedChunks => Math.Clamp(MaxIndexedChunks, 1, 2_000_000);
    public double EffectiveMinimumVectorScore => Math.Clamp(MinimumVectorScore, 0.0, 1.0);
}

/// What ONE domain says about semantic retrieval.
///
/// Both properties are NULLABLE on purpose. "Not configured" and "configured to
/// false" have to be different states: the first may inherit the installation
/// default and the second must not, and a `bool` cannot hold that difference —
/// it would make every unmentioned domain look like an explicit opt-out and
/// silently turn semantic retrieval off for Product Help the moment anyone added
/// a `Domains` section for something else.
public sealed class RagDomainSemanticOptions
{
    public bool? SemanticEnabled { get; set; }

    public string? TextEmbeddingProfileKey { get; set; }
}

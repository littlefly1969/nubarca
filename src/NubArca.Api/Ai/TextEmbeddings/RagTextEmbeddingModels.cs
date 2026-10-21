namespace NubArca.Api.Ai.TextEmbeddings;

/// How a text-embedding model must be fed, in code rather than in the database.
///
/// The same discipline as OnnxImageModels: preprocessing is versioned and
/// reviewable, an AiProfile links to an entry through its short ConfigHash, and
/// no weights are committed. Getting one of these fields wrong does not throw —
/// it silently produces a vector in the wrong place in the space — which is
/// exactly why they are written down where a reviewer sees them next to the
/// code that uses them.
public sealed record TextEmbeddingModelConfig(
    /// Catalog key. Also the AiModel key and the profile's ConfigHash. Two
    /// entries may share one ModelSubdir (one set of weights, two output
    /// dimensions): they are different preprocessing, so different keys.
    string Key,

    /// Directory under Ai:Onnx:ModelDir holding ModelFile and TokenizerFile.
    string ModelSubdir,
    string ModelFile,
    string TokenizerFile,
    /// The STORED dimension: the length of every vector this entry produces,
    /// and the profile's Dimension. Equal to the graph's hidden size unless
    /// the model was trained with Matryoshka representation learning, in which
    /// case the pooled vector is cut to its first Dimension components and
    /// THEN L2-normalized (a cut normalized vector is shorter than 1).
    int Dimension,

    /// Prefix the model was TRAINED with for questions, applied by the provider.
    /// Empty for a symmetric model.
    string QueryPrefix,

    /// Prefix the model was trained with for corpus passages.
    string PassagePrefix,

    /// Hard cap on tokens per input. Longer text is truncated, and the final
    /// separator token is preserved so the sequence still ends the way the model
    /// expects.
    int MaxTokens,

    /// How the token axis becomes one vector. A per-model fact, never a
    /// default: masked `mean` (E5), `cls` (token 0, Granite R2), or `last`
    /// (the last ATTENDED token, Qwen3-Embedding; see TextEmbeddingPooling).
    string Pooling,

    /// L2-normalize the pooled vector. Required for cosine to mean what the
    /// model was trained for.
    bool Normalize,

    string InputIdsTensor = "input_ids",
    string AttentionMaskTensor = "attention_mask",
    string TokenTypeIdsTensor = "token_type_ids",
    string OutputTensor = "last_hidden_state",

    /// The graph's hidden size, when it differs from the stored Dimension
    /// (Matryoshka). Null means "the same as Dimension".
    int? NativeDimension = null,

    /// A token that must be the LAST token of every sequence, written as the
    /// token string and resolved through the tokenizer (exactly one id, or the
    /// model is refused). Required for `last` pooling: the model was trained to
    /// summarize the sequence INTO this token, so pooling whichever token
    /// truncation happened to stop on produces a vector that looks normal and
    /// sits in the wrong place. The provider appends it when the tokenizer did
    /// not, and truncation keeps it last.
    string? EosToken = null,

    /// Decoder exports declare position ids; encoder exports usually do not.
    /// Fed only when the graph declares it, exactly like token_type_ids.
    string PositionIdsTensor = "position_ids",

    /// Per-profile cosine floor at which a purely SEMANTIC hit is strong
    /// evidence on its own. Null falls back to `Rag:MinimumVectorScore`, which
    /// was set for E5. Cosine is not calibrated across checkpoints, so a new
    /// model carries either a value measured on the golden set or
    /// RagTextEmbeddingModels.UncalibratedMinimumVectorScore, never a number
    /// that merely looks plausible.
    double? MinimumVectorScore = null,

    /// Provenance for the operator who has to reproduce the weights: the
    /// Hugging Face repository and the exact commit this entry was written
    /// against. Documentation only; nothing downloads anything.
    string? SourceRepository = null,
    string? SourceRevision = null)
{
    /// The hidden size the graph must produce.
    public int GraphDimension => NativeDimension ?? Dimension;
}

public static class TextEmbeddingPooling
{
    public const string Mean = "mean";
    public const string Cls = "cls";

    /// The last token with attention_mask == 1. Correct for left padding (that
    /// is position T-1) and for right padding (the position before the first
    /// pad) without being told which side the tokenizer padded.
    public const string Last = "last";
}

public static class RagTextEmbeddingModels
{
    /// multilingual-e5-small. Chosen for Slice 2 because NubArca's interface is
    /// Italian and much of its documentation is English, so a monolingual model
    /// would have been measured on half the corpus; because 384 dimensions keep
    /// one pgvector table small enough to be uncontroversial; and because it is
    /// an ASYMMETRIC model — it forces the query/passage seam to be real from
    /// the first commit rather than retrofitted when the second model needs it.
    public const string MultilingualE5SmallKey = "multilingual-e5-small";

    /// The profile key an operator sets in `Rag:TextEmbeddingProfileKey`.
    public const string MultilingualE5SmallProfileKey = "rag-text-multilingual-e5-small-v1";

    public const int MultilingualE5SmallDimension = 384;

    /// Qwen3-Embedding-0.6B (Apache-2.0): a 28-layer decoder, 1024-d, trained
    /// with Matryoshka down to 32-d. Last-token pooling over `<|endoftext|>`,
    /// an English instruction on the QUERY side only, raw passages. One set of
    /// weights, two catalog entries: 1024 (native) and 768 (truncated).
    public const string Qwen3Embedding06BModelSubdir = "qwen3-embedding-0.6b";
    public const string Qwen3Embedding06B1024Key = "qwen3-embedding-0.6b-1024";
    public const string Qwen3Embedding06B768Key = "qwen3-embedding-0.6b-768";
    public const string Qwen3Embedding06B1024ProfileKey = "rag-text-qwen3-embedding-0.6b-1024-v1";
    public const string Qwen3Embedding06B768ProfileKey = "rag-text-qwen3-embedding-0.6b-768-v1";

    /// The model's own default query prompt (config_sentence_transformers.json
    /// at the pinned revision). A task-specific instruction may retrieve better;
    /// that is a benchmark result, and a different instruction is a different
    /// catalog entry, because it moves every query vector.
    public const string Qwen3EmbeddingQueryInstruction =
        "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:";

    /// granite-embedding-311m-multilingual-r2 (Apache-2.0; its tokenizer is
    /// derived from Gemma 3's, see the deployment doc): ModernBERT, 768-d,
    /// CLS pooling, no prefixes.
    public const string GraniteEmbedding311MKey = "granite-embedding-311m-multilingual-r2";
    public const string GraniteEmbedding311MProfileKey = "rag-text-granite-embedding-311m-multilingual-r2-v1";

    /// granite-embedding-97m-multilingual-r2 (Apache-2.0): ModernBERT, 384-d,
    /// CLS pooling, no prefixes, no Matryoshka.
    public const string GraniteEmbedding97MKey = "granite-embedding-97m-multilingual-r2";
    public const string GraniteEmbedding97MProfileKey = "rag-text-granite-embedding-97m-multilingual-r2-v1";

    /// PLACEHOLDER, NOT A CALIBRATION. A cosine of 1.0 is reached only by an
    /// identical vector, so a profile carrying this value never lets a purely
    /// semantic hit count as strong evidence on its own: the evidence decision
    /// stays on the lexical gate, which has a golden set behind it, until this
    /// profile's own threshold is measured on the golden set and written into
    /// its catalog entry. Semantic hits still RANK through fusion; they just
    /// cannot open the gate alone.
    public const double UncalibratedMinimumVectorScore = 1.0;

    public static readonly IReadOnlyDictionary<string, TextEmbeddingModelConfig> Catalog =
        new Dictionary<string, TextEmbeddingModelConfig>(StringComparer.Ordinal)
        {
            [MultilingualE5SmallKey] = new(
                Key: MultilingualE5SmallKey,
                ModelSubdir: MultilingualE5SmallKey,
                ModelFile: "model.onnx",
                TokenizerFile: "tokenizer.json",
                Dimension: MultilingualE5SmallDimension,
                // These two strings are not decoration. E5 was trained with them;
                // embedding a passage as a query measurably degrades retrieval,
                // and nothing about the resulting vector looks wrong.
                QueryPrefix: "query: ",
                PassagePrefix: "passage: ",
                MaxTokens: 512,
                Pooling: TextEmbeddingPooling.Mean,
                Normalize: true),

            // ---- benchmark candidates: opt-in, never a default ---------------
            //
            // Nothing selects these. Selection waits for the target-server
            // benchmark (docs/model-deployment/text-embeddings.md).

            [Qwen3Embedding06B1024Key] = Qwen3Embedding06B(Qwen3Embedding06B1024Key, dimension: 1024),
            [Qwen3Embedding06B768Key] = Qwen3Embedding06B(Qwen3Embedding06B768Key, dimension: 768),

            [GraniteEmbedding311MKey] = new(
                Key: GraniteEmbedding311MKey,
                ModelSubdir: GraniteEmbedding311MKey,
                ModelFile: "model.onnx",
                TokenizerFile: "tokenizer.json",
                Dimension: 768,
                // Symmetric: the card and config_sentence_transformers.json at
                // the pinned revision carry empty query and document prompts.
                QueryPrefix: "",
                PassagePrefix: "",
                // A 32k window; 1024 covers the chunker's character budget with
                // room, at a fraction of the full window's attention cost.
                MaxTokens: 1024,
                Pooling: TextEmbeddingPooling.Cls,
                Normalize: true,
                MinimumVectorScore: UncalibratedMinimumVectorScore,
                SourceRepository: "ibm-granite/granite-embedding-311m-multilingual-r2",
                SourceRevision: "44399559930365213510b1ee2eb15ded83374f0e"),

            [GraniteEmbedding97MKey] = new(
                Key: GraniteEmbedding97MKey,
                ModelSubdir: GraniteEmbedding97MKey,
                ModelFile: "model.onnx",
                TokenizerFile: "tokenizer.json",
                Dimension: 384,
                QueryPrefix: "",
                PassagePrefix: "",
                MaxTokens: 1024,
                Pooling: TextEmbeddingPooling.Cls,
                Normalize: true,
                MinimumVectorScore: UncalibratedMinimumVectorScore,
                SourceRepository: "ibm-granite/granite-embedding-97m-multilingual-r2",
                SourceRevision: "835ad14087e140460703cf0fae09f97d469d65c2"),
        };

    /// Profile key → catalog key, stored in AiProfile.ConfigHash at seed time.
    public static readonly IReadOnlyDictionary<string, string> ProfileToCatalogKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MultilingualE5SmallProfileKey] = MultilingualE5SmallKey,
            [Qwen3Embedding06B1024ProfileKey] = Qwen3Embedding06B1024Key,
            [Qwen3Embedding06B768ProfileKey] = Qwen3Embedding06B768Key,
            [GraniteEmbedding311MProfileKey] = GraniteEmbedding311MKey,
            [GraniteEmbedding97MProfileKey] = GraniteEmbedding97MKey,
        };

    private static TextEmbeddingModelConfig Qwen3Embedding06B(string key, int dimension) => new(
        Key: key,
        ModelSubdir: Qwen3Embedding06BModelSubdir,
        ModelFile: "model.onnx",
        TokenizerFile: "tokenizer.json",
        Dimension: dimension,
        // `Instruct: {task}\nQuery:{q}`: no space after `Query:`, and NO
        // instruction on passages. Getting either backwards is the E5 mistake
        // again: nothing about the vector looks wrong.
        QueryPrefix: Qwen3EmbeddingQueryInstruction,
        PassagePrefix: "",
        MaxTokens: 1024,
        Pooling: TextEmbeddingPooling.Last,
        Normalize: true,
        NativeDimension: 1024,
        // The pinned tokenizer.json appends it itself (upstream commit
        // b22da495, "Automatically add EOS via Tokenizer"); the provider
        // verifies rather than trusts, and appends it when it is absent.
        EosToken: "<|endoftext|>",
        MinimumVectorScore: UncalibratedMinimumVectorScore,
        SourceRepository: "Qwen/Qwen3-Embedding-0.6B",
        SourceRevision: "97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3");

    /// The cosine floor for a purely semantic hit under this profile: the
    /// catalog entry's own value when it has one, otherwise the configured
    /// `Rag:MinimumVectorScore`. An unknown or absent profile (the
    /// deterministic dev profile, a lexical-only retrieval) gets the fallback.
    public static double MinimumVectorScoreFor(string? profileKey, double fallback)
        => profileKey is not null
           && ProfileToCatalogKey.TryGetValue(profileKey, out var catalogKey)
           && Catalog.TryGetValue(catalogKey, out var config)
           && config.MinimumVectorScore is { } own
            ? own
            : fallback;

    /// Prefer the profile's ConfigHash, else derive from the profile key.
    /// Returns null when the profile is not a known local text-embedding
    /// profile — which is an availability answer, never a reason to guess.
    public static TextEmbeddingModelConfig? ResolveConfig(string? configHash, string profileKey)
    {
        if (!string.IsNullOrWhiteSpace(configHash) && Catalog.TryGetValue(configHash, out var byHash))
        {
            return byHash;
        }

        return ProfileToCatalogKey.TryGetValue(profileKey, out var catalogKey)
               && Catalog.TryGetValue(catalogKey, out var byProfile)
            ? byProfile
            : null;
    }

    /// The prefix for one input kind. Centralized so a provider cannot apply
    /// the passage prefix to a query by writing the wrong field name.
    public static string PrefixFor(TextEmbeddingModelConfig config, TextEmbeddingInputKind kind)
        => kind == TextEmbeddingInputKind.Query ? config.QueryPrefix : config.PassagePrefix;
}

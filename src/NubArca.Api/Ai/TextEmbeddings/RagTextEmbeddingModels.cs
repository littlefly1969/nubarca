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
    /// Catalog key == the model's directory name under Ai:Onnx:ModelDir.
    string Key,
    string ModelSubdir,
    string ModelFile,
    string TokenizerFile,
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

    /// Mean pooling over the token axis, masked by attention. The alternative
    /// (CLS) is a per-model fact, not a default.
    string Pooling,

    /// L2-normalize the pooled vector. Required for cosine to mean what the
    /// model was trained for.
    bool Normalize,

    string InputIdsTensor = "input_ids",
    string AttentionMaskTensor = "attention_mask",
    string TokenTypeIdsTensor = "token_type_ids",
    string OutputTensor = "last_hidden_state");

public static class TextEmbeddingPooling
{
    public const string Mean = "mean";
    public const string Cls = "cls";
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
        };

    /// Profile key → catalog key, stored in AiProfile.ConfigHash at seed time.
    public static readonly IReadOnlyDictionary<string, string> ProfileToCatalogKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MultilingualE5SmallProfileKey] = MultilingualE5SmallKey,
        };

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

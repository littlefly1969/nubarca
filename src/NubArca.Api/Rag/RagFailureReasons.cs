namespace NubArca.Api.Rag;

/// Sanitized reason codes for the RAG substrate.
///
/// These reach logs, the CLI and — through the Assistant's own failure codes —
/// a browser. None of them carries an exception message, a SQL fragment, a
/// model file path, a connection string or a provider payload. An operator
/// reads their own configuration; a reason code tells them WHICH configuration
/// to read.
public static class RagFailureReasons
{
    public const string DomainUnknown = "rag_domain_unknown";
    public const string DomainNotAllowed = "rag_domain_not_allowed";
    public const string IndexUnavailable = "rag_index_unavailable";
    public const string RevisionMismatch = "rag_revision_mismatch";

    /// One domain's sources came from more than one commit — an interrupted
    /// reindex. Distinct from RevisionMismatch, which is a coherent index that
    /// belongs to a different build: an operator fixes this one by finishing the
    /// reindex, and that one by rebuilding the image.
    public const string MixedRevisionIndex = "rag_mixed_revision_index";
    public const string OwnerRequired = "rag_owner_required";

    /// The corpus is larger than the configured in-memory chunk ceiling.
    ///
    /// Distinct from IndexUnavailable, which means there is nothing to read: a
    /// person whose library outgrew the bound has plenty to read and NubArca is
    /// refusing to hold it all at once. Truncating instead would be worse than
    /// the refusal — it would answer from a silent, arbitrary fraction of
    /// somebody's documents while looking exactly like a complete answer.
    public const string CorpusTooLarge = "rag_corpus_too_large";
    public const string NoStrongEvidence = "rag_no_strong_evidence";

    public const string EmbeddingDisabled = "text_embedding_disabled";
    public const string EmbeddingProfileUnavailable = "text_embedding_profile_unavailable";
    public const string EmbeddingModelUnavailable = "text_embedding_model_unavailable";
    public const string EmbeddingDimensionUnsupported = "text_embedding_dimension_unsupported";
    public const string EmbeddingFailed = "text_embedding_failed";

    /// Local inference did not return within the configured budget. Distinct
    /// from EmbeddingFailed because it is RESUMABLE: the text is indexed, the
    /// embeddings that completed are kept, and re-running the index continues
    /// from where it stopped.
    public const string EmbeddingTimeout = "text_embedding_timeout";
    public const string PgvectorUnavailable = "pgvector_unavailable";

    /// The short suffix used in a `lexical-fallback-…` retrieval mode. Keeps
    /// the mode string readable without inventing a second vocabulary.
    public static string ShortFallback(string reason) => reason switch
    {
        EmbeddingDisabled => "semantic-disabled",
        EmbeddingProfileUnavailable => "profile-unavailable",
        EmbeddingModelUnavailable => "model-unavailable",
        EmbeddingDimensionUnsupported => "dimension-unsupported",
        EmbeddingFailed => "embedding-failed",
        EmbeddingTimeout => "embedding-timeout",
        PgvectorUnavailable => "pgvector-unavailable",
        _ => "semantic-unavailable",
    };
}

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
    public const string OwnerRequired = "rag_owner_required";
    public const string NoStrongEvidence = "rag_no_strong_evidence";

    public const string EmbeddingDisabled = "text_embedding_disabled";
    public const string EmbeddingProfileUnavailable = "text_embedding_profile_unavailable";
    public const string EmbeddingModelUnavailable = "text_embedding_model_unavailable";
    public const string EmbeddingDimensionUnsupported = "text_embedding_dimension_unsupported";
    public const string EmbeddingFailed = "text_embedding_failed";
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
        PgvectorUnavailable => "pgvector-unavailable",
        _ => "semantic-unavailable",
    };
}

namespace NubArca.Api.Rag.Indexing;

/// What one indexing run did. Aggregate counts only — no paths beyond the
/// repository-relative source keys the caller already knows, no chunk text, no
/// vectors.
public sealed record RagIndexOutcome(
    string Domain,
    string Revision,
    int SourcesSeen,
    int SourcesCreated,
    int SourcesUpdated,
    int SourcesUnchanged,
    int SourcesRemoved,
    int ChunksCreated,
    int ChunksUpdated,
    int ChunksRemoved,
    int ChunksUnchanged,
    int EmbeddingsCreated,
    int EmbeddingsRemoved,
    int VectorsIndexed,
    string? EmbeddingProfileKey,
    string? EmbeddingReason)
{
    public static RagIndexOutcome Empty(string domain, string revision)
        => new(domain, revision, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
}

/// What an indexing run is allowed to do.
///
/// `EmbedPassages` is opt-in per run rather than implied by configuration:
/// indexing text is seconds and embedding it is minutes, and an operator
/// re-running the index after a rename should not silently pay for inference
/// they did not ask for.
public sealed record RagIndexRequest(
    string Domain,
    string RootPath,
    string Revision,
    bool EmbedPassages = false,
    int? Limit = null,
    bool DryRun = false);

/// Idempotent indexing of one domain from one snapshot.
///
/// Running it twice against the same snapshot must produce no new rows — not
/// "produce duplicates that a later cleanup removes". The difference matters
/// operationally: a duplicate-then-clean design is a design where an interrupted
/// run leaves the index worse than before it started.
public interface IRagIndexer
{
    Task<RagIndexOutcome> IndexAsync(RagIndexRequest request, CancellationToken cancellationToken = default);
}

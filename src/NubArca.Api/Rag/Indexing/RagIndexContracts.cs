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
    string? EmbeddingReason,

    /// Whether this run saw only PART of the domain's snapshot.
    ///
    /// A partial run is not a statement about what the snapshot contains, so it
    /// is never allowed to conclude that anything left it — see
    /// `ReconciliationPerformed`.
    bool Partial = false,

    /// Whether departed sources were reconciled. False for a partial or dry run.
    bool ReconciliationPerformed = false)
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
///
/// `Limit` makes the run PARTIAL, which is a stronger statement than "slower".
/// A complete run may conclude that a source it did not see has left the
/// snapshot; a partial run has seen nothing beyond its cap and may conclude
/// nothing. `rag index --limit 10` against a complete index used to interpret
/// every source after the tenth as deleted and remove its membership.
public sealed record RagIndexRequest(
    string Domain,
    string RootPath,
    string Revision,
    bool EmbedPassages = false,
    int? Limit = null,
    bool DryRun = false)
{
    /// A run that cannot have seen the whole snapshot. Derived from the REQUEST
    /// rather than from how many sources happened to be enumerated: inferring
    /// completeness from a count would make an empty repository look like a
    /// complete run that found nothing, and delete the entire index.
    public bool IsPartial => Limit is not null;

    public bool MayReconcile => !IsPartial && !DryRun;
}

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

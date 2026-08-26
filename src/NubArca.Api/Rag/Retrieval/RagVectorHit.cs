namespace NubArca.Api.Rag.Retrieval;

/// One semantic candidate, already resolved to the chunk it names.
public sealed record RagVectorHit(RagIndexedChunk Chunk, double Score, int Rank);

/// What the semantic path produced, or why it produced nothing.
///
/// `Reason` being set is not an error state — semantic retrieval is optional and
/// its absence is a supported configuration. It is reported so a degraded run
/// can be seen as degraded rather than as a corpus that got worse.
public sealed record RagVectorSearchOutcome(
    IReadOnlyList<RagVectorHit> Hits,
    string? ProfileKey,
    string? Reason)
{
    public static RagVectorSearchOutcome Unavailable(string reason)
        => new(Array.Empty<RagVectorHit>(), null, reason);

    public bool IsAvailable => Reason is null;
}

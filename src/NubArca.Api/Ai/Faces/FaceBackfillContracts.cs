namespace NubArca.Api.Ai.Faces;

// Shared options/result for the face detection + embedding backfills. Flags +
// aggregate counts only — never storage keys, paths, vectors, or tokens.
public sealed record FaceBackfillOptions
{
    public int? Limit { get; init; }
    public bool DryRun { get; init; }

    // Optional single-blob scope used by the post-ingestion fast path. Null
    // preserves the existing global, keyset-paged backfill behaviour.
    public Guid? TargetBlobObjectId { get; init; }
}

public sealed record FaceBackfillResult(
    int Processed,      // blobs processed this slice
    int Produced,       // detection: faces detected; embedding: embeddings written
    int Skipped,        // detection: zero-face completions; embedding: no-op skips
    int Failed,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null,
    int ProcessedTotal = 0,
    int ProducedTotal = 0,
    int SkippedTotal = 0,
    int FailedTotal = 0,
    // Best-effort pgvector index outcomes for THIS slice (embedding backfill only).
    int VectorIndexed = 0,
    int VectorDeferred = 0)
{
    public static FaceBackfillResult Dry() => new(0, 0, 0, 0, DryRun: true);
}

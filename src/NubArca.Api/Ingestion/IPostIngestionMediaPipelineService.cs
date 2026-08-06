namespace NubArca.Api.Ingestion;

// Centralized post-ingestion media pipeline. Called AFTER a normal-library
// FileItem is successfully created (direct UI upload today; reusable by other
// ingestion paths). It only ENQUEUES bounded, idempotent background work — it
// never performs decode/encode/inference inline, so the HTTP upload stays
// responsive. Private Vault content and non-media files enqueue nothing.
public interface IPostIngestionMediaPipelineService
{
    // Schedule the media pipeline for one freshly-ingested file. Best-effort and
    // safe: any failure is swallowed/logged by the caller and never breaks the
    // upload. Returns the enqueue outcome (for tests/diagnostics — counts only).
    Task<PostIngestionEnqueueResult> OnFileIngestedAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);

    // Same bounded pipeline with latency priorities for anonymous Party uploads:
    // preview first, then targeted face indexing, while SigLIP remains compute.
    Task<PostIngestionEnqueueResult> OnPartyFileIngestedAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);
}

// What was scheduled (aggregate flags only — no ids/keys/paths surfaced).
public sealed record PostIngestionEnqueueResult(
    bool MetadataScheduled,
    bool DerivativesScheduled,
    bool AiEmbeddingScheduled,
    string Outcome, // "image" | "video" | "non-media" | "skipped-vault-or-missing"
    bool FaceIndexScheduled = false)
{
    public static readonly PostIngestionEnqueueResult Skipped =
        new(false, false, false, "skipped-vault-or-missing", false);
}

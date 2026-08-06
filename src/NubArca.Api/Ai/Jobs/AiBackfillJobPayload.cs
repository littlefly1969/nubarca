namespace NubArca.Api.Ai.Jobs;

// Shared payload for the AI backfill jobs. Flags only — never storage keys,
// paths, raw metadata, or tokens. `ProfileKey` is a stable, human-meaningful
// profile key (not a GUID); when null the job resolves the default profile for
// its capability. `BlobObjectId` is an optional server-side single-target scope
// (post-ingest): when set, the photo-embedding backfill indexes only that blob
// (bounded point-lookup, no library scan). PayloadJson is never surfaced.
public sealed record AiBackfillJobPayload(
    string? ProfileKey = null,
    int? Limit = null,
    bool DryRun = false,
    Guid? BlobObjectId = null);

namespace NubArca.Api.Jobs;

// Job payloads are intentionally tiny and explicit: operation FLAGS only.
// They never carry storage keys, physical paths, raw metadata, tokens, or user
// strings. Internal row ids (import run, organizer run, and the optional
// post-ingest single-target scope below) are allowed ONLY as server-side
// references — PayloadJson is never surfaced through any API/UI/log.

public sealed record MetadataBackfillJobPayload(
    int? Limit = null,
    bool FailedOnly = false,
    bool DryRun = false,
    // Post-ingest scope: when set, restrict extraction to this single blob
    // (bounded point-lookup, no library scan). Null = the global backfill.
    Guid? BlobObjectId = null);

public sealed record VideoMetadataBackfillJobPayload(
    int? Limit = null,
    bool FailedOnly = false,
    bool DryRun = false,
    // Post-ingest scope: when set, restrict probing to this single blob
    // (bounded point-lookup, no library scan). Null = the global backfill.
    Guid? BlobObjectId = null);

public sealed record MediaDerivativesBackfillJobPayload(
    int? Limit = null,
    bool MissingOnly = true,
    bool FailedOnly = false,
    bool DryRun = false,
    // Slice 99: re-attempt derivatives blocked by a prior failure diagnostic
    // (bypasses retry gating). FailedOnly is treated as an alias of this.
    bool RetryFailed = false,
    // Post-ingest scope: when set, restrict derivatives to this single FileItem
    // (thumbnails are per-FileItem). Null = the global backfill.
    Guid? FileItemId = null);

// Forced rebuild for the unified gallery contract. Flags only: no ids, paths,
// storage keys, hashes, filenames, metadata or credentials.
public sealed record GalleryDerivativesRegenerationJobPayload(
    string[]? Sizes = null,
    bool Force = false,
    bool DryRun = false,
    int? Limit = null,
    int? BatchSize = null);

public sealed record MediumPreviewRegenerationJobPayload(
    int? Limit = null);

// Operator poster regeneration (replace synthetic placeholder posters with real
// frames once an ffmpeg provider is enabled). Force=false → only PosterSource
// == synthetic rows (the safe default); Force=true → every video poster.
public sealed record PosterRegenerationJobPayload(
    bool Force = false,
    int? Limit = null,
    bool DryRun = false);

// Admin console: bulk HLS pre-warm. Walks the eligible video blobs with
// keyset paging and generates the missing ladders (RetryFailed re-attempts
// recorded failures; Force regenerates ready ones too). Flags only.
public sealed record VideoHlsBackfillJobPayload(
    int? Limit = null,
    bool RetryFailed = false,
    bool Force = false,
    bool DryRun = false);

// Video-hls slice 1: generate the HLS playback ladder for ONE source blob
// (bounded point work — the lazy playback path enqueues one of these per
// cache-miss; the CLI enqueues them for manual pre-warming). The blob id is an
// internal row reference only, per the payload rules above. Force re-runs a
// failed/ready row and replaces the published ladder.
public sealed record VideoHlsGenerateJobPayload(
    Guid BlobObjectId,
    bool Force = false);

public sealed record StorageReconcileJobPayload(
    int? Limit = null,
    bool DeleteOrphans = false,
    bool DryRun = true);

// Slice 81: the import run row (admin_import_runs) holds all parameters and
// progress; the payload references it by id only — no paths, keys, or tokens.
public sealed record AdminImportJobPayload(Guid ImportRunId);

// Phase 2: the organizer run row (photo_organizer_runs) holds all options +
// progress; the payload references it by id only.
public sealed record PhotoOrganizerJobPayload(Guid RunId);

// Plates ALPR: the plate_analysis_jobs row holds the owner + target PlateImage +
// status; the payload references it by id only (no image bytes, blob id, or path).
public sealed record PlateAnalysisJobPayload(Guid AnalysisJobId);

// Aesthetics Lab: the aesthetic_analysis_runs row holds the owner + target lab
// item + requested capabilities + status; the payload references it by RUN id
// only — never image bytes, blob id, SHA, path, person names, prompts, or model
// output.
public sealed record AestheticAnalysisJobPayload(Guid RunId);

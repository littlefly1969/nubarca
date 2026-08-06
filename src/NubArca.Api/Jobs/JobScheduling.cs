namespace NubArca.Api.Jobs;

// Cooperative scheduler model (slice "scheduler v2").
//
// `BackgroundJob.Priority` stays the single ordering source of truth — lower
// number = higher priority, exactly as before. This type only gives those raw
// numbers *names* (priority classes) and a per-job-type default, plus a pure
// function to label a numeric priority for the admin dashboard. No class is
// stored on the row; the label is always derived from `Priority`, so a custom
// priority still maps to the nearest class and old rows need no migration.
//
// Future extension seam (NOT implemented here — documented only): a parallel
// `ResourceClass` ("io-db", "cpu-io", "cpu", "cpu-gpu", "external", "network")
// would let the scheduler apply per-resource concurrency limits and route
// CPU/GPU-heavy AI jobs (tagging, embeddings, OCR, transcription, semantic
// indexing) to dedicated worker pools. The registry below is where a
// `type → resource class` mapping would be added; nothing in the core
// selection / slicing logic assumes all jobs share one resource.
public static class JobScheduling
{
    // ---- numeric priority bands (lower = higher priority) -----------------
    // Foreground: user-facing work that makes files visible / completes an
    // explicit user action (admin import, staging-to-library import).
    public const int Foreground = 10;
    // Latency-sensitive post-upload work. Party previews come first, then face
    // indexing; ordinary upload previews remain ahead of maintenance/backfills.
    public const int PartyPreview = 20;
    public const int PartyFaces = 30;
    public const int PostIngestPreview = 40;
    public const int PostIngestFaces = 60;
    // Normal: useful user-triggered work that is not latency-critical.
    public const int Normal = 50;
    // Maintenance: media derivatives, metadata extraction, storage verify,
    // index maintenance — must yield to foreground but must not starve.
    public const int Maintenance = 100;
    // Cleanup: janitor / sweepers / reconciliation (low, but never forever).
    public const int Cleanup = 150;
    // Compute: future AI/embedding/OCR — lowest by default, resource-limited.
    public const int Compute = 200;

    // Default priority for a job type. Unknown / unmapped types fall to Normal
    // so a new type is never accidentally treated as foreground.
    public static int DefaultPriorityFor(string? jobType) => jobType switch
    {
        JobTypes.AdminImport => Foreground,
        // User-initiated bulk organize: prompt, but yields to foreground import.
        JobTypes.PhotoOrganizerDateTaken => Normal,
        // User-initiated export-snapshot build: same band as organize.
        JobTypes.PhotoExportBuild => Normal,
        // Video-hls: a user pressed play on an unprepared video — latency
        // matters, but the transcode is heavy, so it must still yield to
        // foreground imports. Normal band (explicit, not just the fallback).
        JobTypes.MediaVideoHlsGenerate => Normal,
        // Bulk HLS pre-warm is maintenance work: many long transcodes that
        // must never crowd out user-facing jobs.
        JobTypes.MediaVideoHlsBackfill => Maintenance,
        JobTypes.MediaDerivativesBackfill => Maintenance,
        JobTypes.MediaGalleryDerivativesRegenerate => Maintenance,
        JobTypes.MediumPreviewRegenerate => Maintenance,
        JobTypes.MediaPostersRegenerate => Maintenance,
        JobTypes.MetadataEmbeddedBackfill => Maintenance,
        JobTypes.MetadataVideoBackfill => Maintenance,
        JobTypes.StorageReconcile => Maintenance,
        // AI backfills are the lowest band: they must yield to everything and
        // never starve foreground imports.
        JobTypes.AiPhotosEmbeddingsBackfill => Compute,
        JobTypes.AiDocumentsExtractBackfill => Compute,
        JobTypes.AiDocumentsEmbeddingsBackfill => Compute,
        JobTypes.AiFacesDetectBackfill => Compute,
        JobTypes.AiFacesEmbeddingsBackfill => Compute,
        JobTypes.AiFacesClusterBackfill => Compute,
        JobTypes.AiTagsGenerateBackfill => Compute,
        // Video segmentation decodes whole files: the heaviest AI backfill, and
        // the one that must yield most readily.
        JobTypes.AiVideosSegmentsBackfill => Compute,
        // Video sample embedding is inference work — same lowest band.
        JobTypes.AiVideosEmbeddingsBackfill => Compute,
        // Video face analysis extracts hundreds of frames and runs two models
        // over each — same lowest band, and it must yield just as readily.
        JobTypes.AiVideosFacesBackfill => Compute,
        // Plate ALPR is CPU/ML work — lowest band, yields to everything.
        JobTypes.PlatesAnalyze => Compute,
        // HumanAesExpert analysis is CPU/ML work — lowest band, yields to all.
        JobTypes.AestheticsAnalyze => Compute,
        _ => Normal,
    };

    // Human-readable class label for the admin dashboard — derived purely from
    // the numeric priority (no stored column). Bands are inclusive upper bounds
    // so any custom value maps to the nearest class.
    public static string ClassForPriority(int priority) => priority switch
    {
        <= Foreground => JobPriorityClasses.Foreground,
        <= Normal => JobPriorityClasses.Normal,
        <= Maintenance => JobPriorityClasses.Maintenance,
        <= Cleanup => JobPriorityClasses.Cleanup,
        _ => JobPriorityClasses.Compute,
    };

    // Foreground jobs self-pace (admin/staging import already slice themselves
    // via their own MaxRunMinutes) and are never preempted by the cooperative
    // scheduler — they get an unbounded slice budget and only stop for
    // cancellation. Everything below foreground gets a maintenance slice budget.
    public static bool IsForeground(int priority) => priority <= Foreground;
}

public static class JobPriorityClasses
{
    public const string Foreground = "foreground";
    public const string Normal = "normal";
    public const string Maintenance = "maintenance";
    public const string Cleanup = "cleanup";
    public const string Compute = "compute";
}

// Why a slice yielded — surfaced on the admin dashboard (counts/phase only,
// never sensitive data). Persisted to BackgroundJob.YieldReason on continuation.
public static class JobYieldReasons
{
    public const string SliceBudget = "slice-budget";        // wall-clock / item budget reached
    public const string HigherPriority = "higher-priority";  // a higher-priority job was waiting
    public const string MaxSlices = "max-slices";            // safety cap hit; remaining deferred
}

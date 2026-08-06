namespace NubArca.Api.Jobs;

// Job lifecycle (slice 70). Transitions: queued → running → succeeded | failed
// | cancelled. A transient failure goes running → queued (retry) until
// MaxAttempts, then running → failed (terminal). Cancellation is a flag on a
// queued/running job; the job lands in `cancelled` (terminal). succeeded /
// failed / cancelled are terminal.
public static class JobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) => status is
        Queued or Running or Succeeded or Failed or Cancelled;

    public static bool IsTerminal(string? status) => status is
        Succeeded or Failed or Cancelled;
}

public static class JobTypes
{
    public const string MetadataEmbeddedBackfill = "metadata.embedded.backfill";
    public const string MetadataVideoBackfill = "metadata.video.backfill";
    public const string MediaDerivativesBackfill = "media.derivatives.backfill";
    public const string MediaGalleryDerivativesRegenerate = "media.gallery.derivatives.regenerate";
    public const string MediumPreviewRegenerate = "media.previews.medium.regenerate";
    public const string MediaPostersRegenerate = "media.posters.regenerate";
    public const string MediaVideoHlsGenerate = "media.video.hls.generate";
    public const string MediaVideoHlsBackfill = "media.video.hls.backfill";
    public const string StorageReconcile = "storage.reconcile";
    // Slice 81: admin server-side directory import. The payload carries only
    // the AdminImportRun id (a Guid) — never paths or storage keys.
    public const string AdminImport = "admin.import";

    // Phase 2: owner-scoped "Organize photos by date" run. The payload carries
    // only the PhotoOrganizerRun id (a Guid). Sliceable + cancellable; runs in
    // the Normal band so a foreground import can preempt it.
    public const string PhotoOrganizerDateTaken = "photo.organizer.datetaken";

    // Owner-scoped photo-archive export: builds a read-only snapshot/manifest of
    // exportable photos. The payload carries only the PhotoExportSession id.
    // Sliceable + checkpointed; Normal band (yields to foreground import).
    public const string PhotoExportBuild = "photo.export.build";

    // AI substrate (Phase 0C): skeleton backfill jobs. They are real, registered,
    // enqueueable handlers in the Compute band, but in Phase 0C they NO-OP — no
    // inference, no embeddings/chunks/detections/annotations, no per-blob status
    // rows. Payloads carry only flags + an optional profile stable KEY (never a
    // GUID/path/key). See AiBackfillJobPayload.
    public const string AiPhotosEmbeddingsBackfill = "ai.photos.embeddings.backfill";
    public const string AiDocumentsExtractBackfill = "ai.documents.extract.backfill";
    public const string AiDocumentsEmbeddingsBackfill = "ai.documents.embeddings.backfill";
    public const string AiFacesDetectBackfill = "ai.faces.detect.backfill";
    public const string AiFacesEmbeddingsBackfill = "ai.faces.embeddings.backfill";
    public const string AiFacesClusterBackfill = "ai.faces.cluster.backfill";
    public const string AiTagsGenerateBackfill = "ai.tags.generate.backfill";

    // VSEM-01: canonical video temporal manifest (scene segments + representative
    // sample timestamps). Blob-level and owner-free; the payload carries only an
    // optional blob id, an optional segmentation version, and flags. Scheduled
    // only AFTER authoritative video metadata has been persisted.
    public const string AiVideosSegmentsBackfill = "ai.videos.segments.backfill";

    // VSEM-02: canonical SigLIP2 embeddings of the temporal samples. Scheduled
    // only AFTER a manifest has COMPLETED (never from initial upload). Payload
    // carries only flags, an optional profile stable key, an optional blob id
    // and an optional segmentation version.
    public const string AiVideosEmbeddingsBackfill = "ai.videos.embeddings.backfill";

    // VFACE-01: canonical video face TRACKS (bounded temporal sampling, face
    // detection + recognition, deterministic association). Scheduled only AFTER a
    // manifest has COMPLETED, and independent of VSEM-02 visual embeddings. The
    // payload carries only flags, optional profile stable keys, an optional blob
    // id and optional segmentation/analysis versions. Evidence only — no person
    // identity is produced or representable.
    public const string AiVideosFacesBackfill = "ai.videos.faces.backfill";

    // Plates (Targhe) ALPR: analyze ONE owner-private PlateImage (detect plates +
    // OCR). One-shot, Compute band. Separate from the AI face substrate — its own
    // model profile/config (Plates:Alpr), no People/Face identity. The payload
    // carries only the PlateAnalysisJob row id.
    public const string PlatesAnalyze = "plates.analyze";

    // Aesthetics Lab: analyze ONE owner-private AestheticLabItem with the local
    // HumanAesExpert sidecar. One image = one run = one job (a batch of N creates
    // N independent jobs, never a monolith). Compute band. Opt-in/experimental,
    // disabled by default. The payload carries only the AestheticAnalysisRun id.
    public const string AestheticsAnalyze = "ai.aesthetics.human-aesexpert.analyze";
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ingestion;

// Bridges direct upload (and other ingestion paths) into the SAME background
// media pipeline that bulk/staged/import already use — but bounded to the single
// ingested file so no full-library scan runs per upload.
//
// It enqueues the EXISTING backfill job types with a single-target scope and
// idempotency keys chosen by the natural grain of each artifact:
//   * metadata    — blob-level     (postingest:metadata:{blob})          dedup uploads of
//   * AI embed     — blob+profile   (postingest:ai:{blob}:{profile})       the same blob
//                                                                          coalesce to one job.
//   * derivatives — per-FileItem   (postingest:derivatives:{file})       thumbnails are per
//                                                                          FileItem → per-file.
//
// Precision: before enqueuing, each artifact's COMPLETION is checked so an
// already-processed blob (a dedup upload) does not re-enqueue blob-level work
// (metadata / AI). Derivatives are still scheduled for a genuinely new FileItem
// that lacks them (e.g. the medium preview). Idempotency keys additionally dedup
// concurrent duplicates, and the scoped candidate queries re-check normal-library
// eligibility at run time (a file moved into the Private Vault → no work).
public sealed class PostIngestionMediaPipelineService : IPostIngestionMediaPipelineService
{
    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IOptions<AiOptions> _ai;
    private readonly IOptions<MediaOptions> _media;
    private readonly PhotoEmbeddingProfileService _profiles;
    private readonly IAiProfileRegistry _registry;
    private readonly ILogger<PostIngestionMediaPipelineService> _logger;

    public PostIngestionMediaPipelineService(
        AppDbContext db,
        IJobQueue jobs,
        IOptions<AiOptions> ai,
        IOptions<MediaOptions> media,
        PhotoEmbeddingProfileService profiles,
        IAiProfileRegistry registry,
        ILogger<PostIngestionMediaPipelineService> logger)
    {
        _db = db;
        _jobs = jobs;
        _ai = ai;
        _media = media;
        _profiles = profiles;
        _registry = registry;
        _logger = logger;
    }

    public Task<PostIngestionEnqueueResult> OnFileIngestedAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
        => ScheduleAsync(ownerUserId, fileItemId, partyFastLane: false, cancellationToken);

    public Task<PostIngestionEnqueueResult> OnPartyFileIngestedAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
        => ScheduleAsync(ownerUserId, fileItemId, partyFastLane: true, cancellationToken);

    private async Task<PostIngestionEnqueueResult> ScheduleAsync(
        Guid ownerUserId, Guid fileItemId, bool partyFastLane, CancellationToken cancellationToken)
    {
        // Owner-scoped + vault-safe by construction: the default query filter
        // excludes Private Vault content, so a vaulted (or foreign/deleted) file
        // resolves to null here and schedules nothing.
        var file = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new { f.BlobObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null)
        {
            return PostIngestionEnqueueResult.Skipped;
        }

        var blobId = file.BlobObjectId;
        var mediaCategory = await _db.BlobMetadata.AsNoTracking()
            .Where(m => m.BlobObjectId == blobId)
            .Select(m => m.MediaCategory)
            .FirstOrDefaultAsync(cancellationToken);

        var isImage = mediaCategory == MediaCategories.Image;
        var isVideo = mediaCategory == MediaCategories.Video;
        if (!isImage && !isVideo)
        {
            return new PostIngestionEnqueueResult(false, false, false, "non-media", false);
        }

        var metadataScheduled = false;
        var derivativesScheduled = false;
        var aiScheduled = false;
        var faceScheduled = false;
        var previewPriority = partyFastLane
            ? JobScheduling.PartyPreview
            : JobScheduling.PostIngestPreview;
        var facePriority = partyFastLane
            ? JobScheduling.PartyFaces
            : JobScheduling.PostIngestFaces;

        // ── Derivatives (per-FileItem) ───────────────────────────────────────
        // Image → small + medium; video → poster + six-frame preview strip. Only schedule when this
        // FileItem is actually missing a derivative (a new dedup copy still needs
        // its own medium even when the blob is otherwise fully processed).
        var derivativesNeeded = isImage
            ? !(await HasThumbAsync(fileItemId, ThumbnailSizes.Small, cancellationToken)
                && await HasThumbAsync(fileItemId, ThumbnailSizes.Medium, cancellationToken))
            : !(await HasThumbAsync(fileItemId, ThumbnailSizes.Poster, cancellationToken)
                && await HasThumbAsync(fileItemId, ThumbnailSizes.VideoPreviewStrip, cancellationToken));
        if (derivativesNeeded)
        {
            derivativesScheduled = await TryEnqueueAsync(
                JobTypes.MediaDerivativesBackfill,
                new MediaDerivativesBackfillJobPayload(FileItemId: fileItemId),
                $"postingest:derivatives:{fileItemId:N}",
                previewPriority,
                cancellationToken);
        }

        // ── Video metadata probe (blob-level) ────────────────────────────────
        // Only when a provider is configured (ffprobe) and this blob still needs
        // probing. Mirrors the image embedded-metadata enqueue below.
        if (isVideo && _media.Value.VideoMetadataProbeEnabled)
        {
            var videoExtraction = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.BlobObjectId == blobId)
                .Select(m => (string?)m.VideoExtractionStatus)
                .FirstOrDefaultAsync(cancellationToken);
            var videoMetaNeeded = videoExtraction is null
                || videoExtraction == MetadataStatuses.Pending
                || videoExtraction == MetadataStatuses.Failed;
            if (videoMetaNeeded)
            {
                metadataScheduled = await TryEnqueueAsync(
                    JobTypes.MetadataVideoBackfill,
                    new VideoMetadataBackfillJobPayload(BlobObjectId: blobId),
                    $"postingest:videometa:{blobId:N}",
                    priority: null,
                    cancellationToken: cancellationToken);
            }
        }

        if (isImage)
        {
            // ── Embedded metadata (blob-level) ───────────────────────────────
            // Only when not already complete. Direct upload extracts inline
            // (→ Succeeded/Skipped, so no job), but a dedup against a blob left
            // `pending`/`failed` by a deferred import DOES get scheduled.
            var extraction = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.BlobObjectId == blobId)
                .Select(m => (string?)m.ExtractionStatus)
                .FirstOrDefaultAsync(cancellationToken);
            var metadataNeeded = extraction is null
                || extraction == MetadataStatuses.Pending
                || extraction == MetadataStatuses.Failed;
            if (metadataNeeded)
            {
                metadataScheduled = await TryEnqueueAsync(
                    JobTypes.MetadataEmbeddedBackfill,
                    new MetadataBackfillJobPayload(BlobObjectId: blobId),
                    $"postingest:metadata:{blobId:N}",
                    priority: null,
                    cancellationToken: cancellationToken);
            }

            // ── Targeted face detection -> embeddings (blob+profile) ────────
            // Normal uploads and Party uploads use the same idempotent pipeline;
            // Party only receives a higher scheduler priority. Detection chains
            // embeddings after its transaction, avoiding the old race/global scan.
            var ai = _ai.Value;
            if (ai.Enabled && (ai.FaceDetectionEnabled || ai.FaceEmbeddingsEnabled))
            {
                var faceProfile = !string.IsNullOrWhiteSpace(ai.FaceProfileKey)
                    ? await _registry.GetProfileByKeyAsync(ai.FaceProfileKey!, cancellationToken)
                    : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);
                if (faceProfile is { Enabled: true }
                    && faceProfile.Capability == AiCapabilities.FaceEmbedding)
                {
                    var detected = await _db.BlobAiArtifactStatuses.AsNoTracking().AnyAsync(s =>
                        s.BlobObjectId == blobId && s.ProfileId == faceProfile.Id
                        && s.Capability == AiCapabilities.FaceDetection
                        && s.Status == AiArtifactStatuses.Completed, cancellationToken);

                    if (!detected && ai.FaceDetectionEnabled)
                    {
                        faceScheduled = await TryEnqueueAsync(
                            JobTypes.AiFacesDetectBackfill,
                            new AiBackfillJobPayload(ProfileKey: faceProfile.Key, BlobObjectId: blobId),
                            $"postingest:faces:detect:{blobId:N}:{faceProfile.Key}",
                            facePriority,
                            cancellationToken);
                    }
                    else if (detected && ai.FaceEmbeddingsEnabled)
                    {
                        var hasPendingFaces = await _db.FaceDetections.AsNoTracking().AnyAsync(d =>
                            d.BlobObjectId == blobId && d.ProfileId == faceProfile.Id
                            && d.LandmarksJson != null
                            && !_db.FaceEmbeddings.Any(e => e.FaceDetectionId == d.Id
                                && e.ProfileId == faceProfile.Id
                                && (e.EmbeddingStatus == AiArtifactStatuses.Completed
                                    || e.EmbeddingStatus == AiArtifactStatuses.Skipped)), cancellationToken);
                        if (hasPendingFaces)
                        {
                            faceScheduled = await TryEnqueueAsync(
                                JobTypes.AiFacesEmbeddingsBackfill,
                                new AiBackfillJobPayload(ProfileKey: faceProfile.Key, BlobObjectId: blobId),
                                $"postingest:faces:embed:{blobId:N}:{faceProfile.Key}",
                                facePriority,
                                cancellationToken);
                        }
                    }
                }
            }

            // ── AI photo embedding + vector (blob+profile) ───────────────────
            // Only when the capability is enabled (mirrors admin-import gating),
            // an active profile resolves as usable, AND no embedding exists yet.
            // The handler still does the authoritative backend-readiness check
            // and no-ops safely (aggregate diagnostic) if the provider is down.
            if (ai.Enabled && ai.ImageEmbeddingsEnabled)
            {
                var resolution = await _profiles.ResolveActiveProfileAsync(null, cancellationToken);
                if (resolution.Usable && resolution.Profile is { } profile)
                {
                    var alreadyEmbedded = await _db.BlobEmbeddings.AsNoTracking()
                        .AnyAsync(e => e.BlobObjectId == blobId && e.ProfileId == profile.Id, cancellationToken);
                    if (!alreadyEmbedded)
                    {
                        aiScheduled = await TryEnqueueAsync(
                            JobTypes.AiPhotosEmbeddingsBackfill,
                            new AiBackfillJobPayload(ProfileKey: profile.Key, BlobObjectId: blobId),
                            $"postingest:ai:{blobId:N}:{profile.Key}",
                            priority: null,
                            cancellationToken: cancellationToken);
                    }
                }
            }
        }

        return new PostIngestionEnqueueResult(
            metadataScheduled, derivativesScheduled, aiScheduled, isImage ? "image" : "video", faceScheduled);
    }

    private Task<bool> HasThumbAsync(Guid fileItemId, string size, CancellationToken ct)
        => _db.FileThumbnails.AsNoTracking()
            .AnyAsync(t => t.FileItemId == fileItemId && t.Size == size, ct);

    private async Task<bool> TryEnqueueAsync<TPayload>(
        string jobType, TPayload payload, string idempotencyKey, int? priority,
        CancellationToken cancellationToken)
    {
        try
        {
            await _jobs.EnqueueAsync(jobType, payload, priority: priority, idempotencyKey: idempotencyKey,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a scheduling hiccup must never break ingestion. Safe
            // aggregate log only (job type — never ids/keys/paths/secrets).
            _logger.LogWarning(ex, "Post-ingest enqueue failed for job type {JobType}.", jobType);
            return false;
        }
    }
}

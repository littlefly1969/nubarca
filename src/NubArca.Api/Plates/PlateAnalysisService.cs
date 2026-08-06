using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Plates.Alpr;
using NubArca.Api.Storage;

namespace NubArca.Api.Plates;

public sealed class PlateAnalysisService : IPlateAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IPlateAnalysisPipeline _pipeline;
    private readonly IBlobService _blobs;
    private readonly TimeProvider _clock;
    private readonly PlatesAlprOptions _options;
    private readonly ILogger<PlateAnalysisService> _logger;

    public PlateAnalysisService(
        AppDbContext db,
        IJobQueue jobs,
        IPlateAnalysisPipeline pipeline,
        IBlobService blobs,
        TimeProvider clock,
        ILogger<PlateAnalysisService> logger,
        IOptions<PlatesAlprOptions>? options = null)
    {
        _db = db;
        _jobs = jobs;
        _pipeline = pipeline;
        _blobs = blobs;
        _clock = clock;
        _logger = logger;
        _options = options?.Value ?? new PlatesAlprOptions();
    }

    public async Task<PlateAnalysisJobSummary?> RequestAnalysisAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default)
    {
        var image = await _db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plateImageId && p.OwnerUserId == ownerUserId)
            .Select(p => new { p.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (image is null)
        {
            return null;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Idempotent: reuse an already queued/running analysis for this image.
            var job = await _db.PlateAnalysisJobs
                .Where(j => j.PlateImageId == plateImageId && j.OwnerUserId == ownerUserId
                    && (j.Status == PlateAnalysisJobStatuses.Queued || j.Status == PlateAnalysisJobStatuses.Running))
                .OrderByDescending(j => j.RequestedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var imageStatus = image.Status;
            if (job is null)
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                job = new PlateAnalysisJob
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    PlateImageId = plateImageId,
                    Status = PlateAnalysisJobStatuses.Queued,
                    RequestedAt = now,
                    ProfileKey = _options.ProfileKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.PlateAnalysisJobs.Add(job);
                await _db.PlateImages
                    .Where(p => p.Id == plateImageId && p.OwnerUserId == ownerUserId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.Status, PlateImageStatuses.AnalysisPending)
                        .SetProperty(p => p.UpdatedAt, now), cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                imageStatus = PlateImageStatuses.AnalysisPending;
            }

            // ALWAYS (re)enqueue — the per-image idempotency key dedups a still
            // live background job, but re-drives a queued domain job whose prior
            // background job was cancelled/lost (so it can't be orphaned). Runs
            // inside the transaction so the domain job + background job commit
            // atomically.
            await _jobs.EnqueueAsync(
                JobTypes.PlatesAnalyze,
                new PlateAnalysisJobPayload(job.Id),
                idempotencyKey: $"plates:analyze:{plateImageId:N}",
                cancellationToken: cancellationToken);

            await tx.CommitAsync(cancellationToken);
            var (count, lastAt) = await CountAndLastAsync(ownerUserId, plateImageId, cancellationToken);
            return PlateAnalysisMapping.ToJobSummary(job, imageStatus, count, lastAt);
        });
    }

    public async Task<PlateAnalysisSummary?> GetLatestSummaryAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default)
    {
        var status = await _db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plateImageId && p.OwnerUserId == ownerUserId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return null;
        }
        return await BuildSummaryAsync(ownerUserId, plateImageId, status, cancellationToken);
    }

    public async Task<(PlateAnalysisSummary Summary, IReadOnlyList<PlateDetectionDto> Detections)> LoadForDetailAsync(
        Guid ownerUserId, Guid plateImageId, string plateImageStatus, CancellationToken cancellationToken = default)
    {
        var detections = await _db.PlateDetections.AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId && d.PlateImageId == plateImageId)
            .OrderByDescending(d => d.CombinedConfidence)
            .ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

        var summary = await BuildSummaryAsync(ownerUserId, plateImageId, plateImageStatus, cancellationToken);
        IReadOnlyList<PlateDetectionDto> dtos = detections.Select(PlateAnalysisMapping.ToDto).ToList();
        return (summary, dtos);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountDetectionsForImagesAsync(
        Guid ownerUserId, IReadOnlyList<Guid> plateImageIds, CancellationToken cancellationToken = default)
    {
        if (plateImageIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }
        var counts = await _db.PlateDetections.AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId && plateImageIds.Contains(d.PlateImageId))
            .GroupBy(d => d.PlateImageId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(x => x.Key, x => x.Count);
    }

    public async Task AnalyzeAsync(
        Guid analysisJobId, JobContext context, CancellationToken cancellationToken = default)
    {
        var job = await _db.PlateAnalysisJobs
            .FirstOrDefaultAsync(j => j.Id == analysisJobId, cancellationToken);
        if (job is null)
        {
            // The domain job (and its image) is gone — a deleted PlateImage
            // cascades its jobs away. Nothing to do; succeed quietly.
            return;
        }
        if (job.Status is PlateAnalysisJobStatuses.Completed
            or PlateAnalysisJobStatuses.Failed
            or PlateAnalysisJobStatuses.Canceled)
        {
            return; // already processed
        }

        var image = await _db.PlateImages
            .FirstOrDefaultAsync(p => p.Id == job.PlateImageId && p.OwnerUserId == job.OwnerUserId, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        if (image is null)
        {
            job.Status = PlateAnalysisJobStatuses.Failed;
            job.ErrorCode = PlateAnalysisErrorCodes.ImageNotFound;
            job.ErrorMessageSafe = "The plate image no longer exists.";
            job.FailedAt = now;
            job.UpdatedAt = now;
            await _db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // Cooperative cancellation BEFORE work starts.
        if (context.IsCancellationRequested)
        {
            await MarkCanceledAsync(job, image, cancellationToken);
            throw new OperationCanceledException();
        }

        job.Status = PlateAnalysisJobStatuses.Running;
        job.StartedAt = now;
        job.UpdatedAt = now;
        image.Status = PlateImageStatuses.AnalysisRunning;
        image.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            if (!_pipeline.IsAvailable)
            {
                // A missing/incompatible model is an environment/config state, not
                // a content failure. Use the pipeline's sanitized reason code.
                await FailAsync(job, image,
                    _pipeline.UnavailableReason ?? PlateAnalysisErrorCodes.ModelNotConfigured,
                    "The plate recognition model is not configured.");
                return;
            }

            if (image.Width is not int width || image.Height is not int height || width <= 0 || height <= 0)
            {
                await FailAsync(job, image, PlateAnalysisErrorCodes.UnsupportedImage,
                    "The image dimensions could not be determined.");
                return;
            }
            if ((long)width * height > _options.MaxImagePixels)
            {
                await FailAsync(job, image, PlateAnalysisErrorCodes.ImageTooLarge,
                    "The image exceeds the maximum size for analysis.");
                return;
            }

            byte[] bytes;
            await using (var stream = await _blobs.OpenContentAsync(image.BlobObjectId, cancellationToken))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }

            var result = await _pipeline.AnalyzeAsync(
                new PlateImageInput(bytes, width, height), cancellationToken);

            await PersistSuccessAsync(job, image, result, cancellationToken);
        }
        catch (OperationCanceledException) when (context.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            await MarkCanceledAsync(job, image, CancellationToken.None);
            throw; // let the processor mark the background job cancelled (not failed)
        }
        catch (PlateAnalysisModelException ex)
        {
            // ONNX model load / output / inference failure: record the pipeline's
            // stable SAFE code (never the inner exception's path/message). Not a
            // rethrow — the background job succeeds; the domain job carries it.
            _logger.LogWarning(ex, "Plate analysis model failure for job {JobId} ({Code}).", job.Id, ex.SafeCode);
            await FailAsync(job, image, ex.SafeCode, "Plate analysis failed.");
        }
        catch (Exception ex)
        {
            // Content/model failure: record a SAFE code (never a stack trace,
            // model path, or storage internal) and do NOT rethrow, so the
            // background job succeeds while the domain job carries the failure.
            _logger.LogWarning(ex, "Plate analysis failed for job {JobId}.", job.Id);
            await FailAsync(job, image, PlateAnalysisErrorCodes.AnalysisFailed,
                "Plate analysis failed.");
        }
    }

    private async Task PersistSuccessAsync(
        PlateAnalysisJob job, PlateImage image, PlateAnalysisResult result, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Latest detections REPLACE prior ones for this image (only the newest
            // analysis is surfaced). Older PlateAnalysisJob rows are preserved.
            await _db.PlateDetections
                .Where(d => d.PlateImageId == image.Id && d.OwnerUserId == image.OwnerUserId)
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var d in result.Detections)
            {
                _db.PlateDetections.Add(new PlateDetection
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = image.OwnerUserId,
                    PlateImageId = image.Id,
                    PlateAnalysisJobId = job.Id,
                    Text = d.Text,
                    NormalizedText = d.NormalizedText,
                    CountryHint = d.CountryHint,
                    RegionHint = d.RegionHint,
                    PlateConfidence = d.PlateConfidence,
                    OcrConfidence = d.OcrConfidence,
                    CombinedConfidence = d.CombinedConfidence,
                    BoundingBoxX = d.BoundingBox.X,
                    BoundingBoxY = d.BoundingBox.Y,
                    BoundingBoxWidth = d.BoundingBox.Width,
                    BoundingBoxHeight = d.BoundingBox.Height,
                    PolygonJson = PlatePolygonJson.Serialize(d.Polygon),
                    ModelProfileKey = result.ProfileKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            _db.PlateAnalysisModelRuns.Add(new PlateAnalysisModelRun
            {
                Id = Guid.NewGuid(),
                PlateAnalysisJobId = job.Id,
                ProfileKey = result.ProfileKey,
                DetectorName = result.DetectorName,
                DetectorVersion = result.DetectorVersion,
                OcrName = result.OcrName,
                OcrVersion = result.OcrVersion,
                InputWidth = image.Width ?? 0,
                InputHeight = image.Height ?? 0,
                DurationMs = result.DurationMs,
                DetectionsCount = result.Detections.Count,
                CreatedAt = now,
            });

            var trackedJob = await _db.PlateAnalysisJobs.FirstAsync(j => j.Id == job.Id, cancellationToken);
            trackedJob.Status = PlateAnalysisJobStatuses.Completed;
            trackedJob.CompletedAt = now;
            trackedJob.UpdatedAt = now;

            var trackedImage = await _db.PlateImages.FirstAsync(p => p.Id == image.Id, cancellationToken);
            trackedImage.Status = PlateImageStatuses.AnalysisCompleted;
            trackedImage.UpdatedAt = now;

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private async Task FailAsync(PlateAnalysisJob job, PlateImage image, string errorCode, string safeMessage)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        job.Status = PlateAnalysisJobStatuses.Failed;
        job.ErrorCode = errorCode;
        job.ErrorMessageSafe = safeMessage;
        job.FailedAt = now;
        job.UpdatedAt = now;
        image.Status = PlateImageStatuses.AnalysisFailed;
        image.UpdatedAt = now;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkCanceledAsync(PlateAnalysisJob job, PlateImage image, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        job.Status = PlateAnalysisJobStatuses.Canceled;
        job.UpdatedAt = now;
        // Revert the image to its pre-analysis state; a cancel is not a failure.
        image.Status = PlateImageStatuses.Uploaded;
        image.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int Count, DateTime? LastAnalyzedAt)> CountAndLastAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken)
    {
        var count = await _db.PlateDetections.AsNoTracking()
            .CountAsync(d => d.OwnerUserId == ownerUserId && d.PlateImageId == plateImageId, cancellationToken);
        var lastAt = await _db.PlateAnalysisJobs.AsNoTracking()
            .Where(j => j.OwnerUserId == ownerUserId && j.PlateImageId == plateImageId
                && j.Status == PlateAnalysisJobStatuses.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .Select(j => j.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return (count, lastAt);
    }

    private async Task<PlateAnalysisSummary> BuildSummaryAsync(
        Guid ownerUserId, Guid plateImageId, string imageStatus, CancellationToken cancellationToken)
    {
        var (count, lastAt) = await CountAndLastAsync(ownerUserId, plateImageId, cancellationToken);
        var latestJobId = await _db.PlateAnalysisJobs.AsNoTracking()
            .Where(j => j.OwnerUserId == ownerUserId && j.PlateImageId == plateImageId)
            .OrderByDescending(j => j.RequestedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return PlateAnalysisMapping.ToSummary(imageStatus, count, latestJobId, lastAt);
    }
}

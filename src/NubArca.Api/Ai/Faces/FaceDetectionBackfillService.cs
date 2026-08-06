using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Faces;

// Face Substrate v0: the real body of ai.faces.detect.backfill. For each eligible
// image blob it detects faces and persists FaceDetection rows (bbox + landmarks +
// score), then records ONE terminal BlobAiArtifactStatus (capability
// face-detection) — the completion marker, written even for the legitimate
// zero-face case so a blob is never re-detected. Keyset-paged by BlobObjectId,
// sliceable/checkpointed/cancellable, idempotent.
//
// PRIVACY: eligibility requires an active, NON-VAULT FileItem to reference the
// blob (the FileItems query carries the global Private-Vault filter), so a blob
// referenced only by Private-Vault content is never detected. No owner identity
// is created — detections are a blob-level technical artifact.
public sealed class FaceDetectionBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IOptions<AiOptions> _options;
    private readonly TimeProvider _clock;

    public FaceDetectionBackfillService(
        AppDbContext db, IBlobService blobs, IOptions<AiOptions> options, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _options = options;
        _clock = clock;
    }

    public async Task<FaceBackfillResult> RunAsync(
        IFaceDetector detector,
        AiProfile profile,
        FaceBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var profileId = profile.Id;

        if (options.DryRun)
        {
            var pending = await CandidateQuery(profileId, options.TargetBlobObjectId).CountAsync(cancellationToken);
            if (options.Limit is int lim && pending > lim)
            {
                pending = lim;
            }
            log?.Invoke($"ai faces detect (dry-run): {pending} image blob(s) would be scanned for faces.");
            return FaceBackfillResult.Dry();
        }

        var checkpoint = FaceBackfillCheckpoint.TryParse(checkpointJson) ?? FaceBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId ?? Guid.Empty;
        var processedTotal = checkpoint.ProcessedTotal;
        var producedTotal = checkpoint.ProducedTotal; // faces detected
        var skippedTotal = checkpoint.SkippedTotal;   // zero-face completions
        var failedTotal = checkpoint.FailedTotal;

        var processedSlice = 0;
        var producedSlice = 0;
        var skippedSlice = 0;
        var failedSlice = 0;
        long yieldCounter = 0;
        var yielded = false;
        var exhausted = false;
        var maxFaces = _options.Value.Face.MaxFacesPerImage;

        bool LimitReached() => options.Limit is int gl && processedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"detecting faces ({processedTotal} blobs, {producedTotal} faces, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateQuery(profileId, options.TargetBlobObjectId)
                .Where(b => b.Id > cursor)
                .OrderBy(b => b.Id)
                .Select(b => b.Id)
                .Take(PageSize)
                .ToListAsync(cancellationToken);

            if (page.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var blobId in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = blobId;
                processedTotal++;
                processedSlice++;
                yieldCounter++;

                var outcome = await DetectBlobAsync(detector, profile, blobId, maxFaces, cancellationToken);
                switch (outcome.Kind)
                {
                    case DetectKind.Faces:
                        producedTotal += outcome.FaceCount; producedSlice += outcome.FaceCount;
                        break;
                    case DetectKind.ZeroFaces:
                        skippedTotal++; skippedSlice++;
                        break;
                    default:
                        failedTotal++; failedSlice++;
                        break;
                }

                await ReportAsync();

                if (LimitReached()) break;
                if (shouldYield is not null && shouldYield(yieldCounter)) { yielded = true; break; }
            }

            if (!yielded && !LimitReached() && page.Count < PageSize)
            {
                exhausted = true;
            }
        }

        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var moreWork = !exhausted && !LimitReached();
        var nextCheckpointJson = moreWork
            ? new FaceBackfillCheckpoint(
                FaceBackfillCheckpoint.CurrentVersion, cursor, processedTotal, producedTotal, skippedTotal, failedTotal)
                .Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"ai faces detect: {(moreWork ? "yielded" : "done")} — blobs {processedSlice} "
            + $"(faces {producedSlice}, zero-face {skippedSlice}, failed {failedSlice}; total blobs {processedTotal}).");

        return new FaceBackfillResult(
            processedSlice, producedSlice, skippedSlice, failedSlice, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson,
            ProcessedTotal: processedTotal, ProducedTotal: producedTotal,
            SkippedTotal: skippedTotal, FailedTotal: failedTotal);
    }

    private enum DetectKind { Faces, ZeroFaces, Failed }

    private readonly record struct DetectOutcome(DetectKind Kind, int FaceCount);

    private async Task<DetectOutcome> DetectBlobAsync(
        IFaceDetector detector, AiProfile profile, Guid blobId, int maxFaces, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            await using var stream = await _blobs.OpenContentAsync(blobId, cancellationToken);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DetectOutcome(DetectKind.Failed, 0);
        }

        AiFaceDetectionResult detection;
        try
        {
            detection = await detector.DetectFacesAsync(bytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DetectOutcome(DetectKind.Failed, 0);
        }

        var faces = detection.Faces;
        if (maxFaces > 0 && faces.Count > maxFaces)
        {
            faces = faces
                .OrderByDescending(f => f.Confidence ?? 0)
                .Take(maxFaces)
                .ToList();
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // Idempotent (re)write for this (blob, profile): clear any partial rows
        // from a crashed prior run, insert the fresh set, and record the terminal
        // status. Detection completion is marked even when zero faces are found.
        try
        {
            var existing = await _db.FaceDetections
                .Where(d => d.BlobObjectId == blobId && d.ProfileId == profile.Id)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                _db.FaceDetections.RemoveRange(existing);
            }

            var index = 0;
            foreach (var f in faces)
            {
                _db.FaceDetections.Add(new FaceDetection
                {
                    Id = Guid.NewGuid(),
                    BlobObjectId = blobId,
                    ProfileId = profile.Id,
                    DetectorProfileKey = profile.Key,
                    FaceIndex = index++,
                    BoundingBoxX = Clamp01(f.X),
                    BoundingBoxY = Clamp01(f.Y),
                    BoundingBoxWidth = Clamp01(f.Width),
                    BoundingBoxHeight = Clamp01(f.Height),
                    DetectionScore = f.Confidence,
                    FaceQualityScore = null,
                    LandmarksJson = FaceLandmarksJson.Serialize(f.Landmarks),
                    CreatedAt = now,
                });
            }

            await UpsertDetectionStatusAsync(blobId, profile.Id, now, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Persist failure: leave the blob eligible for a future run (no status).
            _db.ChangeTracker.Clear();
            return new DetectOutcome(DetectKind.Failed, 0);
        }

        return faces.Count == 0
            ? new DetectOutcome(DetectKind.ZeroFaces, 0)
            : new DetectOutcome(DetectKind.Faces, faces.Count);
    }

    private async Task UpsertDetectionStatusAsync(
        Guid blobId, Guid profileId, DateTime now, CancellationToken cancellationToken)
    {
        var status = await _db.BlobAiArtifactStatuses.FirstOrDefaultAsync(
            s => s.BlobObjectId == blobId && s.ProfileId == profileId
                 && s.Capability == AiCapabilities.FaceDetection,
            cancellationToken);

        if (status is null)
        {
            _db.BlobAiArtifactStatuses.Add(new BlobAiArtifactStatus
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobId,
                ProfileId = profileId,
                Capability = AiCapabilities.FaceDetection,
                Status = AiArtifactStatuses.Completed,
                IsPermanent = true,
                AttemptCount = 1,
                CreatedAt = now,
            });
        }
        else
        {
            status.Status = AiArtifactStatuses.Completed;
            status.IsPermanent = true;
            status.AttemptCount += 1;
            status.UpdatedAt = now;
        }
    }

    // Eligible, not-yet-detected candidates: image blobs referenced by an active
    // NON-VAULT FileItem, with no terminal face-detection status for this profile.
    private IQueryable<BlobObject> CandidateQuery(Guid profileId, Guid? targetBlobObjectId)
    {
        return _db.BlobObjects.AsNoTracking().Where(b =>
            (targetBlobObjectId == null || b.Id == targetBlobObjectId)
            &&
            _db.BlobMetadata.Any(m => m.BlobObjectId == b.Id && m.MediaCategory == MediaCategories.Image)
            && _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            && !_db.BlobAiArtifactStatuses.Any(s =>
                s.BlobObjectId == b.Id && s.ProfileId == profileId
                && s.Capability == AiCapabilities.FaceDetection));
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0d, 1d);
}

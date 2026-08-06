using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;

namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: builds the canonical face tracks of ONE video blob at ONE
// segmentation version, under ONE analysis version and ONE face profile.
//
//   video blob → deterministic temporal sampling → detection + embedding
//              → temporal association → canonical face tracks
//
// Responsibilities kept here: manifest/profile eligibility, bounded sampling,
// per-frame failure isolation, canonical persistence and the aggregate status.
// Frame extraction is IVideoSemanticFrameStreamExtractor (staged once, one frame
// in memory at a time); detection/recognition are the SAME IFaceDetector /
// IFaceEmbedder the photo face substrate uses, so video tracks live in the photo
// recognition space. Sampling, association and aggregation are three PURE,
// separately tested collaborators.
//
// BLOB-LEVEL AND OWNER-FREE: nothing here reads or stores OwnerUserId,
// FileItemId, PersonId or a person name. Eligibility is only "at least one
// current, non-deleted, media-library-active reference exists" (the global
// Private-Vault query filter keeps vault-only blobs out by construction). No
// person assignment is performed and none is representable.
//
// NO PERSISTENT FRAME: extracted frames are transient in-memory inference input.
// Nothing is written to the derived store — see the Gate 4 note on
// VideoFaceTrack.RepresentativeCropBlobObjectId.
public sealed class VideoFaceAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IVideoSemanticFrameStreamExtractor _extractor;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<VideoFaceAnalysisOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoFaceAnalysisService> _logger;

    public VideoFaceAnalysisService(
        AppDbContext db,
        IBlobService blobs,
        IVideoSemanticFrameStreamExtractor extractor,
        IAiVectorSerializer serializer,
        IOptions<VideoFaceAnalysisOptions> options,
        TimeProvider clock,
        ILogger<VideoFaceAnalysisService> logger)
    {
        _db = db;
        _blobs = blobs;
        _extractor = extractor;
        _serializer = serializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<VideoFaceAnalysisOutcome> ProcessBlobAsync(
        IFaceDetector detector,
        IFaceEmbedder embedder,
        AiProfile profile,
        Guid blobObjectId,
        int segmentationVersion,
        int analysisVersion,
        CancellationToken cancellationToken = default,
        Guid? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(profile);

        var started = Stopwatch.GetTimestamp();
        var options = _options.Value;

        // ---- profile guards -------------------------------------------------
        // Operator/config states, never blob outcomes: nothing is written.
        if (!string.Equals(profile.Capability, AiCapabilities.FaceEmbedding, StringComparison.Ordinal)
            || profile.Dimension is not > 0)
        {
            return VideoFaceAnalysisOutcome.NotEligible(VideoFaceErrorCodes.ProfileMissing);
        }

        if (analysisVersion <= 0)
        {
            return VideoFaceAnalysisOutcome.NotEligible(VideoFaceErrorCodes.ApplicationBug);
        }

        // Only a COMPLETED temporal manifest is analysable. A missing or
        // non-completed manifest is premature scheduling — no row is written
        // (implicit pending), and the candidate query re-offers the blob once
        // segmentation lands.
        var index = await _db.VideoSemanticIndexes.AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.BlobObjectId == blobObjectId && i.SegmentationVersion == segmentationVersion,
                cancellationToken);
        if (index is null || index.Status != AiArtifactStatuses.Completed)
        {
            return VideoFaceAnalysisOutcome.NotEligible(VideoFaceErrorCodes.SegmentationMissing);
        }

        // Idempotency: a terminal analysis is never rebuilt.
        var aggregate = await _db.VideoFaceAnalysisStatuses.FirstOrDefaultAsync(
            s => s.VideoSemanticIndexId == index.Id
                && s.AnalysisVersion == analysisVersion
                && s.DetectionProfileId == profile.Id
                && s.EmbeddingProfileId == profile.Id,
            cancellationToken);
        if (aggregate is not null
            && (aggregate.Status == VideoFaceAnalysisStatuses.Completed
                || aggregate.Status == VideoFaceAnalysisStatuses.Skipped))
        {
            return VideoFaceAnalysisOutcome.AlreadyTerminal(aggregate);
        }

        // Eligibility is re-checked at RUN time — a file can be deleted, excluded
        // or vaulted between enqueue and execution.
        var hasEligibleReference = await _db.FileItems.AsNoTracking()
            .AnyAsync(
                f => f.BlobObjectId == blobObjectId
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active,
                cancellationToken);
        if (!hasEligibleReference)
        {
            var skipped = await WriteAsync(
                aggregate, index, profile, analysisVersion, VideoFaceAnalysisStatuses.Skipped,
                planned: 0, processed: 0, failed: 0, tracks: null,
                VideoFaceErrorCodes.NoEligibleReference, cancellationToken);
            LogAttempt(jobId, analysisVersion, profile.Key, skipped, FrameSweep.Empty, started);
            return VideoFaceAnalysisOutcome.From(skipped);
        }

        // ---- bounded sampling plan -----------------------------------------
        var segments = await _db.VideoSemanticSegments.AsNoTracking()
            .Where(s => s.VideoSemanticIndexId == index.Id)
            .OrderBy(s => s.SegmentIndex)
            .Select(s => new VideoFaceSegmentInterval(
                s.SegmentIndex, s.StartMilliseconds, s.EndMilliseconds))
            .ToListAsync(cancellationToken);

        var plan = VideoFaceSamplePlanner.Plan(segments, options);
        if (plan.Count == 0)
        {
            // A completed manifest always has at least one usable interval; an
            // empty plan is a manifest defect, not a property of the content.
            return VideoFaceAnalysisOutcome.NotEligible(VideoFaceErrorCodes.SegmentationMissing);
        }

        // ---- bounded analysis budget ---------------------------------------
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessTimeoutSeconds));

        FrameSweep sweep;
        try
        {
            sweep = await SweepFramesAsync(
                detector, embedder, profile, blobObjectId, plan, options, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The BUDGET elapsed, not the caller's cancellation. That is a
            // retryable environment outcome, never a content skip.
            var timedOut = await WriteAsync(
                aggregate, index, profile, analysisVersion, VideoFaceAnalysisStatuses.Failed,
                plan.Count, processed: 0, failed: plan.Count, tracks: null,
                VideoFaceErrorCodes.Timeout, cancellationToken);
            LogAttempt(jobId, analysisVersion, profile.Key, timedOut, FrameSweep.Empty, started);
            return VideoFaceAnalysisOutcome.From(timedOut);
        }

        // A staging failure is batch-level: no frame was ever attempted.
        if (sweep.StagingErrorCode is { } stagingError)
        {
            var failed = await WriteAsync(
                aggregate, index, profile, analysisVersion, VideoFaceAnalysisStatuses.Failed,
                plan.Count, processed: 0, failed: plan.Count, tracks: null,
                stagingError, cancellationToken);
            LogAttempt(jobId, analysisVersion, profile.Key, failed, sweep, started);
            return VideoFaceAnalysisOutcome.From(failed);
        }

        // ---- association + finalization ------------------------------------
        IReadOnlyList<VideoFaceTrackResult> tracks;
        var rejectedShortTracks = 0;
        var trackingStarted = Stopwatch.GetTimestamp();
        try
        {
            var drafts = VideoFaceTracker.Associate(sweep.Observations, options);
            var finalized = new List<VideoFaceTrackResult>(drafts.Count);
            foreach (var draft in drafts)
            {
                var result = VideoFaceTrackAggregator.Finalize(
                    draft, profile.Dimension!.Value, options);
                if (result is null)
                {
                    rejectedShortTracks++;
                    continue;
                }

                finalized.Add(result);
            }

            tracks = finalized;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-faces: association raised {ExceptionType}.", ex.GetType().Name);
            var failed = await WriteAsync(
                aggregate, index, profile, analysisVersion, VideoFaceAnalysisStatuses.Failed,
                plan.Count, sweep.ProcessedFrames, sweep.FailedFrames, tracks: null,
                VideoFaceErrorCodes.TrackingFailed, cancellationToken);
            LogAttempt(jobId, analysisVersion, profile.Key, failed, sweep, started);
            return VideoFaceAnalysisOutcome.From(failed);
        }

        var trackingMs = (long)Stopwatch.GetElapsedTime(trackingStarted).TotalMilliseconds;

        // ---- outcome classification -----------------------------------------
        var (status, errorCode) = Classify(sweep, tracks.Count);

        var written = await WriteAsync(
            aggregate, index, profile, analysisVersion, status,
            plan.Count, sweep.ProcessedFrames, sweep.FailedFrames,
            status == VideoFaceAnalysisStatuses.Completed || status == VideoFaceAnalysisStatuses.Partial
                ? tracks
                : null,
            errorCode, cancellationToken);

        LogAttempt(
            jobId, analysisVersion, profile.Key, written,
            sweep with { RejectedShortTracks = rejectedShortTracks, TrackingMilliseconds = trackingMs },
            started);
        return VideoFaceAnalysisOutcome.From(written);
    }

    // completed → every planned frame processed and at least one track;
    // partial   → tracks exist but some frames failed;
    // failed    → nothing usable AND something went wrong (retryable);
    // skipped   → nothing went wrong and there is simply nothing to record
    //             (terminal, never retried).
    private static (string Status, string? ErrorCode) Classify(FrameSweep sweep, int trackCount)
    {
        if (sweep.ProcessedFrames == 0)
        {
            return (VideoFaceAnalysisStatuses.Failed, DominantFailure(sweep));
        }

        if (trackCount > 0)
        {
            return sweep.FailedFrames == 0
                ? (VideoFaceAnalysisStatuses.Completed, null)
                : (VideoFaceAnalysisStatuses.Partial, DominantFailure(sweep));
        }

        // No track. When frames failed, the faces may well have been in exactly
        // those frames — retryable, not a terminal "no faces" verdict.
        if (sweep.FailedFrames > 0)
        {
            return (VideoFaceAnalysisStatuses.Failed, DominantFailure(sweep));
        }

        return sweep.Observations.Count == 0
            ? (VideoFaceAnalysisStatuses.Skipped, VideoFaceErrorCodes.NoFacesFound)
            : (VideoFaceAnalysisStatuses.Skipped, VideoFaceErrorCodes.NoTracksRetained);
    }

    private static string DominantFailure(FrameSweep sweep)
    {
        if (sweep.DimensionMismatches > 0
            && sweep.DimensionMismatches >= sweep.ExtractionFailures
            && sweep.DimensionMismatches >= sweep.DetectionFailures
            && sweep.DimensionMismatches >= sweep.EmbeddingFailures)
        {
            return VideoFaceErrorCodes.DimensionMismatch;
        }

        if (sweep.ExtractionFailures >= sweep.DetectionFailures
            && sweep.ExtractionFailures >= sweep.EmbeddingFailures
            && sweep.ExtractionFailures > 0)
        {
            return sweep.ExtractionErrorCode ?? VideoFaceErrorCodes.FrameExtractFailed;
        }

        if (sweep.DetectionFailures >= sweep.EmbeddingFailures && sweep.DetectionFailures > 0)
        {
            return VideoFaceErrorCodes.FaceDetectionFailed;
        }

        return sweep.EmbeddingFailures > 0
            ? VideoFaceErrorCodes.FaceEmbeddingFailed
            : VideoFaceErrorCodes.ApplicationBug;
    }

    // ---- frame sweep --------------------------------------------------------

    // Streams every planned frame through detection + recognition, accumulating
    // ACCEPTED observations. Each frame is an independent unit: one bad seek, one
    // detector throw or one recognizer throw costs that frame only.
    private async Task<FrameSweep> SweepFramesAsync(
        IFaceDetector detector,
        IFaceEmbedder embedder,
        AiProfile profile,
        Guid blobObjectId,
        IReadOnlyList<VideoFaceSamplePlanner.PlannedFrame> plan,
        VideoFaceAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var sweep = new FrameSweepBuilder();
        var requests = new List<VideoSemanticFrameRequest>(plan.Count);
        var frameIndexById = new Dictionary<Guid, int>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            // The extractor keys frames by an opaque GUID; VFACE-01 has no sample
            // rows, so a fresh per-frame correlation id is generated here. It
            // never reaches the database or a log line.
            var id = Guid.NewGuid();
            frameIndexById[id] = i;
            requests.Add(new VideoSemanticFrameRequest(id, plan[i].TimestampMilliseconds));
        }

        var stagingError = await _extractor.ExtractFramesStreamingAsync(
            ct => _blobs.OpenContentAsync(blobObjectId, ct),
            requests,
            // Frame resolution is THIS pipeline's setting: the face detector's
            // input edge and the pixel-size gate decide it, never the SigLIP2
            // video-embedding configuration.
            options.FrameMaxEdge,
            async (frame, ct) =>
            {
                var frameIndex = frameIndexById[frame.SampleId];
                await ProcessFrameAsync(
                    detector, embedder, profile, frameIndex, frame, options, sweep, ct);
            },
            cancellationToken);

        return sweep.Build(stagingError is null ? null : MapExtractionCode(stagingError));
    }

    private async Task ProcessFrameAsync(
        IFaceDetector detector,
        IFaceEmbedder embedder,
        AiProfile profile,
        int frameIndex,
        VideoSemanticFrameResult frame,
        VideoFaceAnalysisOptions options,
        FrameSweepBuilder sweep,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!frame.Succeeded)
        {
            sweep.ExtractionFailed(MapExtractionCode(frame.ErrorCode));
            return;
        }

        var bytes = frame.ImageBytes!;

        // The ACTUAL decoded geometry — never assumed from configuration — so the
        // pixel-size gate means the same thing for a 4K source and a phone clip.
        int width, height;
        try
        {
            var info = Image.Identify(bytes);
            width = info.Width;
            height = info.Height;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-faces: frame geometry could not be read ({ExceptionType}).", ex.GetType().Name);
            sweep.ExtractionFailed(VideoFaceErrorCodes.FrameExtractFailed);
            return;
        }

        if (width <= 0 || height <= 0)
        {
            sweep.ExtractionFailed(VideoFaceErrorCodes.FrameExtractFailed);
            return;
        }

        var detectionStarted = Stopwatch.GetTimestamp();
        AiFaceDetectionResult detection;
        try
        {
            detection = await detector.DetectFacesAsync(bytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-faces: detection raised {ExceptionType}.", ex.GetType().Name);
            sweep.DetectionFailed();
            return;
        }

        sweep.AddDetectionTime(Stopwatch.GetElapsedTime(detectionStarted));
        sweep.FrameProcessed(detection.Faces.Count);

        var candidates = SelectCandidates(detection.Faces, width, height, options);
        if (candidates.Count == 0)
        {
            return;
        }

        var landmarks = candidates
            .Select(c => (IReadOnlyList<FaceLandmark>)(c.Face.Landmarks ?? Array.Empty<FaceLandmark>()))
            .ToList();

        var embeddingStarted = Stopwatch.GetTimestamp();
        IReadOnlyList<FaceEmbedAttempt> attempts;
        try
        {
            attempts = embedder is IAlignedFaceEmbedder aligned
                ? await aligned.EmbedAlignedFacesAsync(bytes, landmarks, profile, cancellationToken)
                : await EmbedIndividuallyAsync(embedder, profile, bytes, candidates.Count, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A whole-frame recognition failure (undecodable bytes, batch
            // timeout): the frame contributes nothing and is counted failed.
            _logger.LogWarning(
                "video-faces: recognition raised {ExceptionType}.", ex.GetType().Name);
            sweep.EmbeddingFailed();
            return;
        }

        sweep.AddEmbeddingTime(Stopwatch.GetElapsedTime(embeddingStarted));

        var expectedDimension = profile.Dimension!.Value;
        for (var i = 0; i < candidates.Count && i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            if (attempt.Outcome != FaceEmbedOutcome.Ok || attempt.Embedding is not { } embedding)
            {
                sweep.FaceRejected();
                continue;
            }

            if (embedding.Vector.Length != expectedDimension)
            {
                sweep.DimensionMismatch();
                continue;
            }

            if (embedding.Vector.Any(v => !float.IsFinite(v)))
            {
                sweep.FaceRejected();
                continue;
            }

            var candidate = candidates[i];
            sweep.Accept(new VideoFaceObservation(
                FrameIndex: frameIndex,
                TimestampMilliseconds: frame.TimestampMilliseconds,
                FaceIndex: candidate.FaceIndex,
                X: Clamp01(candidate.Face.X),
                Y: Clamp01(candidate.Face.Y),
                Width: Clamp01(candidate.Face.Width),
                Height: Clamp01(candidate.Face.Height),
                Confidence: candidate.Face.Confidence,
                QualityScore: candidate.QualityScore,
                Embedding: embedding.Vector));
        }
    }

    // Confidence / size / quality gates plus the per-frame cap. The cap keeps the
    // HIGHEST-confidence faces but restores the detector's own ordering afterwards,
    // so FaceIndex stays a stable in-frame ordinal.
    private static List<FaceCandidate> SelectCandidates(
        IReadOnlyList<DetectedFace> faces, int width, int height, VideoFaceAnalysisOptions options)
    {
        var accepted = new List<FaceCandidate>(faces.Count);
        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            if (face.Confidence is { } confidence && confidence < options.MinimumDetectionConfidence)
            {
                continue;
            }

            var facePixelWidth = Clamp01(face.Width) * width;
            var facePixelHeight = Clamp01(face.Height) * height;
            if (Math.Min(facePixelWidth, facePixelHeight) < options.MinimumFaceSizePixels)
            {
                continue;
            }

            var quality = VideoFaceQuality.Score(
                face.Confidence, facePixelWidth, facePixelHeight, options.QualityReferenceFaceSizePixels);
            if (quality < options.MinimumQualityScore)
            {
                continue;
            }

            accepted.Add(new FaceCandidate(i, face, quality));
        }

        if (accepted.Count > options.MaximumFacesPerFrame)
        {
            accepted = accepted
                .OrderByDescending(c => c.Face.Confidence ?? 0d)
                .ThenByDescending(c => c.QualityScore)
                .ThenBy(c => c.FaceIndex)
                .Take(options.MaximumFacesPerFrame)
                .OrderBy(c => c.FaceIndex)
                .ToList();
        }

        return accepted;
    }

    // Fallback for a backend without landmark alignment (the deterministic test
    // backend). Mirrors the photo embedding backfill: each face is isolated.
    private static async Task<IReadOnlyList<FaceEmbedAttempt>> EmbedIndividuallyAsync(
        IFaceEmbedder embedder, AiProfile profile, byte[] bytes, int count,
        CancellationToken cancellationToken)
    {
        var attempts = new FaceEmbedAttempt[count];
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                attempts[i] = FaceEmbedAttempt.Ok(
                    await embedder.EmbedFaceAsync(bytes, profile, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                attempts[i] = FaceEmbedAttempt.RecognitionFailed;
            }
        }

        return attempts;
    }

    // The extractor speaks the VSEM vocabulary; the analysis speaks its own.
    private static string MapExtractionCode(string? code) => code switch
    {
        VideoSemanticErrorCodes.BlobStorage => VideoFaceErrorCodes.BlobStorage,
        VideoSemanticErrorCodes.TemporaryStorage => VideoFaceErrorCodes.TemporaryStorage,
        VideoSemanticErrorCodes.ProcessTimeout => VideoFaceErrorCodes.Timeout,
        _ => VideoFaceErrorCodes.FrameExtractFailed,
    };

    private static double Clamp01(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    // ---- persistence --------------------------------------------------------

    // Writes the aggregate and, when tracks are supplied, REPLACES the analysis'
    // track set — all in ONE SaveChanges, so a half-written analysis can never be
    // observed. A non-terminal (failed/partial) analysis stays retryable and its
    // rerun rewrites the set from scratch.
    private async Task<VideoFaceAnalysisStatus> WriteAsync(
        VideoFaceAnalysisStatus? aggregate,
        VideoSemanticIndex index,
        AiProfile profile,
        int analysisVersion,
        string status,
        int planned,
        int processed,
        int failed,
        IReadOnlyList<VideoFaceTrackResult>? tracks,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (aggregate is null)
        {
            aggregate = new VideoFaceAnalysisStatus
            {
                Id = Guid.NewGuid(),
                VideoSemanticIndexId = index.Id,
                AnalysisVersion = analysisVersion,
                DetectionProfileId = profile.Id,
                EmbeddingProfileId = profile.Id,
                CreatedAt = now,
            };
            _db.VideoFaceAnalysisStatuses.Add(aggregate);
        }
        else
        {
            var stale = await _db.VideoFaceTracks
                .Where(t => t.VideoFaceAnalysisStatusId == aggregate.Id)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                _db.VideoFaceTracks.RemoveRange(stale);
            }
        }

        aggregate.Status = status;
        aggregate.PlannedFrameCount = planned;
        aggregate.ProcessedFrameCount = processed;
        aggregate.FailedFrameCount = failed;
        aggregate.TrackCount = tracks?.Count ?? 0;
        aggregate.ErrorCode = errorCode;
        aggregate.AttemptCount += 1;
        aggregate.UpdatedAt = now;
        aggregate.CompletedAt = now;

        if (tracks is not null)
        {
            var ordinal = 0;
            foreach (var track in tracks)
            {
                _db.VideoFaceTracks.Add(new VideoFaceTrack
                {
                    Id = Guid.NewGuid(),
                    VideoFaceAnalysisStatusId = aggregate.Id,
                    TrackIndex = ordinal++,
                    StartMilliseconds = track.StartMilliseconds,
                    EndMilliseconds = track.EndMilliseconds,
                    RepresentativeTimestampMilliseconds = track.RepresentativeTimestampMilliseconds,
                    DetectionCount = track.DetectionCount,
                    EmbeddingBytes = _serializer.Serialize(track.Embedding, profile.Dimension!.Value),
                    EmbeddingDimension = profile.Dimension.Value,
                    QualityScore = track.QualityScore,
                    RepresentativeBoundingBoxX = track.RepresentativeBoundingBoxX,
                    RepresentativeBoundingBoxY = track.RepresentativeBoundingBoxY,
                    RepresentativeBoundingBoxWidth = track.RepresentativeBoundingBoxWidth,
                    RepresentativeBoundingBoxHeight = track.RepresentativeBoundingBoxHeight,
                    // Gate 4: VFACE-01 persists no crop.
                    RepresentativeCropBlobObjectId = null,
                    CreatedAt = now,
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return aggregate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            // A unique race (a concurrent run owns this analysis) or a database
            // problem. Neither may escalate into a job crash: the candidate query
            // simply offers the blob again.
            _logger.LogWarning(
                "video-faces: analysis row could not be persisted ({ExceptionType}).", ex.GetType().Name);
            _db.ChangeTracker.Clear();
            return new VideoFaceAnalysisStatus
            {
                Id = aggregate.Id,
                VideoSemanticIndexId = index.Id,
                AnalysisVersion = analysisVersion,
                DetectionProfileId = profile.Id,
                EmbeddingProfileId = profile.Id,
                Status = VideoFaceAnalysisStatuses.Failed,
                PlannedFrameCount = planned,
                ProcessedFrameCount = processed,
                FailedFrameCount = failed,
                TrackCount = 0,
                ErrorCode = VideoFaceErrorCodes.Database,
                AttemptCount = aggregate.AttemptCount,
                CreatedAt = aggregate.CreatedAt,
                UpdatedAt = now,
            };
        }
    }

    // ---- observability ------------------------------------------------------

    // Counts, sanitized codes, the profile's stable key and elapsed times only.
    // NEVER an embedding, a crop byte, a person name, a filename, a path, a
    // storage key, a blob id or raw process output.
    private void LogAttempt(
        Guid? jobId, int analysisVersion, string profileKey,
        VideoFaceAnalysisStatus aggregate, FrameSweep sweep, long startedTimestamp)
    {
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        var retryable = aggregate.Status is VideoFaceAnalysisStatuses.Failed
            or VideoFaceAnalysisStatuses.Partial;

        _logger.LogInformation(
            "video-faces: operation={Operation} job={JobId} analysis-version={AnalysisVersion} "
            + "detection-profile={DetectionProfileKey} embedding-profile={EmbeddingProfileKey} "
            + "status={Status} planned-frames={Planned} processed-frames={Processed} "
            + "failed-frames={Failed} detections-found={DetectionsFound} "
            + "detections-accepted={DetectionsAccepted} tracks={Tracks} "
            + "rejected-short-tracks={RejectedShortTracks} detect-ms={DetectionMs} "
            + "embed-ms={EmbeddingMs} track-ms={TrackingMs} failure={FailureCode} "
            + "retry={Retryable} attempts={Attempts} elapsed-ms={ElapsedMs}",
            "video.face.analysis",
            jobId,
            analysisVersion,
            profileKey,
            profileKey,
            aggregate.Status,
            aggregate.PlannedFrameCount,
            aggregate.ProcessedFrameCount,
            aggregate.FailedFrameCount,
            sweep.DetectionsFound,
            sweep.Observations.Count,
            aggregate.TrackCount,
            sweep.RejectedShortTracks,
            sweep.DetectionMilliseconds,
            sweep.EmbeddingMilliseconds,
            sweep.TrackingMilliseconds,
            aggregate.ErrorCode,
            retryable,
            aggregate.AttemptCount,
            elapsedMs);
    }

    // ---- internals ----------------------------------------------------------

    private readonly record struct FaceCandidate(int FaceIndex, DetectedFace Face, double QualityScore);

    private sealed record FrameSweep(
        string? StagingErrorCode,
        int ProcessedFrames,
        int FailedFrames,
        int ExtractionFailures,
        int DetectionFailures,
        int EmbeddingFailures,
        int DimensionMismatches,
        string? ExtractionErrorCode,
        int DetectionsFound,
        long DetectionMilliseconds,
        long EmbeddingMilliseconds,
        IReadOnlyList<VideoFaceObservation> Observations)
    {
        public int RejectedShortTracks { get; init; }

        public long TrackingMilliseconds { get; init; }

        // The "nothing was swept" value, used when an outcome is decided before
        // (or instead of) any frame work.
        public static FrameSweep Empty { get; } = new(
            null, 0, 0, 0, 0, 0, 0, null, 0, 0, 0, Array.Empty<VideoFaceObservation>());
    }

    private sealed class FrameSweepBuilder
    {
        private readonly List<VideoFaceObservation> _observations = new();
        private TimeSpan _detection;
        private TimeSpan _embedding;

        private int _processed;
        private int _failed;
        private int _extractionFailures;
        private int _detectionFailures;
        private int _embeddingFailures;
        private int _dimensionMismatches;
        private int _detectionsFound;
        private string? _extractionErrorCode;

        public void ExtractionFailed(string code)
        {
            _failed++;
            _extractionFailures++;
            _extractionErrorCode ??= code;
        }

        public void DetectionFailed()
        {
            _failed++;
            _detectionFailures++;
        }

        public void EmbeddingFailed()
        {
            _failed++;
            _embeddingFailures++;
        }

        public void DimensionMismatch() => _dimensionMismatches++;

        public void FaceRejected()
        {
            // A per-face non-Ok recognition outcome. The FRAME still counts as
            // processed: only faces are lost, and the aggregate must not read as
            // a frame failure.
        }

        public void FrameProcessed(int detectionsFound)
        {
            _processed++;
            _detectionsFound += detectionsFound;
        }

        public void AddDetectionTime(TimeSpan elapsed) => _detection += elapsed;

        public void AddEmbeddingTime(TimeSpan elapsed) => _embedding += elapsed;

        public void Accept(VideoFaceObservation observation) => _observations.Add(observation);

        public FrameSweep Build(string? stagingErrorCode) => new(
            stagingErrorCode,
            _processed,
            _failed,
            _extractionFailures,
            _detectionFailures,
            _embeddingFailures,
            _dimensionMismatches,
            _extractionErrorCode,
            _detectionsFound,
            (long)_detection.TotalMilliseconds,
            (long)_embedding.TotalMilliseconds,
            _observations);
    }
}

public enum VideoFaceAnalysisOutcomeKind
{
    // Every planned frame processed and at least one canonical track persisted.
    Completed,

    // Tracks exist, but at least one planned frame failed.
    Partial,

    // Processing ran and produced no usable result.
    Failed,

    // A permanent, non-retryable outcome (no eligible reference, no faces, no
    // track above the evidence floor).
    Skipped,

    // A terminal analysis for this (manifest, version, profile pair) existed.
    AlreadyTerminal,

    // Nothing was processed and nothing was written: no completed manifest, or
    // the profile cannot host face work.
    NotEligible,
}

// The result of ONE analysis attempt on ONE blob. Counts and sanitized codes
// only — no embeddings, no identifiers beyond what the caller already holds.
public sealed record VideoFaceAnalysisOutcome(
    VideoFaceAnalysisOutcomeKind Kind,
    string? ErrorCode = null,
    int PlannedFrameCount = 0,
    int ProcessedFrameCount = 0,
    int FailedFrameCount = 0,
    int TrackCount = 0)
{
    public static VideoFaceAnalysisOutcome NotEligible(string reason)
        => new(VideoFaceAnalysisOutcomeKind.NotEligible, reason);

    public static VideoFaceAnalysisOutcome AlreadyTerminal(VideoFaceAnalysisStatus aggregate)
        => new(
            VideoFaceAnalysisOutcomeKind.AlreadyTerminal, aggregate.ErrorCode,
            aggregate.PlannedFrameCount, aggregate.ProcessedFrameCount,
            aggregate.FailedFrameCount, aggregate.TrackCount);

    public static VideoFaceAnalysisOutcome From(VideoFaceAnalysisStatus aggregate)
        => new(
            aggregate.Status switch
            {
                VideoFaceAnalysisStatuses.Completed => VideoFaceAnalysisOutcomeKind.Completed,
                VideoFaceAnalysisStatuses.Partial => VideoFaceAnalysisOutcomeKind.Partial,
                VideoFaceAnalysisStatuses.Skipped => VideoFaceAnalysisOutcomeKind.Skipped,
                _ => VideoFaceAnalysisOutcomeKind.Failed,
            },
            aggregate.ErrorCode,
            aggregate.PlannedFrameCount,
            aggregate.ProcessedFrameCount,
            aggregate.FailedFrameCount,
            aggregate.TrackCount);
}

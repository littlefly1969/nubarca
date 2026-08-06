using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Video;

// VSEM-01: builds and persists the canonical temporal manifest of ONE video
// blob at ONE segmentation version.
//
// Responsibilities kept here (and nowhere else): eligibility, authoritative
// metadata loading, idempotency, atomic persistence, failure classification.
// The FFmpeg invocation lives in IVideoSemanticSegmenter; all normalization
// lives in the pure VideoSemanticManifestBuilder.
//
// BLOB-LEVEL AND OWNER-FREE. The manifest stores no OwnerUserId, FileItemId,
// PersonId, filename, folder, album, tag, storage key or path. Several
// FileItems (even across owners) referencing the same blob share exactly one
// manifest; whether a given viewer may see any of them is decided later, by the
// read path, not here.
//
// PRIVATE VAULT. Eligibility requires at least one CURRENT, non-deleted,
// media-library-active reference. `_db.FileItems` carries the global
// PrivateVaultId == null query filter, so vaulted references are invisible to
// this query by construction: a blob referenced ONLY from the vault is skipped.
// A blob with both a normal and a vaulted reference is segmented once — and
// that manifest still cannot surface the vaulted file, because it names no
// FileItem at all.
public sealed class VideoSemanticSegmentationService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IVideoSemanticSegmenter _segmenter;
    private readonly IOptions<VideoSemanticSegmentationOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoSemanticSegmentationService> _logger;

    public VideoSemanticSegmentationService(
        AppDbContext db,
        IBlobService blobs,
        IVideoSemanticSegmenter segmenter,
        IOptions<VideoSemanticSegmentationOptions> options,
        TimeProvider clock,
        ILogger<VideoSemanticSegmentationService> logger)
    {
        _db = db;
        _blobs = blobs;
        _segmenter = segmenter;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public VideoSemanticSegmentationOptions Options => _options.Value;

    public async Task<VideoSemanticSegmentationOutcome> ProcessBlobAsync(
        Guid blobObjectId,
        int segmentationVersion,
        CancellationToken cancellationToken = default,
        Guid? jobId = null)
    {
        var options = _options.Value;
        var started = Stopwatch.GetTimestamp();

        // ---- idempotency (before any work) --------------------------------
        // A completed manifest is never rebuilt and never duplicated; a
        // permanent content skip is not retried. A FAILED row, or no row at
        // all, means this attempt may proceed. A different version never looks
        // at this row — old completed versions survive a reindex untouched.
        var existing = await _db.VideoSemanticIndexes
            .FirstOrDefaultAsync(
                i => i.BlobObjectId == blobObjectId && i.SegmentationVersion == segmentationVersion,
                cancellationToken);

        if (existing is not null && IsTerminal(existing))
        {
            return new VideoSemanticSegmentationOutcome(
                VideoSemanticSegmentationOutcomeKind.AlreadyTerminal,
                existing.ErrorCode,
                existing.SegmentCount,
                existing.SampleCount);
        }

        // ---- validate input ------------------------------------------------
        var metadata = await _db.BlobMetadata.AsNoTracking()
            .Where(m => m.BlobObjectId == blobObjectId)
            .Select(m => new MetadataProjection(
                m.MediaCategory, m.VideoExtractionStatus, m.DurationSeconds, m.VideoCodec))
            .FirstOrDefaultAsync(cancellationToken);

        var validation = Validate(metadata);
        if (validation is not null)
        {
            return await RecordOutcomeAsync(
                existing, blobObjectId, segmentationVersion, validation, started, jobId, cancellationToken);
        }

        // Eligibility is re-checked at RUN time, not only at scheduling time: a
        // file can be deleted, excluded from the media library, or moved into
        // the Private Vault between enqueue and execution.
        var hasEligibleReference = await _db.FileItems.AsNoTracking()
            .AnyAsync(
                f => f.BlobObjectId == blobObjectId
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active,
                cancellationToken);
        if (!hasEligibleReference)
        {
            return await RecordOutcomeAsync(
                existing, blobObjectId, segmentationVersion,
                VideoSemanticSegmentationOutcome.Skipped(VideoSemanticErrorCodes.NoEligibleReference),
                started, jobId, cancellationToken);
        }

        // Duration is authoritative: it comes from the persisted ffprobe result,
        // not from a second probe of our own.
        var durationMilliseconds = (long)Math.Round(metadata!.DurationSeconds!.Value * 1000d);

        // Both segment limits are hard: a video longer than
        // MaximumSegmentsPerVideo × MaximumSegmentSeconds cannot be segmented
        // without stretching some segment past the maximum, so it is skipped
        // permanently AT THIS VERSION before any FFmpeg work. A later version
        // with different limits gets its own row and may process it.
        if (durationMilliseconds > options.MaximumCapacityMilliseconds)
        {
            return await RecordOutcomeAsync(
                existing, blobObjectId, segmentationVersion,
                VideoSemanticSegmentationOutcome.Skipped(VideoSemanticErrorCodes.SegmentationCapacityExceeded),
                started, jobId, cancellationToken, durationMilliseconds);
        }

        // ---- detect + normalize --------------------------------------------
        VideoSemanticSegmentationOutcome outcome;
        VideoSemanticManifest? manifest = null;
        try
        {
            var detection = await _segmenter.DetectSceneCandidatesAsync(
                ct => _blobs.OpenContentAsync(blobObjectId, ct), cancellationToken);

            if (!detection.Succeeded)
            {
                outcome = VideoSemanticSegmentationOutcome.Failed(
                    detection.ErrorCode ?? VideoSemanticErrorCodes.ApplicationBug);
                return await RecordOutcomeAsync(
                    existing, blobObjectId, segmentationVersion, outcome, started, jobId,
                    cancellationToken, durationMilliseconds, detection.ProcessExitCode);
            }

            manifest = VideoSemanticManifestBuilder.Build(
                durationMilliseconds, detection.CandidateSeconds, options);
        }
        catch (OperationCanceledException)
        {
            // Cancellation stays cancellation: no row is written, no attempt is
            // counted, nothing is marked failed or skipped.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-segments: normalization raised an unexpected {ExceptionType}.", ex.GetType().Name);
            return await RecordOutcomeAsync(
                existing, blobObjectId, segmentationVersion,
                VideoSemanticSegmentationOutcome.Failed(VideoSemanticErrorCodes.ApplicationBug),
                started, jobId, cancellationToken);
        }

        // ---- persist atomically ---------------------------------------------
        try
        {
            await PersistAsync(existing, blobObjectId, segmentationVersion, manifest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever failed, the transaction rolled back: no half-written
            // manifest can be observed as completed.
            _logger.LogWarning(
                "video-segments: manifest persistence failed ({ExceptionType}).", ex.GetType().Name);
            _db.ChangeTracker.Clear();
            var code = ex is DbUpdateException
                ? VideoSemanticErrorCodes.Database
                : VideoSemanticErrorCodes.ApplicationBug;
            return await RecordOutcomeAsync(
                existing: null, blobObjectId, segmentationVersion,
                VideoSemanticSegmentationOutcome.Failed(code), started, jobId, cancellationToken);
        }

        outcome = new VideoSemanticSegmentationOutcome(
            VideoSemanticSegmentationOutcomeKind.Completed,
            ErrorCode: null,
            manifest.SegmentCount,
            manifest.SampleCount,
            manifest.CandidateCount,
            manifest.FallbackUsed);

        LogAttempt(jobId, segmentationVersion, durationMilliseconds, outcome, processExitCode: 0, started);
        return outcome;
    }

    // ---- validation --------------------------------------------------------

    private sealed record MetadataProjection(
        string MediaCategory, string VideoExtractionStatus, double? DurationSeconds, string? VideoCodec);

    // Returns null when the blob is a valid segmentation target, otherwise the
    // classified outcome. The content/environment split is deliberate: a blob
    // whose metadata has not been probed yet is a RETRY (scheduling ran early),
    // never a permanent skip.
    private static VideoSemanticSegmentationOutcome? Validate(MetadataProjection? metadata)
    {
        if (metadata is null)
        {
            return VideoSemanticSegmentationOutcome.Failed(VideoSemanticErrorCodes.MetadataMissing);
        }

        if (metadata.MediaCategory != MediaCategories.Video)
        {
            return VideoSemanticSegmentationOutcome.Skipped(VideoSemanticErrorCodes.UnsupportedInput);
        }

        if (metadata.VideoExtractionStatus != MetadataStatuses.Completed)
        {
            return VideoSemanticSegmentationOutcome.Failed(VideoSemanticErrorCodes.MetadataMissing);
        }

        if (string.IsNullOrWhiteSpace(metadata.VideoCodec))
        {
            return VideoSemanticSegmentationOutcome.Skipped(VideoSemanticErrorCodes.NoVideoStream);
        }

        var seconds = metadata.DurationSeconds;
        if (seconds is not double value || !double.IsFinite(value) || value <= 0
            || Math.Round(value * 1000d) < 1)
        {
            return VideoSemanticSegmentationOutcome.Skipped(VideoSemanticErrorCodes.InvalidDuration);
        }

        return null;
    }

    private static bool IsTerminal(VideoSemanticIndex index)
        => index.Status == AiArtifactStatuses.Completed
            || (index.Status == AiArtifactStatuses.Skipped && index.IsPermanentFailure);

    // ---- persistence -------------------------------------------------------

    // Writes the manifest head and its full segment/sample tree inside ONE
    // transaction. A retry first removes the previous non-terminal attempt for
    // the SAME version (children cascade); other versions are never touched.
    private async Task PersistAsync(
        VideoSemanticIndex? existing,
        Guid blobObjectId,
        int segmentationVersion,
        VideoSemanticManifest manifest,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var attemptCount = (existing?.AttemptCount ?? 0) + 1;
        var createdAt = existing?.CreatedAt ?? now;

        var owned = _db.Database.CurrentTransaction is null;
        var transaction = owned ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            if (existing is not null)
            {
                // Delete-then-insert rather than in-place patching: the segment
                // and sample ordinals of the previous attempt have no relation
                // to the new ones, and the FK cascade makes the removal exact.
                _db.VideoSemanticIndexes.Remove(existing);
                await _db.SaveChangesAsync(cancellationToken);
            }

            var index = new VideoSemanticIndex
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobObjectId,
                SegmentationVersion = segmentationVersion,
                Status = AiArtifactStatuses.Completed,
                ErrorCode = null,
                IsPermanentFailure = false,
                AttemptCount = attemptCount,
                DurationMilliseconds = manifest.DurationMilliseconds,
                SegmentCount = manifest.SegmentCount,
                SampleCount = manifest.SampleCount,
                CreatedAt = createdAt,
                UpdatedAt = now,
                CompletedAt = now,
            };
            _db.VideoSemanticIndexes.Add(index);

            foreach (var segment in manifest.Segments)
            {
                var segmentId = Guid.NewGuid();
                _db.VideoSemanticSegments.Add(new VideoSemanticSegment
                {
                    Id = segmentId,
                    VideoSemanticIndexId = index.Id,
                    SegmentIndex = segment.SegmentIndex,
                    StartMilliseconds = segment.StartMilliseconds,
                    EndMilliseconds = segment.EndMilliseconds,
                    BoundaryReason = segment.BoundaryReason,
                    CreatedAt = now,
                });

                foreach (var sample in segment.Samples)
                {
                    _db.VideoSemanticSamples.Add(new VideoSemanticSample
                    {
                        Id = Guid.NewGuid(),
                        VideoSemanticSegmentId = segmentId,
                        SampleIndex = sample.SampleIndex,
                        TimestampMilliseconds = sample.TimestampMilliseconds,
                        SelectionReason = sample.SelectionReason,
                        CreatedAt = now,
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    // Upserts the manifest HEAD for a non-completed outcome: a permanent
    // content skip, or a retryable failure. Never writes children.
    private async Task<VideoSemanticSegmentationOutcome> RecordOutcomeAsync(
        VideoSemanticIndex? existing,
        Guid blobObjectId,
        int segmentationVersion,
        VideoSemanticSegmentationOutcome outcome,
        long startedTimestamp,
        Guid? jobId,
        CancellationToken cancellationToken,
        long? durationMilliseconds = null,
        int? processExitCode = null)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var isSkip = outcome.Kind == VideoSemanticSegmentationOutcomeKind.Skipped;

        var row = existing;
        if (row is null)
        {
            row = await _db.VideoSemanticIndexes.FirstOrDefaultAsync(
                i => i.BlobObjectId == blobObjectId && i.SegmentationVersion == segmentationVersion,
                cancellationToken);
        }

        if (row is null)
        {
            row = new VideoSemanticIndex
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobObjectId,
                SegmentationVersion = segmentationVersion,
                CreatedAt = now,
            };
            _db.VideoSemanticIndexes.Add(row);
        }

        row.Status = isSkip ? AiArtifactStatuses.Skipped : AiArtifactStatuses.Failed;
        row.ErrorCode = outcome.ErrorCode;
        // Only a CONTENT skip is permanent. A missing provider, a timeout, a
        // full disk or a database hiccup must stay retryable forever.
        row.IsPermanentFailure = isSkip;
        row.AttemptCount += 1;
        row.SegmentCount = 0;
        row.SampleCount = 0;
        row.DurationMilliseconds = null;
        row.UpdatedAt = now;
        row.CompletedAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            // Recording the outcome must not escalate into a job crash: the
            // candidate query will simply offer this blob again.
            _logger.LogWarning(
                "video-segments: outcome row could not be persisted ({ExceptionType}).", ex.GetType().Name);
            _db.ChangeTracker.Clear();
            return VideoSemanticSegmentationOutcome.Failed(VideoSemanticErrorCodes.Database);
        }

        LogAttempt(jobId, segmentationVersion, durationMilliseconds, outcome, processExitCode, startedTimestamp);
        return outcome;
    }

    // ---- observability -----------------------------------------------------

    // Counts, codes and elapsed time only. Never a filename, a temp path, a
    // storage key, a signed URL, raw FFmpeg output or media content. The blob
    // id is deliberately absent too — it identifies content and is a
    // no-leak-listed value.
    private void LogAttempt(
        Guid? jobId, int segmentationVersion, long? durationMilliseconds,
        VideoSemanticSegmentationOutcome outcome, int? processExitCode, long startedTimestamp)
    {
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        var retryable = outcome.Kind == VideoSemanticSegmentationOutcomeKind.Failed;

        _logger.LogInformation(
            "video-segments: operation={Operation} job={JobId} version={SegmentationVersion} "
            + "attempt-outcome={Outcome} duration-ms={DurationMs} candidates={Candidates} "
            + "segments={Segments} samples={Samples} fallback={Fallback} elapsed-ms={ElapsedMs} "
            + "failure={FailureCode} retryable={Retryable} exit-code={ExitCode}",
            "video.semantic.segmentation",
            jobId,
            segmentationVersion,
            outcome.Kind,
            durationMilliseconds,
            outcome.CandidateCount,
            outcome.SegmentCount,
            outcome.SampleCount,
            outcome.FallbackUsed,
            elapsedMs,
            outcome.ErrorCode,
            retryable,
            processExitCode);
    }
}

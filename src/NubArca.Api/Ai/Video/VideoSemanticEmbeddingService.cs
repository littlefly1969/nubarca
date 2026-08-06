using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Video;

// VSEM-02: embeds the temporal samples of ONE video blob at ONE segmentation
// version under ONE AI profile.
//
// Responsibilities kept here: manifest/profile eligibility, per-sample
// idempotency and retry, failure isolation, canonical persistence, best-effort
// vector synchronization, aggregate status. Frame extraction lives in
// IVideoSemanticFrameExtractor; inference in the resolved IImageEmbedder (the
// SAME image tower and profile the photo index uses, so video samples share
// the photo embedding space and its paired text tower).
//
// FAILURE ISOLATION. Each sample is an independent unit: a failed frame or a
// failed inference writes a retryable failed row for THAT sample and never
// discards other samples' completed embeddings. A rerun processes only failed
// or missing samples. pgvector synchronization is strictly best-effort — a
// sync failure leaves the canonical row intact and retryable-by-sync, never
// unusable.
//
// BLOB-LEVEL AND OWNER-FREE, like everything in this substrate: nothing here
// reads or stores OwnerUserId/FileItemId; eligibility is only "at least one
// current, non-deleted, media-library-active reference exists" (the global
// Private-Vault query filter keeps vault-only blobs out by construction).
public sealed class VideoSemanticEmbeddingService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IVideoSemanticFrameExtractor _extractor;
    private readonly IAiVectorSerializer _serializer;
    private readonly VideoSemanticSampleVectorIndexService _vectors;
    private readonly IOptions<VideoVisualEmbeddingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoSemanticEmbeddingService> _logger;

    public VideoSemanticEmbeddingService(
        AppDbContext db,
        IBlobService blobs,
        IVideoSemanticFrameExtractor extractor,
        IAiVectorSerializer serializer,
        VideoSemanticSampleVectorIndexService vectors,
        IOptions<VideoVisualEmbeddingOptions> options,
        TimeProvider clock,
        ILogger<VideoSemanticEmbeddingService> logger)
    {
        _db = db;
        _blobs = blobs;
        _extractor = extractor;
        _serializer = serializer;
        _vectors = vectors;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<VideoSemanticEmbeddingOutcome> ProcessBlobAsync(
        IImageEmbedder embedder,
        AiProfile profile,
        Guid blobObjectId,
        int segmentationVersion,
        CancellationToken cancellationToken = default,
        Guid? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(profile);
        var started = Stopwatch.GetTimestamp();

        // Profile guards. These are operator/config states, never blob
        // outcomes: nothing is written for them.
        if (!string.Equals(profile.Capability, AiCapabilities.ImageEmbedding, StringComparison.Ordinal))
        {
            return VideoSemanticEmbeddingOutcome.NotEligible(AiUnavailableReasons.CapabilityMismatch);
        }

        if (profile.Dimension is not > 0)
        {
            return VideoSemanticEmbeddingOutcome.NotEligible(AiUnavailableReasons.ProfileDimensionInvalid);
        }

        // Only samples of a COMPLETED temporal manifest are eligible. A
        // missing or non-completed manifest is premature scheduling — no row
        // is written (implicit pending), the candidate query will re-offer the
        // blob once segmentation lands.
        var index = await _db.VideoSemanticIndexes.AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.BlobObjectId == blobObjectId && i.SegmentationVersion == segmentationVersion,
                cancellationToken);
        if (index is null || index.Status != AiArtifactStatuses.Completed)
        {
            return VideoSemanticEmbeddingOutcome.NotEligible("manifest_not_completed");
        }

        // Idempotency: a terminal aggregate is never rebuilt.
        var aggregate = await _db.VideoSemanticEmbeddingStatuses
            .FirstOrDefaultAsync(
                s => s.VideoSemanticIndexId == index.Id && s.ProfileId == profile.Id,
                cancellationToken);
        if (aggregate is not null
            && (aggregate.Status == VideoSemanticEmbeddingStatuses.Completed
                || aggregate.Status == VideoSemanticEmbeddingStatuses.Skipped))
        {
            return VideoSemanticEmbeddingOutcome.AlreadyTerminal(aggregate);
        }

        // Eligibility is re-checked at RUN time — a file can be deleted,
        // excluded or vaulted between enqueue and execution.
        var hasEligibleReference = await _db.FileItems.AsNoTracking()
            .AnyAsync(
                f => f.BlobObjectId == blobObjectId
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active,
                cancellationToken);
        if (!hasEligibleReference)
        {
            var skipped = await WriteAggregateAsync(
                aggregate, index, profile, VideoSemanticEmbeddingStatuses.Skipped,
                expected: index.SampleCount, completed: 0, failed: 0,
                VideoSemanticErrorCodes.NoEligibleReference, cancellationToken);
            LogAttempt(jobId, segmentationVersion, profile.Key, skipped, 0, 0, 0, 0, started);
            return VideoSemanticEmbeddingOutcome.From(skipped);
        }

        // ---- sample inventory ---------------------------------------------
        var samples = await (
            from segment in _db.VideoSemanticSegments.AsNoTracking()
            where segment.VideoSemanticIndexId == index.Id
            join sample in _db.VideoSemanticSamples.AsNoTracking()
                on segment.Id equals sample.VideoSemanticSegmentId
            orderby segment.SegmentIndex, sample.SampleIndex
            select new { sample.Id, sample.TimestampMilliseconds })
            .ToListAsync(cancellationToken);

        var sampleIds = samples.Select(s => s.Id).ToList();
        var existing = await _db.VideoSemanticSampleEmbeddings
            .Where(e => e.ProfileId == profile.Id && sampleIds.Contains(e.VideoSemanticSampleId))
            .ToDictionaryAsync(e => e.VideoSemanticSampleId, cancellationToken);

        // Retry ONLY failed or missing samples; completed rows are preserved.
        var pending = samples
            .Where(s => !existing.TryGetValue(s.Id, out var row)
                || row.Status != AiArtifactStatuses.Completed)
            .Select(s => new VideoSemanticFrameRequest(s.Id, s.TimestampMilliseconds))
            .ToList();

        var extractionFailures = 0;
        var inferenceFailures = 0;
        var vectorSynced = 0;
        var vectorDeferred = 0;

        if (pending.Count > 0)
        {
            // Frame resolution is THIS pipeline's setting: the SigLIP2 tower's
            // input edge decides it, never the face-analysis configuration.
            var batch = await _extractor.ExtractFramesAsync(
                ct => _blobs.OpenContentAsync(blobObjectId, ct), pending,
                _options.Value.FrameMaxEdge, cancellationToken);

            if (!batch.Staged)
            {
                // The blob could not even be staged: a batch-level retryable
                // failure. Per-sample rows are untouched.
                var failedAggregate = await WriteAggregateAsync(
                    aggregate, index, profile, VideoSemanticEmbeddingStatuses.Failed,
                    expected: samples.Count,
                    completed: existing.Values.Count(r => r.Status == AiArtifactStatuses.Completed),
                    failed: existing.Values.Count(r => r.Status == AiArtifactStatuses.Failed),
                    batch.StagingErrorCode, cancellationToken);
                LogAttempt(jobId, segmentationVersion, profile.Key, failedAggregate, 0, 0, 0, 0, started);
                return VideoSemanticEmbeddingOutcome.From(failedAggregate);
            }

            foreach (var frame in batch.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!frame.Succeeded)
                {
                    extractionFailures++;
                    await UpsertSampleAsync(
                        existing, frame.SampleId, profile, vector: null,
                        frame.ErrorCode ?? VideoSemanticErrorCodes.FrameExtraction, cancellationToken);
                    continue;
                }

                var (vector, errorCode) = await EmbedFrameAsync(
                    embedder, profile, frame.ImageBytes!, cancellationToken);
                if (vector is null)
                {
                    inferenceFailures++;
                    await UpsertSampleAsync(existing, frame.SampleId, profile, null, errorCode, cancellationToken);
                    continue;
                }

                var row = await UpsertSampleAsync(
                    existing, frame.SampleId, profile, vector, errorCode: null, cancellationToken);
                if (row is null)
                {
                    inferenceFailures++;
                    continue;
                }

                // Canonical row committed; mirror best-effort into pgvector.
                // A failure here NEVER affects the canonical outcome.
                try
                {
                    var sync = await _vectors.SyncEmbeddingAsync(
                        row.Id, frame.SampleId, profile.Id, vector, profile.Dimension, cancellationToken);
                    if (sync == VectorUpsertOutcome.Indexed) vectorSynced++;
                    else if (sync == VectorUpsertOutcome.Failed) vectorDeferred++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    vectorDeferred++;
                }
            }
        }

        // ---- aggregate -----------------------------------------------------
        var completedCount = existing.Values.Count(r => r.Status == AiArtifactStatuses.Completed);
        var failedCount = existing.Values.Count(r => r.Status == AiArtifactStatuses.Failed);
        var status = completedCount == samples.Count
            ? VideoSemanticEmbeddingStatuses.Completed
            : completedCount > 0
                ? VideoSemanticEmbeddingStatuses.Partial
                : VideoSemanticEmbeddingStatuses.Failed;
        var aggregateError = status == VideoSemanticEmbeddingStatuses.Completed
            ? null
            : extractionFailures >= inferenceFailures && extractionFailures > 0
                ? VideoSemanticErrorCodes.FrameExtraction
                : inferenceFailures > 0
                    ? VideoSemanticErrorCodes.ProviderInference
                    : VideoSemanticErrorCodes.ApplicationBug;

        var updated = await WriteAggregateAsync(
            aggregate, index, profile, status, samples.Count, completedCount, failedCount,
            aggregateError, cancellationToken);

        LogAttempt(
            jobId, segmentationVersion, profile.Key, updated,
            extractionFailures, inferenceFailures, vectorSynced, vectorDeferred, started);
        return VideoSemanticEmbeddingOutcome.From(updated, vectorSynced, vectorDeferred);
    }

    // ---- inference ---------------------------------------------------------

    private async Task<(float[]? Vector, string? ErrorCode)> EmbedFrameAsync(
        IImageEmbedder embedder, AiProfile profile, byte[] frameBytes,
        CancellationToken cancellationToken)
    {
        AiEmbeddingResult embedding;
        try
        {
            embedding = await embedder.EmbedImageAsync(frameBytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-embed: inference raised {ExceptionType}.", ex.GetType().Name);
            return (null, VideoSemanticErrorCodes.ProviderInference);
        }

        var expectedDimension = profile.Dimension!.Value;
        return PhotoVectorIndexService.ClassifyVector(embedding.Vector, expectedDimension) switch
        {
            VectorRowValidity.DimensionMismatch
                => (null, VideoSemanticErrorCodes.EmbeddingDimensionMismatch),
            VectorRowValidity.NonFinite
                => (null, VideoSemanticErrorCodes.EmbeddingInvalidVector),
            _ => (embedding.Vector, null),
        };
    }

    // ---- persistence -------------------------------------------------------

    // Upserts ONE per-sample row and commits it immediately (one sample = one
    // unit of work, so a crash or cancellation costs at most the sample in
    // flight). A non-null vector writes a completed row; null writes a failed
    // row carrying the sanitized code. Returns the row, or null when a unique
    // race made another run the owner.
    private async Task<VideoSemanticSampleEmbedding?> UpsertSampleAsync(
        Dictionary<Guid, VideoSemanticSampleEmbedding> existing,
        Guid sampleId,
        AiProfile profile,
        float[]? vector,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var isNew = !existing.TryGetValue(sampleId, out var row);
        if (isNew)
        {
            row = new VideoSemanticSampleEmbedding
            {
                Id = Guid.NewGuid(),
                VideoSemanticSampleId = sampleId,
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            _db.VideoSemanticSampleEmbeddings.Add(row);
            existing[sampleId] = row!;
        }

        if (vector is not null)
        {
            row!.EmbeddingBytes = _serializer.Serialize(vector, profile.Dimension!.Value);
            row.Dimension = profile.Dimension.Value;
            row.Status = AiArtifactStatuses.Completed;
            row.ErrorCode = null;
            row.CompletedAt = now;
        }
        else
        {
            row!.EmbeddingBytes = Array.Empty<byte>();
            row.Dimension = 0;
            row.Status = AiArtifactStatuses.Failed;
            row.ErrorCode = errorCode;
            row.CompletedAt = null;
        }

        row.AttemptCount += 1;
        row.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return row;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException)
        {
            // Unique (sample, profile) race: a concurrent run already owns this
            // sample. Detach our copy and defer to the committed row.
            _db.Entry(row!).State = EntityState.Detached;
            if (isNew)
            {
                existing.Remove(sampleId);
            }

            var committed = await _db.VideoSemanticSampleEmbeddings.AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.VideoSemanticSampleId == sampleId && e.ProfileId == profile.Id,
                    cancellationToken);
            if (committed is not null)
            {
                existing[sampleId] = committed;
            }

            return null;
        }
    }

    private async Task<VideoSemanticEmbeddingStatus> WriteAggregateAsync(
        VideoSemanticEmbeddingStatus? aggregate,
        VideoSemanticIndex index,
        AiProfile profile,
        string status,
        int expected,
        int completed,
        int failed,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (aggregate is null)
        {
            aggregate = new VideoSemanticEmbeddingStatus
            {
                Id = Guid.NewGuid(),
                VideoSemanticIndexId = index.Id,
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            _db.VideoSemanticEmbeddingStatuses.Add(aggregate);
        }

        aggregate.Status = status;
        aggregate.ExpectedSampleCount = expected;
        aggregate.CompletedSampleCount = completed;
        aggregate.FailedSampleCount = failed;
        aggregate.ErrorCode = errorCode;
        aggregate.AttemptCount += 1;
        aggregate.UpdatedAt = now;
        aggregate.CompletedAt = status == VideoSemanticEmbeddingStatuses.Completed ? now : null;

        await _db.SaveChangesAsync(cancellationToken);
        return aggregate;
    }

    // ---- observability -----------------------------------------------------

    // Counts, codes, the profile's stable key and elapsed time only. Never a
    // blob id, sample id, filename, temp path, vector or frame byte.
    private void LogAttempt(
        Guid? jobId, int segmentationVersion, string profileKey,
        VideoSemanticEmbeddingStatus aggregate,
        int extractionFailures, int inferenceFailures, int vectorSynced, int vectorDeferred,
        long startedTimestamp)
    {
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "video-embed: operation={Operation} job={JobId} version={SegmentationVersion} "
            + "profile={ProfileKey} status={Status} samples={Expected} completed={Completed} "
            + "failed={Failed} extract-failed={ExtractionFailures} infer-failed={InferenceFailures} "
            + "vector-synced={VectorSynced} vector-deferred={VectorDeferred} "
            + "failure={FailureCode} attempts={Attempts} elapsed-ms={ElapsedMs}",
            "video.semantic.embedding",
            jobId,
            segmentationVersion,
            profileKey,
            aggregate.Status,
            aggregate.ExpectedSampleCount,
            aggregate.CompletedSampleCount,
            aggregate.FailedSampleCount,
            extractionFailures,
            inferenceFailures,
            vectorSynced,
            vectorDeferred,
            aggregate.ErrorCode,
            aggregate.AttemptCount,
            elapsedMs);
    }
}

public enum VideoSemanticEmbeddingOutcomeKind
{
    // Every required sample has a completed embedding.
    Completed,

    // At least one completed and at least one failed/missing sample.
    Partial,

    // Processing ran and produced no usable sample embedding.
    Failed,

    // A permanent content-level skip (e.g. no eligible reference).
    Skipped,

    // A terminal aggregate for this (manifest, profile) already existed.
    AlreadyTerminal,

    // Nothing was processed and nothing was written: the manifest is not
    // completed yet, or the profile cannot host image embeddings.
    NotEligible,
}

// The result of ONE embedding attempt on ONE blob. Counts and sanitized codes
// only — no vectors, no identifiers beyond what the caller already holds.
public sealed record VideoSemanticEmbeddingOutcome(
    VideoSemanticEmbeddingOutcomeKind Kind,
    string? ErrorCode = null,
    int ExpectedSampleCount = 0,
    int CompletedSampleCount = 0,
    int FailedSampleCount = 0,
    int VectorSynced = 0,
    int VectorDeferred = 0)
{
    public static VideoSemanticEmbeddingOutcome NotEligible(string reason)
        => new(VideoSemanticEmbeddingOutcomeKind.NotEligible, reason);

    public static VideoSemanticEmbeddingOutcome AlreadyTerminal(VideoSemanticEmbeddingStatus aggregate)
        => new(
            VideoSemanticEmbeddingOutcomeKind.AlreadyTerminal, aggregate.ErrorCode,
            aggregate.ExpectedSampleCount, aggregate.CompletedSampleCount, aggregate.FailedSampleCount);

    public static VideoSemanticEmbeddingOutcome From(
        VideoSemanticEmbeddingStatus aggregate, int vectorSynced = 0, int vectorDeferred = 0)
        => new(
            aggregate.Status switch
            {
                VideoSemanticEmbeddingStatuses.Completed => VideoSemanticEmbeddingOutcomeKind.Completed,
                VideoSemanticEmbeddingStatuses.Partial => VideoSemanticEmbeddingOutcomeKind.Partial,
                VideoSemanticEmbeddingStatuses.Skipped => VideoSemanticEmbeddingOutcomeKind.Skipped,
                _ => VideoSemanticEmbeddingOutcomeKind.Failed,
            },
            aggregate.ErrorCode,
            aggregate.ExpectedSampleCount,
            aggregate.CompletedSampleCount,
            aggregate.FailedSampleCount,
            vectorSynced,
            vectorDeferred);
}

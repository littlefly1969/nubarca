using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Faces;

// Face Substrate v0: the real body of ai.faces.embeddings.backfill. For each blob
// that has FaceDetection rows lacking a FaceEmbedding (for the active profile),
// it decodes the image ONCE, aligns+recognizes each pending face, and writes one
// FaceEmbedding row per face plus a best-effort pgvector row. Keyset-paged by
// BlobObjectId, sliceable/checkpointed/cancellable, idempotent (unique
// (FaceDetectionId, ProfileId)).
//
// PRIVACY: a candidate blob must still be referenced by an active, NON-VAULT
// FileItem (the FileItems query carries the global Private-Vault filter), so a
// detection whose only referencing file was moved into the vault is not embedded.
// Only detections WITH landmarks are candidates (recognition needs alignment).
public sealed class FaceEmbeddingBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IAiVectorSerializer _serializer;
    private readonly FaceVectorIndexService _vectors;
    private readonly TimeProvider _clock;

    public FaceEmbeddingBackfillService(
        AppDbContext db, IBlobService blobs, IAiVectorSerializer serializer,
        FaceVectorIndexService vectors, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _serializer = serializer;
        _vectors = vectors;
        _clock = clock;
    }

    public async Task<FaceBackfillResult> RunAsync(
        IFaceEmbedder embedder,
        AiProfile profile,
        FaceBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var profileId = profile.Id;

        if (options.DryRun)
        {
            var pending = await CandidateBlobQuery(profileId, options.TargetBlobObjectId).CountAsync(cancellationToken);
            if (options.Limit is int lim && pending > lim)
            {
                pending = lim;
            }
            log?.Invoke($"ai faces embeddings (dry-run): {pending} blob(s) with faces would be embedded.");
            return FaceBackfillResult.Dry();
        }

        var checkpoint = FaceBackfillCheckpoint.TryParse(checkpointJson) ?? FaceBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId ?? Guid.Empty;
        var processedTotal = checkpoint.ProcessedTotal; // blobs
        var producedTotal = checkpoint.ProducedTotal;   // embeddings written
        var skippedTotal = checkpoint.SkippedTotal;
        var failedTotal = checkpoint.FailedTotal;

        var processedSlice = 0;
        var producedSlice = 0;
        var skippedSlice = 0;
        var failedSlice = 0;
        var vectorIndexedSlice = 0;
        var vectorDeferredSlice = 0;
        long yieldCounter = 0;
        var yielded = false;
        var exhausted = false;

        bool LimitReached() => options.Limit is int gl && producedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(producedTotal, null,
                    $"embedding faces ({producedTotal} embeddings, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateBlobQuery(profileId, options.TargetBlobObjectId)
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

                var outcome = await EmbedBlobAsync(embedder, profile, blobId, cancellationToken);
                producedTotal += outcome.Produced; producedSlice += outcome.Produced;
                skippedTotal += outcome.Skipped; skippedSlice += outcome.Skipped;
                failedTotal += outcome.Failed; failedSlice += outcome.Failed;
                vectorIndexedSlice += outcome.VectorIndexed;
                vectorDeferredSlice += outcome.VectorDeferred;

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
            $"ai faces embeddings: {(moreWork ? "yielded" : "done")} — blobs {processedSlice} "
            + $"(embeddings {producedSlice}, skipped {skippedSlice}, failed {failedSlice}; total embeddings {producedTotal}; "
            + $"vectors indexed {vectorIndexedSlice}, vector-deferred {vectorDeferredSlice}).");

        return new FaceBackfillResult(
            processedSlice, producedSlice, skippedSlice, failedSlice, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson,
            ProcessedTotal: processedTotal, ProducedTotal: producedTotal,
            SkippedTotal: skippedTotal, FailedTotal: failedTotal,
            VectorIndexed: vectorIndexedSlice, VectorDeferred: vectorDeferredSlice);
    }

    private readonly record struct BlobEmbedOutcome(int Produced, int Skipped, int Failed, int VectorIndexed, int VectorDeferred);

    // Per-blob embedding: decode ONCE, then process each FaceDetection in ISOLATION.
    // One face failing (alignment/recognition/serialize/vector) never blocks the
    // others, and every attempted face is persisted with an explicit status so it
    // is never confused with a never-attempted (missing) face. Only a whole-image
    // failure (unreadable bytes / batch timeout / decode) marks ALL pending faces
    // with a shared transient reason.
    private async Task<BlobEmbedOutcome> EmbedBlobAsync(
        IFaceEmbedder embedder, AiProfile profile, Guid blobId, CancellationToken cancellationToken)
    {
        // Pending = landmarked faces with NO terminal (completed/skipped) row.
        // A prior FAILED (transient) row is retried.
        var pending = await _db.FaceDetections.AsNoTracking()
            .Where(d => d.BlobObjectId == blobId
                && d.ProfileId == profile.Id
                && d.LandmarksJson != null
                && !_db.FaceEmbeddings.Any(e => e.FaceDetectionId == d.Id && e.ProfileId == profile.Id
                    && (e.EmbeddingStatus == AiArtifactStatuses.Completed
                        || e.EmbeddingStatus == AiArtifactStatuses.Skipped)))
            .OrderBy(d => d.FaceIndex)
            .Select(d => new PendingFace(d.Id, d.LandmarksJson))
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return new BlobEmbedOutcome(0, 0, 0, 0, 0);
        }

        var pendingIds = pending.Select(p => p.Id).ToList();
        // Tracked existing rows (transient failures being retried) for update.
        var existing = (await _db.FaceEmbeddings
                .Where(e => e.ProfileId == profile.Id && pendingIds.Contains(e.FaceDetectionId))
                .ToListAsync(cancellationToken))
            .ToDictionary(e => e.FaceDetectionId);

        var now = _clock.GetUtcNow().UtcDateTime;

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
            // Whole-image bytes unreadable → all pending faces FAILED (transient,
            // shared reason). Retryable on a later run.
            var f = await MarkAllPendingAsync(
                pending, existing, profile, blobId,
                AiArtifactStatuses.Failed, FaceEmbeddingErrorCodes.Unknown, now, cancellationToken);
            return new BlobEmbedOutcome(0, 0, f, 0, 0);
        }

        var landmarks = pending
            .Select(p => (IReadOnlyList<Backends.FaceLandmark>)(FaceLandmarksJson.Deserialize(p.LandmarksJson) ?? Array.Empty<Backends.FaceLandmark>()))
            .ToList();

        IReadOnlyList<FaceEmbedAttempt> attempts;
        try
        {
            attempts = await ComputeAttemptsAsync(embedder, profile, bytes, landmarks, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Whole-image decode / batch timeout / unexpected → all pending FAILED
            // (transient, shared reason). Never marks individual faces skipped.
            var f = await MarkAllPendingAsync(
                pending, existing, profile, blobId,
                AiArtifactStatuses.Failed, FaceEmbeddingErrorCodes.Unknown, now, cancellationToken);
            return new BlobEmbedOutcome(0, 0, f, 0, 0);
        }

        int produced = 0, skipped = 0, failed = 0, vectorIndexed = 0, vectorDeferred = 0;
        var positiveDim = PositiveDimension(profile);

        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detectionId = pending[i].Id;
            existing.TryGetValue(detectionId, out var row);
            var attempt = attempts[i];

            switch (attempt.Outcome)
            {
                case FaceEmbedOutcome.Ok when attempt.Embedding is { } embedding:
                {
                    var dim = profile.Dimension is > 0 ? profile.Dimension.Value : embedding.Dimension;
                    byte[] embeddingBytes;
                    try
                    {
                        embeddingBytes = _serializer.Serialize(embedding.Vector, dim);
                    }
                    catch
                    {
                        if (await PersistAsync(row, detectionId, profile.Id, positiveDim,
                            AiArtifactStatuses.Failed, FaceEmbeddingErrorCodes.Unknown, Array.Empty<byte>(), now, cancellationToken) is { } _)
                        {
                            failed++;
                        }
                        continue;
                    }

                    var persisted = await PersistAsync(
                        row, detectionId, profile.Id, dim,
                        AiArtifactStatuses.Completed, errorCode: null, embeddingBytes, now, cancellationToken);
                    if (persisted is not { } completedRowId)
                    {
                        // Unique race: another run completed it.
                        continue;
                    }

                    produced++;

                    VectorUpsertOutcome vector;
                    try
                    {
                        vector = await _vectors.TryUpsertFaceVectorAsync(
                            completedRowId, detectionId, blobId, profile.Id, embedding.Vector, dim, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        vector = VectorUpsertOutcome.Failed;
                    }

                    if (vector == VectorUpsertOutcome.Indexed) vectorIndexed++;
                    else if (vector == VectorUpsertOutcome.Failed) vectorDeferred++;
                    break;
                }

                case FaceEmbedOutcome.AlignmentInvalid:
                    if (await PersistAsync(row, detectionId, profile.Id, positiveDim,
                        AiArtifactStatuses.Skipped, FaceEmbeddingErrorCodes.AlignmentInvalid, Array.Empty<byte>(), now, cancellationToken) is not null)
                    {
                        skipped++;
                    }
                    break;

                case FaceEmbedOutcome.CropInvalid:
                    if (await PersistAsync(row, detectionId, profile.Id, positiveDim,
                        AiArtifactStatuses.Skipped, FaceEmbeddingErrorCodes.CropInvalid, Array.Empty<byte>(), now, cancellationToken) is not null)
                    {
                        skipped++;
                    }
                    break;

                default: // RecognitionFailed (or Ok with a null embedding, defensively)
                    if (await PersistAsync(row, detectionId, profile.Id, positiveDim,
                        AiArtifactStatuses.Failed, FaceEmbeddingErrorCodes.RecognitionFailed, Array.Empty<byte>(), now, cancellationToken) is not null)
                    {
                        failed++;
                    }
                    break;
            }
        }

        return new BlobEmbedOutcome(produced, skipped, failed, vectorIndexed, vectorDeferred);
    }

    // Update-or-insert a face embedding row with the given terminal/transient
    // state, saving THIS face independently so a later face's failure can never
    // roll back earlier successes. Records ONE bounded aggregate diagnostic when a
    // face newly enters (or transitions into) a non-completed state. Returns the
    // row id, or null on a unique-race (a concurrent run handled the face).
    private async Task<Guid?> PersistAsync(
        FaceEmbedding? existing, Guid detectionId, Guid profileId, int dimension,
        string status, string? errorCode, byte[] bytes, DateTime now, CancellationToken cancellationToken)
    {
        var previousStatus = existing?.EmbeddingStatus;
        Guid rowId;
        FaceEmbedding? added = null;

        if (existing is not null)
        {
            existing.EmbeddingStatus = status;
            existing.ErrorCode = errorCode;
            existing.Dimension = dimension;
            existing.EmbeddingBytes = bytes;
            existing.AttemptCount += 1;
            existing.UpdatedAt = now;
            rowId = existing.Id;
        }
        else
        {
            added = new FaceEmbedding
            {
                Id = Guid.NewGuid(),
                FaceDetectionId = detectionId,
                ProfileId = profileId,
                EmbeddingBytes = bytes,
                Dimension = dimension,
                EmbeddingStatus = status,
                ErrorCode = errorCode,
                AttemptCount = 1,
                CreatedAt = now,
            };
            _db.FaceEmbeddings.Add(added);
            rowId = added.Id;
        }

        // Bounded aggregate diagnostic: only when newly non-completed or on a
        // status transition, so retries of a persistent failure don't inflate it.
        var recordDiagnostic = status != AiArtifactStatuses.Completed
            && (existing is null || previousStatus != status)
            && errorCode is not null;
        AiIndexDiagnostic? diagnostic = null;
        if (recordDiagnostic)
        {
            diagnostic = new AiIndexDiagnostic
            {
                Id = Guid.NewGuid(),
                Capability = AiCapabilities.FaceEmbedding,
                ProfileId = profileId,
                TargetKind = AiDiagnosticTargetKinds.FaceDetection,
                FaceDetectionId = detectionId,
                ErrorCode = errorCode!,
                IsPermanent = status == AiArtifactStatuses.Skipped,
                AttemptCount = existing?.AttemptCount ?? 1,
                OccurredAt = now,
            };
            _db.AiIndexDiagnostics.Add(diagnostic);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return rowId;
        }
        catch (DbUpdateException)
        {
            // Unique (FaceDetectionId, ProfileId) race with a concurrent run.
            if (added is not null) _db.Entry(added).State = EntityState.Detached;
            if (diagnostic is not null) _db.Entry(diagnostic).State = EntityState.Detached;
            return null;
        }
    }

    private async Task<int> MarkAllPendingAsync(
        IReadOnlyList<PendingFace> pending, IReadOnlyDictionary<Guid, FaceEmbedding> existing,
        AiProfile profile, Guid blobId, string status, string errorCode, DateTime now, CancellationToken cancellationToken)
    {
        var dim = PositiveDimension(profile);
        var count = 0;
        foreach (var p in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            existing.TryGetValue(p.Id, out var row);
            if (await PersistAsync(row, p.Id, profile.Id, dim, status, errorCode, Array.Empty<byte>(), now, cancellationToken) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private sealed record PendingFace(Guid Id, string? LandmarksJson);

    private static int PositiveDimension(AiProfile profile)
        => profile.Dimension is > 0 ? profile.Dimension.Value : FaceVectorIndexService.SupportedDimension;

    // Per-face attempts. Aligned path (ONNX) is already per-face resilient; the
    // fallback (deterministic test backend) is wrapped per face so one throw never
    // aborts the rest.
    private static async Task<IReadOnlyList<FaceEmbedAttempt>> ComputeAttemptsAsync(
        IFaceEmbedder embedder, AiProfile profile, byte[] bytes,
        IReadOnlyList<IReadOnlyList<Backends.FaceLandmark>> landmarks, CancellationToken cancellationToken)
    {
        if (embedder is IAlignedFaceEmbedder aligned)
        {
            return await aligned.EmbedAlignedFacesAsync(bytes, landmarks, profile, cancellationToken);
        }

        var outputs = new FaceEmbedAttempt[landmarks.Count];
        for (var i = 0; i < landmarks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var e = await embedder.EmbedFaceAsync(bytes, profile, cancellationToken);
                outputs[i] = FaceEmbedAttempt.Ok(e);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                outputs[i] = FaceEmbedAttempt.RecognitionFailed;
            }
        }

        return outputs;
    }

    // Blobs with >=1 pending (landmarked) face for this profile — no terminal
    // (completed/skipped) row; a prior transient FAILED row is retryable. Still
    // referenced by an active NON-VAULT FileItem.
    private IQueryable<BlobObject> CandidateBlobQuery(Guid profileId, Guid? targetBlobObjectId)
    {
        return _db.BlobObjects.AsNoTracking().Where(b =>
            (targetBlobObjectId == null || b.Id == targetBlobObjectId)
            &&
            _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            && _db.FaceDetections.Any(d =>
                d.BlobObjectId == b.Id
                && d.ProfileId == profileId
                && d.LandmarksJson != null
                && !_db.FaceEmbeddings.Any(e => e.FaceDetectionId == d.Id && e.ProfileId == profileId
                    && (e.EmbeddingStatus == AiArtifactStatuses.Completed
                        || e.EmbeddingStatus == AiArtifactStatuses.Skipped))));
    }
}

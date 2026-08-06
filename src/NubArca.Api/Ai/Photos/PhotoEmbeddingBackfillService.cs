using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;

namespace NubArca.Api.Ai.Photos;

// Phase 1 (photo similarity v0): the real body of `ai.photos.embeddings.backfill`.
// Indexes eligible image blobs by writing one BlobEmbedding row per
// (BlobObjectId, active image-embedding profile). Keyset-paged by BlobObjectId,
// sliceable/checkpointed/cancellable, and idempotent (already-indexed blobs drop
// out of the candidate query).
//
// Eligibility: a blob whose BlobMetadata.MediaCategory is "image" AND which is
// referenced by at least one active (non-deleted) FileItem. The BlobEmbedding
// row's existence IS the completion marker — Phase 1 deliberately does NOT write
// BlobAiArtifactStatus rows, so it never materializes pending rows and a missing
// embedding simply means "not indexed yet".
//
// The embedder is resolved by the caller (handler) and passed in, so this service
// never decides provider/availability; with AI disabled / a capability flag off /
// an unavailable provider it is simply never invoked.
public sealed class PhotoEmbeddingBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IAiVectorSerializer _serializer;
    private readonly PhotoVectorIndexService _vectors;
    private readonly TimeProvider _clock;

    public PhotoEmbeddingBackfillService(
        AppDbContext db, IBlobService blobs, IAiVectorSerializer serializer,
        PhotoVectorIndexService vectors, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _serializer = serializer;
        _vectors = vectors;
        _clock = clock;
    }

    public async Task<PhotoEmbeddingBackfillResult> RunAsync(
        IImageEmbedder embedder,
        AiProfile profile,
        PhotoEmbeddingBackfillOptions options,
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
            var pending = await CandidateQuery(profileId, cursor: null, options.TargetBlobObjectId).CountAsync(cancellationToken);
            if (options.Limit is int lim && pending > lim)
            {
                pending = lim;
            }
            log?.Invoke($"ai photos backfill (dry-run): {pending} image blob(s) would be indexed.");
            return new PhotoEmbeddingBackfillResult(0, 0, 0, 0, DryRun: true);
        }

        var checkpoint = PhotoEmbeddingBackfillCheckpoint.TryParse(checkpointJson)
            ?? PhotoEmbeddingBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId;
        var indexedTotal = checkpoint.IndexedTotal;
        var skippedTotal = checkpoint.SkippedTotal;
        var failedTotal = checkpoint.FailedTotal;

        var examinedThisSlice = 0;
        long processedThisSlice = 0;
        var indexedSlice = 0;
        var skippedSlice = 0;
        var failedSlice = 0;
        var vectorIndexedSlice = 0;
        var vectorFailedSlice = 0;
        var yielded = false;
        var exhausted = false;
        var globalLimit = options.Limit;

        bool LimitReached() => globalLimit is int gl && indexedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(indexedTotal, null,
                    $"indexing photo embeddings ({indexedTotal} indexed, {skippedTotal} skipped, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateQuery(profileId, cursor, options.TargetBlobObjectId)
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
                examinedThisSlice++;
                processedThisSlice++;
                cursor = blobId; // advance keyset cursor regardless of outcome

                var outcome = await IndexBlobAsync(embedder, profile, blobId, cancellationToken);
                switch (outcome.Blob)
                {
                    case BlobOutcome.Indexed:
                        indexedSlice++; indexedTotal++;
                        break;
                    case BlobOutcome.Skipped:
                        skippedSlice++; skippedTotal++;
                        break;
                    default:
                        // A genuine per-blob processing failure (e.g. unreadable
                        // bytes). NOT a provider/config issue, so we never write a
                        // status row; we advance past it and it stays eligible for
                        // a future fresh run (no BlobEmbedding row yet).
                        failedSlice++; failedTotal++;
                        break;
                }

                // Secondary, best-effort pgvector index of the just-written
                // embedding. Aggregate counters only — never per-blob diagnostics;
                // a vector failure never affects the canonical outcome above.
                if (outcome.Vector == VectorUpsertOutcome.Indexed) vectorIndexedSlice++;
                else if (outcome.Vector == VectorUpsertOutcome.Failed) vectorFailedSlice++;

                await ReportAsync();

                if (LimitReached()) break;
                if (shouldYield is not null && shouldYield(processedThisSlice)) { yielded = true; break; }
            }

            // Short page => no more candidates beyond the cursor.
            if (!yielded && !LimitReached() && page.Count < PageSize)
            {
                exhausted = true;
            }
        }

        // A yield caused by CANCELLATION must surface as cancellation so the
        // engine marks the job cancelled (never a permanent failure).
        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var moreWork = !exhausted && !LimitReached();
        var nextCheckpointJson = moreWork
            ? new PhotoEmbeddingBackfillCheckpoint(
                PhotoEmbeddingBackfillCheckpoint.CurrentVersion, cursor, indexedTotal, skippedTotal, failedTotal)
                .Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"ai photos backfill: {(moreWork ? "yielded" : "done")} — examined {examinedThisSlice} "
            + $"(indexed {indexedSlice}, skipped {skippedSlice}, failed {failedSlice}; "
            + $"total indexed {indexedTotal}; vectors indexed {vectorIndexedSlice}, vector-deferred {vectorFailedSlice}).");

        return new PhotoEmbeddingBackfillResult(
            examinedThisSlice, indexedSlice, skippedSlice, failedSlice, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson,
            IndexedTotal: indexedTotal, SkippedTotal: skippedTotal, FailedTotal: failedTotal,
            VectorIndexed: vectorIndexedSlice, VectorDeferred: vectorFailedSlice);
    }

    private enum BlobOutcome { Indexed, Skipped, Failed }

    // Canonical outcome plus the best-effort vector-index outcome.
    private readonly record struct IndexResult(BlobOutcome Blob, VectorUpsertOutcome Vector);

    private async Task<IndexResult> IndexBlobAsync(
        IImageEmbedder embedder, AiProfile profile, Guid blobId, CancellationToken cancellationToken)
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
            // Unreadable bytes: a content/IO problem, not a provider issue. Do not
            // record a status row; leave the blob eligible for a future run.
            return new IndexResult(BlobOutcome.Failed, VectorUpsertOutcome.SkippedUnavailable);
        }

        AiEmbeddingResult embedding;
        try
        {
            embedding = await embedder.EmbedImageAsync(bytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new IndexResult(BlobOutcome.Failed, VectorUpsertOutcome.SkippedUnavailable);
        }

        var dimension = profile.Dimension is > 0 ? profile.Dimension.Value : embedding.Dimension;
        byte[] embeddingBytes;
        try
        {
            embeddingBytes = _serializer.Serialize(embedding.Vector, dimension);
        }
        catch
        {
            return new IndexResult(BlobOutcome.Failed, VectorUpsertOutcome.SkippedUnavailable);
        }

        var row = new BlobEmbedding
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            ProfileId = profile.Id,
            EmbeddingBytes = embeddingBytes,
            Dimension = dimension,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };

        _db.BlobEmbeddings.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Unique (BlobObjectId, ProfileId) race: a concurrent run already
            // indexed this blob. Treat as already-done; the other run owns the
            // vector upsert too.
            _db.Entry(row).State = EntityState.Detached;
            return new IndexResult(BlobOutcome.Skipped, VectorUpsertOutcome.SkippedUnavailable);
        }

        // Canonical row is committed. Best-effort secondary index into pgvector
        // (no-op for unsupported dim / unavailable backend; a failure leaves the
        // vector row missing for a later vector-sync, never undoes the canonical
        // row above and never throws out of the backfill).
        VectorUpsertOutcome vector;
        try
        {
            vector = await _vectors.TryUpsertEmbeddingVectorAsync(
                row.Id, blobId, profile.Id, embedding.Vector, dimension, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            vector = VectorUpsertOutcome.Failed;
        }

        return new IndexResult(BlobOutcome.Indexed, vector);
    }

    // Eligible, not-yet-indexed candidates beyond the keyset cursor: image blobs
    // referenced by an active FileItem with no BlobEmbedding for this profile.
    private IQueryable<BlobObject> CandidateQuery(Guid profileId, Guid? cursor, Guid? targetBlobObjectId = null)
    {
        var query = _db.BlobObjects.AsNoTracking();
        if (cursor is Guid c)
        {
            query = query.Where(b => b.Id > c);
        }
        // Post-ingest single-target scope (bounded, no scan). The eligibility
        // predicate below (an active FileItem references the blob) already
        // re-checks normal-library membership: `_db.FileItems` carries the global
        // Private Vault filter, so a blob whose only file was moved into the vault
        // between enqueue and run yields no candidate. Slice 3 extends the same
        // re-check to per-file media-library exclusion (MediaLibraryState.Active):
        // a blob whose only references were moved to "Esclusi" after the job was
        // enqueued yields no candidate, so the worker writes nothing — the
        // controlled skip the race requires, with no new job and no error.
        if (targetBlobObjectId is Guid target)
        {
            query = query.Where(b => b.Id == target);
        }

        return query.Where(b =>
            _db.BlobMetadata.Any(m => m.BlobObjectId == b.Id && m.MediaCategory == MediaCategories.Image)
            && _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            && !_db.BlobEmbeddings.Any(e => e.BlobObjectId == b.Id && e.ProfileId == profileId));
    }
}

public sealed record PhotoEmbeddingBackfillOptions
{
    public int? Limit { get; init; }
    public bool DryRun { get; init; }
    // Post-ingest single-target scope: when set, index only this one blob
    // (bounded point-lookup, no library scan). Null = the global backfill.
    public Guid? TargetBlobObjectId { get; init; }
}

public sealed record PhotoEmbeddingBackfillResult(
    int Examined,
    int Indexed,
    int Skipped,
    int Failed,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null,
    int IndexedTotal = 0,
    int SkippedTotal = 0,
    int FailedTotal = 0,
    // Best-effort pgvector index outcomes for THIS slice (aggregate only). Vector
    // coverage is authoritatively reported by `ai photos embeddings coverage`.
    int VectorIndexed = 0,
    int VectorDeferred = 0);

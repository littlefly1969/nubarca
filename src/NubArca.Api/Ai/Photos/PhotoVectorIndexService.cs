using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Ai.Photos;

// Phase 2B foundation: pgvector-backed photo similarity gateway.
//
// Bridges the canonical, provider-agnostic embedding storage
// (blob_embeddings.EmbeddingBytes) to a dimension-specific pgvector table
// (blob_embedding_vectors_1152) + HNSW cosine index. ALL pgvector access is raw
// SQL through the EF connection: the `vector` type is never mapped in the EF
// model, so SQLite (unit tests) and any non-pgvector Postgres simply report the
// backend unavailable and callers fall back to exact-scan.
//
// Hard rules honoured here:
//   * profile-keyed only — every read/write filters by ProfileId; never mixes
//     profiles or assumes a "latest" one.
//   * one FIXED dimension (1152, SigLIP2 So400m) per table; other dimensions are
//     rejected/skipped until their own table exists (no truncation/padding).
//   * dimension + finiteness validated before insert.
//   * no raw vectors / BlobObjectId / SHA / StorageKey / paths are ever returned;
//     only logical FileItem id + name + a rounded cosine score.
public sealed class PhotoVectorIndexService
{
    // The only vector dimension with a table/index in this version.
    public const int SupportedDimension = 1152;
    private const string Table = "blob_embedding_vectors_1152";
    private const int PageSize = 200;
    // Upper bound for the per-query hnsw.ef_search GUC (pgvector accepts 1..1000).
    // Also the ceiling for the explorer's candidate pool / fetch cap.
    public const int MaxEfSearch = 1000;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly TimeProvider _clock;

    // Memoized per-scope availability of the vector table. The migration state
    // does not change within a request/job scope, so we probe `to_regclass` at
    // most once — important for the per-blob auto-upsert path (one probe, not one
    // per blob across a 48k backfill).
    private bool? _tableExists;

    public PhotoVectorIndexService(AppDbContext db, IAiVectorSerializer serializer, TimeProvider clock)
    {
        _db = db;
        _serializer = serializer;
        _clock = clock;
    }

    public static bool SupportsDimension(int? dimension) => dimension == SupportedDimension;

    // pgvector path is usable iff: a supported dimension and the dimension's
    // vector table actually exists (Npgsql + migration applied on a pgvector-
    // capable server). Anything else => false => exact-scan fallback.
    public async Task<bool> IsBackendAvailableAsync(int? dimension, CancellationToken cancellationToken = default)
        => SupportsDimension(dimension) && await TableExistsAsync(cancellationToken);

    // Count of vector rows already indexed for a profile (0 when unavailable).
    public async Task<long> CountIndexedAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken))
        {
            return 0;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {Table} WHERE \"ProfileId\" = @p;";
        AddParam(cmd, "p", profileId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    // Owner-private ANN nearest neighbours for a profile, ordered by cosine
    // similarity (== 1 - cosine distance). Returns null when the vector backend
    // is unavailable (caller falls back to exact-scan). Never returns vectors or
    // storage identifiers — only logical FileItem id + name + rounded score.
    public async Task<IReadOnlyList<VectorNeighbor>?> SearchAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        Guid excludeFileItemId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension
            || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var capped = Math.Clamp(take, 1, 100);
        // Owner filter is pushed INTO the query so the ANN limit applies to the
        // caller's own files only (cross-owner rows are never considered). HNSW
        // serves the ORDER BY via vector_cosine_ops.
        var sql = $@"
SELECT f.""Id"", f.""Name"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND f.""MediaLibraryState"" = {MediaLibraryScopePolicy.ActiveDbValue}
  AND f.""Id"" <> @excludeId
ORDER BY v.embedding <=> @q::vector
LIMIT @take;";

        // ef_search must be >= the number of rows we want back, or HNSW returns
        // fewer than `take` even when more neighbours exist (default ef_search is
        // only 40). See RunAnnQueryAsync.
        return await RunAnnQueryAsync(sql, EfSearchFor(capped), cmd =>
        {
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "ownerId", ownerUserId);
            AddParam(cmd, "excludeId", excludeFileItemId);
            AddParam(cmd, "take", capped);
        }, cancellationToken);
    }

    // Text-to-image retrieval uses the same profile/index but has no source
    // FileItem to exclude. Relevance order is returned directly and is never
    // mixed with the gallery's metadata sort order.
    public async Task<IReadOnlyList<VectorNeighbor>?> SearchByVectorAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension
            || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var capped = Math.Clamp(take, 1, 500);
        var sql = $@"
SELECT f.""Id"", f.""Name"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND f.""MediaLibraryState"" = {MediaLibraryScopePolicy.ActiveDbValue}
  AND NOT EXISTS (
      SELECT 1 FROM folders d
      WHERE d.""Id"" = f.""ParentFolderId"" AND d.""MediaPhotosExcluded"")
  AND (
      EXISTS (
          SELECT 1 FROM blob_metadata bm
          WHERE bm.""BlobObjectId"" = f.""BlobObjectId""
            AND bm.""DetectedContentType"" LIKE 'image/%')
      OR (
          NOT EXISTS (
              SELECT 1 FROM blob_metadata bm0
              WHERE bm0.""BlobObjectId"" = f.""BlobObjectId"")
          AND f.""MimeType"" LIKE 'image/%'))
  AND NOT EXISTS (
      SELECT 1 FROM blob_metadata bmq
      WHERE bmq.""BlobObjectId"" = f.""BlobObjectId""
        AND ((bmq.""Width"" IS NOT NULL AND bmq.""Width"" < {SemanticPhotoCandidatePolicy.MinEdgePixels})
          OR (bmq.""Height"" IS NOT NULL AND bmq.""Height"" < {SemanticPhotoCandidatePolicy.MinEdgePixels})))
ORDER BY v.embedding <=> @q::vector, f.""Id""
LIMIT @take;";

        return await RunAnnQueryAsync(sql, EfSearchFor(capped), cmd =>
        {
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "ownerId", ownerUserId);
            AddParam(cmd, "take", capped);
        }, cancellationToken);
    }

    // PHYSICAL-FILTER-FIRST semantic ranking. Ranks a query vector ONLY inside a
    // pre-resolved, owner-scoped candidate FileItem id set (built from the shared
    // physical gallery filters), returning the best `take` by cosine similarity.
    //
    // This is an EXACT distance scan restricted to the candidate ids — NOT a
    // global ANN query post-filtered by the candidate set. That is deliberate and
    // is the correctness guarantee the feature requires: a global HNSW top-N
    // followed by a selective filter can fail to fill Top-K (and can drop a valid
    // match that lies outside the global semantic prefix). Restricting the scan to
    // the candidate set and ordering exactly always returns the true best `take`
    // within the physical filter. The candidate set is bounded by the caller
    // (MaxSemanticCandidates), so the exact scan is bounded too.
    //
    // Returns null when the vector backend is unavailable (caller falls back to an
    // in-process exact scan over canonical embeddings). Never returns vectors or
    // storage identifiers — only logical FileItem id + name + rounded score.
    public async Task<IReadOnlyList<VectorNeighbor>?> SearchWithinCandidatesAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        IReadOnlyCollection<Guid> candidateFileItemIds,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension
            || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }
        if (candidateFileItemIds.Count == 0)
        {
            return Array.Empty<VectorNeighbor>();
        }

        var capped = Math.Clamp(take, 1, MaxEfSearch);
        var ids = candidateFileItemIds is Guid[] arr ? arr : candidateFileItemIds.ToArray();

        // Exact scan: no HNSW / ef_search. The owner filter is redundant with the
        // candidate id set (already owner-scoped) but kept as defence in depth so
        // a cross-owner id can never score. One vector row per candidate blob.
        //
        // CRITICAL: the ORDER BY must NOT be the bare `embedding <=> @q` operator
        // ascending — that is the exact pattern the HNSW index serves, and once
        // the planner picks the index this becomes a global ANN walk bounded by
        // hnsw.ef_search (default 40!) that is post-filtered by the candidate
        // set: with a selective filter almost every neighbour is discarded and
        // the query returns a handful of arbitrary rows instead of the true best
        // `take` within the candidates (field regression: semantic search
        // collapsed to a single incoherent hit once the vector table grew enough
        // to flip the planner). Ordering by the derived score expression keeps
        // the scan exact — pgvector only matches the bare ascending operator.
        var sql = $@"
SELECT f.""Id"", f.""Name"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""Id"" = ANY(@ids)
ORDER BY score DESC, f.""Id""
LIMIT @take;";

        var conn = await OpenAsync(cancellationToken);
        try
        {
            var items = new List<VectorNeighbor>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "ownerId", ownerUserId);
            AddParam(cmd, "ids", ids);
            AddParam(cmd, "take", capped);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new VectorNeighbor(
                    reader.GetGuid(0), reader.GetString(1), Math.Round(reader.GetDouble(2), 6)));
            }

            return items;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // → caller falls back to in-process exact scan
        }
    }

    // Threshold-aware ordered neighbours for the Similar Photos Explorer: the
    // top `fetchCap` owner-private neighbours with cosine similarity >=
    // `minSimilarity`, ordered most-similar-first. Returns null when the vector
    // backend is unavailable (caller falls back to exact-scan). The caller
    // paginates the returned ordered list with a (score desc, id asc) keyset
    // cursor; `fetchCap` bounds how deep the explorer can page (no unbounded
    // scan). Never returns vectors or storage identifiers.
    public async Task<IReadOnlyList<VectorNeighbor>?> SearchTopAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        Guid excludeFileItemId,
        double minSimilarity,
        int fetchCap,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension
            || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var cap = Math.Clamp(fetchCap, 1, MaxEfSearch);
        // HNSW serves the ORDER BY; the similarity threshold is a predicate on
        // the same expression, applied BEFORE the LIMIT. Owner filter is pushed
        // in so the cap applies to the caller's own files only. Deterministic
        // tie-break on f."Id" keeps the order stable for keyset pagination.
        var sql = $@"
SELECT f.""Id"", f.""Name"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND f.""MediaLibraryState"" = {MediaLibraryScopePolicy.ActiveDbValue}
  AND f.""Id"" <> @excludeId
  AND (1.0 - (v.embedding <=> @q::vector)) >= @minSim
ORDER BY v.embedding <=> @q::vector, f.""Id""
LIMIT @cap;";

        // CRITICAL: ef_search must be >= cap. With the default ef_search (40),
        // HNSW only walks ~40 nearest candidates and the threshold filters within
        // them — so `LIMIT 500` would still surface ~40 rows and lowering the
        // threshold would add almost nothing. Raising ef_search to the cap makes
        // the explorer genuinely return up to `cap` nearest-above-threshold rows.
        return await RunAnnQueryAsync(sql, cap, cmd =>
        {
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "ownerId", ownerUserId);
            AddParam(cmd, "excludeId", excludeFileItemId);
            AddParam(cmd, "minSim", minSimilarity);
            AddParam(cmd, "cap", cap);
        }, cancellationToken);
    }

    // EXACT (non-ANN) count of a profile's owner-private embeddings with cosine
    // similarity >= minSimilarity. No ORDER BY/LIMIT, so this is a full filtered
    // scan computing the true distance for every row — ground truth for the
    // diagnostic histogram, independent of HNSW recall. Null when the vector
    // backend is unavailable. Counts only — no ids/vectors/paths.
    public async Task<long?> CountAboveThresholdAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        Guid excludeFileItemId,
        double minSimilarity,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension
            || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT count(*)
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND f.""MediaLibraryState"" = {MediaLibraryScopePolicy.ActiveDbValue}
  AND f.""Id"" <> @excludeId
  AND (1.0 - (v.embedding <=> @q::vector)) >= @minSim;";
        AddParam(cmd, "q", ToVectorLiteral(queryVector));
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "ownerId", ownerUserId);
        AddParam(cmd, "excludeId", excludeFileItemId);
        AddParam(cmd, "minSim", minSimilarity);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    // Runs an ANN reader query with hnsw.ef_search raised for THIS query only.
    // ef_search is set transaction-locally (SET LOCAL) so it auto-resets on
    // commit and never leaks to other queries on the pooled EF connection. On
    // any failure returns null so the caller falls back to exact-scan — the same
    // contract as an unavailable backend.
    private async Task<IReadOnlyList<VectorNeighbor>?> RunAnnQueryAsync(
        string sql, int efSearch, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var conn = await OpenAsync(cancellationToken);
        DbTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(cancellationToken);

            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.Transaction = tx;
                // ef_search is an integer GUC; inline the clamped int (GUC SET
                // does not bind parameters). Clamped → injection-safe.
                setCmd.CommandText =
                    $"SET LOCAL hnsw.ef_search = {Math.Clamp(efSearch, 1, MaxEfSearch)};";
                await setCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var items = new List<VectorNeighbor>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                bind(cmd);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var name = reader.GetString(1);
                    var score = reader.GetDouble(2);
                    items.Add(new VectorNeighbor(id, name, Math.Round(score, 6)));
                }
            }

            await tx.CommitAsync(cancellationToken);
            return items;
        }
        catch (OperationCanceledException)
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            return null; // → caller falls back to exact-scan
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ef_search sized for a desired result count: at least the default (40) for
    // small Top-N reads, at least `want` so HNSW can surface that many.
    private static int EfSearchFor(int want) => Math.Clamp(Math.Max(want, 40), 1, MaxEfSearch);

    // Idempotently mirror a profile's canonical embeddings into its pgvector
    // table. Profile-keyed; validates dimension + finiteness; never touches other
    // profiles. Aggregate counts only — no per-row noise and no raw vectors.
    public async Task<PhotoVectorSyncOutcome> SyncProfileAsync(
        AiProfile profile, int? limit, bool dryRun,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var dimension = profile.Dimension ?? 0;

        // Eligible = canonical embeddings for THIS profile (provider-agnostic).
        var eligible = await _db.BlobEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id, cancellationToken);

        if (!SupportsDimension(profile.Dimension))
        {
            // No vector table exists for this dimension yet — skip cleanly.
            return PhotoVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, AiVectorUnavailableReasons.UnsupportedDimension, dryRun);
        }

        if (!await TableExistsAsync(cancellationToken))
        {
            return PhotoVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, AiVectorUnavailableReasons.PgvectorUnavailable, dryRun);
        }

        var indexedBefore = await CountIndexedAsync(profile.Id, cancellationToken);
        var missingBefore = Math.Max(0, eligible - indexedBefore);

        if (dryRun)
        {
            var wouldSync = limit is int lim ? Math.Min(missingBefore, lim) : missingBefore;
            log?.Invoke($"ai photos vector-sync (dry-run): {wouldSync} embedding(s) would be indexed for {profile.Key}.");
            return new PhotoVectorSyncOutcome(
                Available: true, Reason: null, profile.Key, dimension,
                eligible, indexedBefore, missingBefore,
                Synced: 0, SkippedDimensionMismatch: 0, Failed: 0, DryRun: true);
        }

        long synced = 0, skippedDim = 0, failed = 0, processed = 0;
        var cursor = Guid.Empty;
        var conn = await OpenAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit is int cap && processed >= cap)
            {
                break;
            }

            var remaining = limit is int lm ? Math.Min(PageSize, lm - (int)processed) : PageSize;
            var page = await FetchMissingPageAsync(conn, profile.Id, cursor, remaining, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = row.BlobEmbeddingId;
                processed++;

                float[] vector;
                try
                {
                    vector = _serializer.Deserialize(row.EmbeddingBytes);
                }
                catch
                {
                    failed++;
                    continue;
                }

                switch (ClassifyVector(vector, SupportedDimension))
                {
                    case VectorRowValidity.DimensionMismatch:
                        skippedDim++; // never truncate/pad — skip non-conforming rows
                        continue;
                    case VectorRowValidity.NonFinite:
                        failed++;
                        continue;
                }

                try
                {
                    await InsertVectorRowAsync(
                        conn, row.BlobEmbeddingId, row.BlobObjectId, profile.Id, vector, cancellationToken);
                    synced++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failed++;
                }
            }
        }

        var indexedAfter = await CountIndexedAsync(profile.Id, cancellationToken);
        var missingAfter = Math.Max(0, eligible - indexedAfter);
        log?.Invoke(
            $"ai photos vector-sync: {profile.Key} synced {synced} (skipped_dim {skippedDim}, failed {failed}); "
            + $"vector_indexed {indexedAfter}/{eligible}.");

        return new PhotoVectorSyncOutcome(
            Available: true, Reason: null, profile.Key, dimension,
            eligible, indexedAfter, missingAfter, synced, skippedDim, failed, DryRun: false);
    }

    // Best-effort upsert of a SINGLE embedding's vector row, used by the photo
    // backfill to index vectors as embeddings are written (so production
    // activation needs no separate manual vector-sync pass). It NEVER throws for
    // an unavailable/unsupported backend and NEVER touches the canonical
    // BlobEmbedding row — a failure just leaves the vector row missing for a
    // later `vector-sync` repair. Idempotent (ON CONFLICT DO NOTHING).
    public async Task<VectorUpsertOutcome> TryUpsertEmbeddingVectorAsync(
        Guid blobEmbeddingId, Guid blobObjectId, Guid profileId, float[] vector, int? dimension,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsDimension(dimension))
        {
            return VectorUpsertOutcome.SkippedUnsupported;
        }

        if (!await TableExistsAsync(cancellationToken))
        {
            return VectorUpsertOutcome.SkippedUnavailable;
        }

        // Validate against the table dimension; never truncate/pad, never insert
        // a NaN/Infinity/zero vector.
        if (ClassifyVector(vector, SupportedDimension) != VectorRowValidity.Ok)
        {
            return VectorUpsertOutcome.Failed;
        }

        try
        {
            var conn = await OpenAsync(cancellationToken);
            await InsertVectorRowAsync(conn, blobEmbeddingId, blobObjectId, profileId, vector, cancellationToken);
            return VectorUpsertOutcome.Indexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Transient (e.g. lost connection): keep the canonical row, leave the
            // vector missing so vector-sync repairs it later.
            return VectorUpsertOutcome.Failed;
        }
    }

    // ---- internals ----------------------------------------------------------

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        if (_tableExists is bool cached)
        {
            return cached;
        }

        if (!_db.Database.IsNpgsql())
        {
            _tableExists = false; // SQLite/other: never run Postgres-only SQL.
            return false;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT to_regclass('public.{Table}') IS NOT NULL;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        _tableExists = result is bool b && b;
        return _tableExists.Value;
    }

    private async Task<List<MissingRow>> FetchMissingPageAsync(
        DbConnection conn, Guid profileId, Guid cursor, int pageSize, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT be.""Id"", be.""BlobObjectId"", be.""EmbeddingBytes""
FROM blob_embeddings be
WHERE be.""ProfileId"" = @profileId
  AND be.""Id"" > @cursor
  AND NOT EXISTS (SELECT 1 FROM {Table} v WHERE v.""BlobEmbeddingId"" = be.""Id"")
ORDER BY be.""Id""
LIMIT @page;";
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "cursor", cursor);
        AddParam(cmd, "page", pageSize);

        var rows = new List<MissingRow>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MissingRow(
                reader.GetGuid(0), reader.GetGuid(1), (byte[])reader.GetValue(2)));
        }

        return rows;
    }

    private async Task InsertVectorRowAsync(
        DbConnection conn, Guid blobEmbeddingId, Guid blobObjectId, Guid profileId, float[] vector,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {Table} (""BlobEmbeddingId"", ""BlobObjectId"", ""ProfileId"", embedding, ""CreatedAt"")
VALUES (@id, @blob, @profile, @vec::vector, @now)
ON CONFLICT (""BlobEmbeddingId"") DO NOTHING;";
        AddParam(cmd, "id", blobEmbeddingId);
        AddParam(cmd, "blob", blobObjectId);
        AddParam(cmd, "profile", profileId);
        AddParam(cmd, "vec", ToVectorLiteral(vector));
        AddParam(cmd, "now", _clock.GetUtcNow().UtcDateTime);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
        }

        return conn; // EF owns the connection; never disposed here.
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // Pure, provider-independent validation of a candidate vector against the
    // table's fixed dimension. A non-conforming vector is never truncated/padded.
    public static VectorRowValidity ClassifyVector(float[] vector, int expectedDimension)
    {
        if (vector.Length != expectedDimension)
        {
            return VectorRowValidity.DimensionMismatch;
        }

        return IsFiniteNonZero(vector) ? VectorRowValidity.Ok : VectorRowValidity.NonFinite;
    }

    private static bool IsFiniteNonZero(float[] vector)
    {
        double sumSq = 0;
        foreach (var v in vector)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                return false;
            }

            sumSq += (double)v * v;
        }

        return sumSq > double.Epsilon;
    }

    // pgvector text input: "[f0,f1,...]" with round-trippable, invariant floats.
    private static string ToVectorLiteral(float[] vector)
    {
        var parts = new string[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            parts[i] = vector[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return "[" + string.Join(",", parts) + "]";
    }

    private readonly record struct MissingRow(Guid BlobEmbeddingId, Guid BlobObjectId, byte[] EmbeddingBytes);
}

// Result of validating a candidate vector against a fixed-dimension table.
public enum VectorRowValidity
{
    Ok,
    DimensionMismatch, // length != table dimension (skipped — never truncated/padded)
    NonFinite,         // NaN/Infinity or zero-norm (failed)
}

// Outcome of a single best-effort vector upsert (from the backfill auto-index).
public enum VectorUpsertOutcome
{
    Indexed,             // row inserted (or already present — idempotent)
    SkippedUnsupported,  // profile dimension has no vector table (e.g. deterministic 32)
    SkippedUnavailable,  // pgvector / vector table not present (e.g. SQLite, non-pgvector PG)
    Failed,              // wrong dimension / non-finite / transient insert error
}

// Owner-private neighbour. No vectors or storage identifiers.
public sealed record VectorNeighbor(Guid FileItemId, string Name, double Score);

// Sanitized reason tokens for an unavailable vector path.
public static class AiVectorUnavailableReasons
{
    public const string PgvectorUnavailable = "pgvector-unavailable";
    public const string UnsupportedDimension = "unsupported-dimension";
}

// Aggregate-only vector-sync outcome. No raw vectors / storage identifiers.
public sealed record PhotoVectorSyncOutcome(
    bool Available,
    string? Reason,
    string ProfileKey,
    int Dimension,
    long EligibleEmbeddings,
    long VectorIndexed,
    long MissingVectors,
    long Synced,
    long SkippedDimensionMismatch,
    long Failed,
    bool DryRun)
{
    public static PhotoVectorSyncOutcome Unavailable(
        string profileKey, int dimension, long eligible, string reason, bool dryRun) =>
        new(Available: false, reason, profileKey, dimension,
            eligible, VectorIndexed: 0, MissingVectors: eligible,
            Synced: 0, SkippedDimensionMismatch: 0, Failed: 0, dryRun);
}

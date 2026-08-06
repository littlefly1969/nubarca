using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Face Substrate v0: pgvector-backed face-embedding search gateway. Bridges the
// canonical, provider-agnostic FaceEmbedding.EmbeddingBytes to a fixed-dimension
// pgvector table (face_embedding_vectors_512) + HNSW cosine index.
//
// Mirrors PhotoVectorIndexService exactly in spirit:
//   * ALL pgvector access is raw SQL through the EF connection; the `vector` type
//     is never mapped, so SQLite / non-pgvector Postgres report the backend
//     unavailable and callers skip/fall back.
//   * profile-keyed only; one FIXED dimension (512, ArcFace) per table.
//   * dimension + finiteness validated before insert; never truncate/pad.
//   * NO raw vectors / BlobObjectId / SHA / StorageKey / paths returned — search
//     results carry only logical FileItem id + name + face-detection id + score.
//   * OWNER boundary + Private-Vault exclusion pushed INTO every search query
//     (join file_items under DeletedAt IS NULL AND PrivateVaultId IS NULL).
public sealed class FaceVectorIndexService
{
    // The only face vector dimension with a table/index in this version.
    public const int SupportedDimension = 512;
    private const string Table = "face_embedding_vectors_512";
    private const int PageSize = 200;
    public const int MaxEfSearch = 1000;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly TimeProvider _clock;

    private bool? _tableExists;

    public FaceVectorIndexService(AppDbContext db, IAiVectorSerializer serializer, TimeProvider clock)
    {
        _db = db;
        _serializer = serializer;
        _clock = clock;
    }

    public static bool SupportsDimension(int? dimension) => dimension == SupportedDimension;

    public async Task<bool> IsBackendAvailableAsync(int? dimension, CancellationToken cancellationToken = default)
        => SupportsDimension(dimension) && await TableExistsAsync(cancellationToken);

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

    // Owner-private nearest faces for a query vector, ordered most-similar-first,
    // filtered by cosine similarity >= minSimilarity. Returns null when the vector
    // backend is unavailable. Never returns vectors or storage identifiers.
    public async Task<IReadOnlyList<FaceNeighbor>?> SearchAsync(
        Guid profileId,
        float[] queryVector,
        Guid ownerUserId,
        Guid excludeFaceDetectionId,
        double minSimilarity,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var cap = Math.Clamp(take, 1, MaxEfSearch);
        var sql = $@"
SELECT f.""Id"", f.""Name"", v.""FaceDetectionId"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND v.""FaceDetectionId"" <> @excludeDetectionId
  AND (1.0 - (v.embedding <=> @q::vector)) >= @minSim
ORDER BY v.embedding <=> @q::vector, f.""Id""
LIMIT @cap;";

        var conn = await OpenAsync(cancellationToken);
        DbTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(cancellationToken);
            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.Transaction = tx;
                setCmd.CommandText = $"SET LOCAL hnsw.ef_search = {Math.Clamp(cap, 1, MaxEfSearch)};";
                await setCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var items = new List<FaceNeighbor>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                AddParam(cmd, "q", ToVectorLiteral(queryVector));
                AddParam(cmd, "profileId", profileId);
                AddParam(cmd, "ownerId", ownerUserId);
                AddParam(cmd, "excludeDetectionId", excludeFaceDetectionId);
                AddParam(cmd, "minSim", minSimilarity);
                AddParam(cmd, "cap", cap);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new FaceNeighbor(
                        reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
                        Math.Round(reader.GetDouble(3), 6)));
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
            return null;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // Best-effort upsert of a SINGLE face embedding's vector row, used by the
    // embedding backfill to index vectors as they are written. NEVER throws for
    // an unavailable/unsupported backend and NEVER touches the canonical
    // FaceEmbedding row. Idempotent (ON CONFLICT DO NOTHING).
    public async Task<VectorUpsertOutcome> TryUpsertFaceVectorAsync(
        Guid faceEmbeddingId, Guid faceDetectionId, Guid blobObjectId, Guid profileId,
        float[] vector, int? dimension, CancellationToken cancellationToken = default)
    {
        if (!SupportsDimension(dimension))
        {
            return VectorUpsertOutcome.SkippedUnsupported;
        }

        if (!await TableExistsAsync(cancellationToken))
        {
            return VectorUpsertOutcome.SkippedUnavailable;
        }

        if (PhotoVectorIndexService.ClassifyVector(vector, SupportedDimension) != VectorRowValidity.Ok)
        {
            return VectorUpsertOutcome.Failed;
        }

        try
        {
            var conn = await OpenAsync(cancellationToken);
            await InsertVectorRowAsync(conn, faceEmbeddingId, faceDetectionId, blobObjectId, profileId, vector, cancellationToken);
            return VectorUpsertOutcome.Indexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return VectorUpsertOutcome.Failed;
        }
    }

    // Idempotently mirror a profile's canonical face embeddings into the pgvector
    // table (repair path for anything the backfill left deferred). Aggregate
    // counts only; profile-keyed; validates dimension + finiteness.
    public async Task<FaceVectorSyncOutcome> SyncProfileAsync(
        AiProfile profile, int? limit, bool dryRun,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var dimension = profile.Dimension ?? 0;

        var eligible = await _db.FaceEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id
                && e.EmbeddingStatus == AiArtifactStatuses.Completed, cancellationToken);

        if (!SupportsDimension(profile.Dimension))
        {
            return FaceVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, AiVectorUnavailableReasons.UnsupportedDimension, dryRun);
        }

        if (!await TableExistsAsync(cancellationToken))
        {
            return FaceVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, AiVectorUnavailableReasons.PgvectorUnavailable, dryRun);
        }

        var indexedBefore = await CountIndexedAsync(profile.Id, cancellationToken);
        var missingBefore = Math.Max(0, eligible - indexedBefore);

        if (dryRun)
        {
            var wouldSync = limit is int lim ? Math.Min(missingBefore, lim) : missingBefore;
            log?.Invoke($"ai faces vector-sync (dry-run): {wouldSync} face embedding(s) would be indexed for {profile.Key}.");
            return new FaceVectorSyncOutcome(
                Available: true, Reason: null, profile.Key, dimension,
                eligible, indexedBefore, missingBefore, Synced: 0, SkippedDimensionMismatch: 0, Failed: 0, DryRun: true);
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
                cursor = row.FaceEmbeddingId;
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

                switch (PhotoVectorIndexService.ClassifyVector(vector, SupportedDimension))
                {
                    case VectorRowValidity.DimensionMismatch:
                        skippedDim++;
                        continue;
                    case VectorRowValidity.NonFinite:
                        failed++;
                        continue;
                }

                try
                {
                    await InsertVectorRowAsync(
                        conn, row.FaceEmbeddingId, row.FaceDetectionId, row.BlobObjectId, profile.Id, vector, cancellationToken);
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
            $"ai faces vector-sync: {profile.Key} synced {synced} (skipped_dim {skippedDim}, failed {failed}); "
            + $"vector_indexed {indexedAfter}/{eligible}.");

        return new FaceVectorSyncOutcome(
            Available: true, Reason: null, profile.Key, dimension,
            eligible, indexedAfter, missingAfter, synced, skippedDim, failed, DryRun: false);
    }

    // ---- pgvector kNN clustering support ------------------------------------

    // Eligible ANCHOR faces for owner-scoped automatic clustering, ordered stably.
    // Eligible = has a vector row for the active profile, referenced by an active
    // non-vault FileItem owned by the owner, NOT assigned to any person, NOT
    // individually ignored. Returns null when the pgvector backend is unavailable
    // (caller falls back to exact clustering). No vectors are returned.
    public async Task<IReadOnlyList<Guid>?> GetEligibleClusterFaceIdsAsync(
        Guid ownerUserId, Guid profileId, int max, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var cap = Math.Clamp(max, 1, 1_000_000);
        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT DISTINCT v.""FaceDetectionId""
FROM {Table} v
JOIN file_items f ON f.""BlobObjectId"" = v.""BlobObjectId""
WHERE v.""ProfileId"" = @profileId
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND NOT EXISTS (SELECT 1 FROM person_face_assignments a
                  WHERE a.""OwnerUserId"" = @ownerId AND a.""FaceDetectionId"" = v.""FaceDetectionId"")
  AND NOT EXISTS (SELECT 1 FROM ignored_faces g
                  WHERE g.""OwnerUserId"" = @ownerId AND g.""FaceDetectionId"" = v.""FaceDetectionId"")
ORDER BY v.""FaceDetectionId""
LIMIT @cap;";
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "ownerId", ownerUserId);
        AddParam(cmd, "cap", cap);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    // Collect the kNN similarity graph edges for the given eligible anchor faces:
    // for each anchor, its top-K nearest ELIGIBLE neighbours (same eligibility as
    // above) with cosine similarity >= candidateThreshold, via the HNSW cosine
    // index. The anchor vector is passed as a literal so the index is used (O(n·ef)
    // instead of O(n²)). Runs in ONE transaction with SET LOCAL hnsw.ef_search.
    // Returns null when the backend is unavailable. No vectors escape this method.
    public async Task<IReadOnlyList<FaceEdge>?> CollectKnnEdgesAsync(
        Guid ownerUserId, Guid profileId, IReadOnlyList<Guid> anchorIds,
        double candidateThreshold, int k, int efSearch,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken) || anchorIds.Count == 0)
        {
            return anchorIds.Count == 0 ? Array.Empty<FaceEdge>() : null;
        }

        var kCap = Math.Clamp(k, 1, 500);
        var ef = Math.Clamp(efSearch, 1, MaxEfSearch);
        // Fetch up to ef nearest by the HNSW cosine index (fetch limit <= ef_search,
        // else HNSW can't return more). Eligibility is filtered IN MEMORY afterwards
        // against `eligible` — putting the file_items join / NOT EXISTS / distance
        // predicate in this query would defeat the HNSW index (planner falls back to
        // a per-anchor scan → O(n²)). This is a PURE ANN query so the index is used.
        var fetchLimit = Math.Min(ef, Math.Max(kCap * 4, kCap));
        // The query vector MUST be an inline literal constant (not a bound param):
        // pgvector's HNSW index scan is only planned when the ORDER BY operand is a
        // constant. With a parameter (`@q::vector`) the planner falls back to a seq
        // scan + sort per anchor (verified via EXPLAIN → idx_scan stays 0 → O(n²)).
        // The literal is numeric-only (ToVectorLiteral), so there is no injection.

        // Owner-visible / non-vault / unassigned / not-ignored is exactly the
        // membership of the eligible set (computed in SQL by the caller), so an
        // in-memory hash lookup enforces every eligibility rule.
        var eligibleSet = new HashSet<Guid>(anchorIds);
        var edges = new List<FaceEdge>();
        var conn = await OpenAsync(cancellationToken);
        DbTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(cancellationToken);
            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.Transaction = tx;
                setCmd.CommandText = $"SET LOCAL hnsw.ef_search = {ef};";
                await setCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var processed = 0;
            foreach (var pageStart in Enumerable.Range(0, anchorIds.Count).Where(i => i % PageSize == 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = anchorIds.Skip(pageStart).Take(PageSize).ToList();
                var vectors = await LoadVectorsAsync(conn, tx, profileId, page, cancellationToken);

                foreach (var anchor in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;
                    if (!vectors.TryGetValue(anchor, out var vec))
                    {
                        continue; // anchor vector missing/invalid → skip (no edges)
                    }

                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
SELECT n.""FaceDetectionId"", (1.0 - (n.embedding <=> '{ToVectorLiteral(vec)}'::vector))::float8 AS sim
FROM {Table} n
WHERE n.""ProfileId"" = @profileId
  AND n.""FaceDetectionId"" <> @anchor
ORDER BY n.embedding <=> '{ToVectorLiteral(vec)}'::vector
LIMIT @k;";
                    AddParam(cmd, "profileId", profileId);
                    AddParam(cmd, "anchor", anchor);
                    AddParam(cmd, "k", fetchLimit);
                    var kept = 0;
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (kept >= kCap)
                        {
                            break; // out-degree cap reached (rows are distance-ordered)
                        }
                        var neighbor = reader.GetGuid(0);
                        var sim = reader.GetDouble(1);
                        // In-memory eligibility + candidate-threshold filter.
                        if (sim >= candidateThreshold && neighbor != anchor && eligibleSet.Contains(neighbor))
                        {
                            edges.Add(new FaceEdge(anchor, neighbor, Math.Round(sim, 6)));
                            kept++;
                        }
                    }

                    if (processed % 500 == 0)
                    {
                        progress?.Invoke(processed);
                    }
                }
            }

            await tx.CommitAsync(cancellationToken);
            return edges;
        }
        catch (OperationCanceledException)
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            return null;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // Load the canonical embedding bytes for a page of face ids and deserialize to
    // vectors (internal to clustering; never surfaced). Invalid/zero vectors dropped.
    private async Task<Dictionary<Guid, float[]>> LoadVectorsAsync(
        DbConnection conn, DbTransaction tx, Guid profileId, IReadOnlyList<Guid> faceIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, float[]>(faceIds.Count);
        var idList = string.Join(",", faceIds.Select((_, i) => $"@f{i}"));
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
SELECT fe.""FaceDetectionId"", fe.""EmbeddingBytes""
FROM face_embeddings fe
WHERE fe.""ProfileId"" = @profileId
  AND fe.""EmbeddingStatus"" = 'completed'
  AND fe.""FaceDetectionId"" IN ({idList});";
        AddParam(cmd, "profileId", profileId);
        for (var i = 0; i < faceIds.Count; i++)
        {
            AddParam(cmd, $"f{i}", faceIds[i]);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            try
            {
                var v = _serializer.Deserialize((byte[])reader.GetValue(1));
                if (v.Length == SupportedDimension)
                {
                    result[id] = v;
                }
            }
            catch
            {
                // skip undeserializable
            }
        }
        return result;
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
            _tableExists = false;
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
SELECT fe.""Id"", fe.""FaceDetectionId"", fd.""BlobObjectId"", fe.""EmbeddingBytes""
FROM face_embeddings fe
JOIN face_detections fd ON fd.""Id"" = fe.""FaceDetectionId""
WHERE fe.""ProfileId"" = @profileId
  AND fe.""EmbeddingStatus"" = 'completed'
  AND fe.""Id"" > @cursor
  AND NOT EXISTS (SELECT 1 FROM {Table} v WHERE v.""FaceEmbeddingId"" = fe.""Id"")
ORDER BY fe.""Id""
LIMIT @page;";
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "cursor", cursor);
        AddParam(cmd, "page", pageSize);

        var rows = new List<MissingRow>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MissingRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), (byte[])reader.GetValue(3)));
        }

        return rows;
    }

    private async Task InsertVectorRowAsync(
        DbConnection conn, Guid faceEmbeddingId, Guid faceDetectionId, Guid blobObjectId, Guid profileId,
        float[] vector, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {Table} (""FaceEmbeddingId"", ""FaceDetectionId"", ""BlobObjectId"", ""ProfileId"", embedding, ""CreatedAt"")
VALUES (@id, @detection, @blob, @profile, @vec::vector, @now)
ON CONFLICT (""FaceEmbeddingId"") DO NOTHING;";
        AddParam(cmd, "id", faceEmbeddingId);
        AddParam(cmd, "detection", faceDetectionId);
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

        return conn;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string ToVectorLiteral(float[] vector)
    {
        var parts = new string[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            parts[i] = vector[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return "[" + string.Join(",", parts) + "]";
    }

    private readonly record struct MissingRow(
        Guid FaceEmbeddingId, Guid FaceDetectionId, Guid BlobObjectId, byte[] EmbeddingBytes);
}

// Owner-private face neighbour. No vectors or storage identifiers.
public sealed record FaceNeighbor(Guid FileItemId, string Name, Guid FaceDetectionId, double Score);

// A kNN similarity graph edge between two face detections (clustering internal).
public sealed record FaceEdge(Guid AnchorFaceId, Guid NeighborFaceId, double Similarity);

// Aggregate-only face vector-sync outcome. No raw vectors / storage identifiers.
public sealed record FaceVectorSyncOutcome(
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
    public static FaceVectorSyncOutcome Unavailable(
        string profileKey, int dimension, long eligible, string reason, bool dryRun) =>
        new(Available: false, reason, profileKey, dimension,
            eligible, VectorIndexed: 0, MissingVectors: eligible,
            Synced: 0, SkippedDimensionMismatch: 0, Failed: 0, dryRun);
}

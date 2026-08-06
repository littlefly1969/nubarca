using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Video;

// VSEM-02: pgvector ACCELERATION gateway for video sample embeddings.
//
// The canonical rows (video_semantic_sample_embeddings.EmbeddingBytes) are the
// source of truth; this service mirrors them into the dimension-specific
// video_semantic_sample_embedding_vectors_1152 table + HNSW cosine index. All
// pgvector access is raw SQL through the EF connection — the `vector` type is
// never mapped — so SQLite and any non-pgvector Postgres simply report the
// backend unavailable and every search transparently uses the EXACT in-process
// scan over canonical rows instead. A vector-sync failure never touches a
// canonical embedding.
//
// There is deliberately NO unrestricted/global search method: every search
// requires an explicit bounded candidate sample-id scope, which is what
// preserves the later retrieval contract (owner-visible FileItems → eligible
// blobs/samples → vector ranking). Rows carry only sample ids and profile ids
// — nothing owner-specific — and no method ever returns a raw vector.
public sealed class VideoSemanticSampleVectorIndexService
{
    // The only vector dimension with a table/index in this version (SigLIP2
    // So400m — same space as the photo index).
    public const int SupportedDimension = PhotoVectorIndexService.SupportedDimension;
    private const string Table = "video_semantic_sample_embedding_vectors_1152";

    // Hard ceiling on one candidate scope: keeps the exact scan (SQL or
    // in-process) bounded. Callers page above this level if they ever need more.
    public const int MaxCandidateScope = 20_000;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly TimeProvider _clock;

    // Memoized per-scope availability of the vector table (same rationale as
    // PhotoVectorIndexService: one to_regclass probe per job scope, not one per
    // sample).
    private bool? _tableExists;

    public VideoSemanticSampleVectorIndexService(
        AppDbContext db, IAiVectorSerializer serializer, TimeProvider clock)
    {
        _db = db;
        _serializer = serializer;
        _clock = clock;
    }

    public static bool SupportsDimension(int? dimension) => dimension == SupportedDimension;

    public async Task<bool> IsBackendAvailableAsync(
        int? dimension, CancellationToken cancellationToken = default)
        => SupportsDimension(dimension) && await TableExistsAsync(cancellationToken);

    // Whether a canonical embedding row already has its vector mirror. False
    // when the backend is unavailable (there is nothing to be indexed in).
    public async Task<bool> IsIndexedAsync(
        Guid videoSemanticSampleEmbeddingId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken))
        {
            return false;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT EXISTS (SELECT 1 FROM {Table} WHERE \"VideoSemanticSampleEmbeddingId\" = @id);";
        AddParam(cmd, "id", videoSemanticSampleEmbeddingId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is bool b && b;
    }

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

    // Same as CountIndexedAsync, but excludes STALE mirror rows — a vector row
    // whose canonical embedding is no longer `completed` (e.g. flipped to
    // failed by a rebuild attempt, pending DeleteStaleAsync cleanup). Coverage
    // reporting must never count a stale mirror as synchronized: the canonical
    // row is the source of truth, and a mirror pointing at a non-completed
    // canonical row is not real acceleration coverage.
    public async Task<long> CountValidIndexedAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken))
        {
            return 0;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT count(*) FROM {Table} v
WHERE v.""ProfileId"" = @p
  AND EXISTS (
      SELECT 1 FROM video_semantic_sample_embeddings e
      WHERE e.""Id"" = v.""VideoSemanticSampleEmbeddingId""
        AND e.""Status"" = 'completed');";
        AddParam(cmd, "p", profileId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    // Best-effort upsert of ONE canonical embedding's vector mirror. Never
    // throws for an unavailable backend and never touches the canonical row —
    // a failure leaves the mirror missing/stale for a later sync retry.
    // Upsert (not insert-ignore): a rebuilt canonical embedding under the same
    // id must replace its old vector, never keep serving it.
    public async Task<VectorUpsertOutcome> SyncEmbeddingAsync(
        Guid videoSemanticSampleEmbeddingId, Guid videoSemanticSampleId, Guid profileId,
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
INSERT INTO {Table}
    (""VideoSemanticSampleEmbeddingId"", ""VideoSemanticSampleId"", ""ProfileId"", embedding, ""CreatedAt"")
VALUES (@id, @sample, @profile, @vec::vector, @now)
ON CONFLICT (""VideoSemanticSampleEmbeddingId"")
DO UPDATE SET embedding = EXCLUDED.embedding, ""CreatedAt"" = EXCLUDED.""CreatedAt"";";
            AddParam(cmd, "id", videoSemanticSampleEmbeddingId);
            AddParam(cmd, "sample", videoSemanticSampleId);
            AddParam(cmd, "profile", profileId);
            AddParam(cmd, "vec", ToVectorLiteral(vector));
            AddParam(cmd, "now", _clock.GetUtcNow().UtcDateTime);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
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

    // Removes vector rows whose canonical embedding is no longer a completed
    // row for this profile (FK cascade already covers deleted canonical rows;
    // this covers rows that flipped to failed on a rebuild attempt). Returns
    // the number of rows removed, 0 when the backend is unavailable.
    public async Task<long> DeleteStaleAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken))
        {
            return 0;
        }

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
DELETE FROM {Table} v
WHERE v.""ProfileId"" = @profile
  AND NOT EXISTS (
      SELECT 1 FROM video_semantic_sample_embeddings e
      WHERE e.""Id"" = v.""VideoSemanticSampleEmbeddingId""
        AND e.""Status"" = 'completed');";
        AddParam(cmd, "profile", profileId);
        var removed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return removed;
    }

    // Exact cosine ranking of a query vector WITHIN an explicit bounded sample
    // candidate scope. pgvector is used as the acceleration layer when
    // available; otherwise (or on any pgvector failure) the ranking falls back
    // to an exact in-process scan over the canonical rows — same contract,
    // same scores. Always ordered best-first with a deterministic id
    // tie-break. Never returns vectors.
    //
    // NOTE: the SQL deliberately orders by the derived score expression, NOT
    // by the bare ascending `embedding <=> @q` operator — the bare pattern is
    // what HNSW serves, and a planner flip would turn this into a global ANN
    // walk post-filtered by the candidate set (bounded by ef_search, default
    // 40), silently dropping true matches. Ordering by the derived expression
    // keeps the restricted scan exact.
    public async Task<IReadOnlyList<VideoSampleNeighbor>> SearchWithinCandidatesAsync(
        Guid profileId,
        float[] queryVector,
        IReadOnlyCollection<Guid> candidateSampleIds,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        ArgumentNullException.ThrowIfNull(candidateSampleIds);

        if (candidateSampleIds.Count == 0)
        {
            return Array.Empty<VideoSampleNeighbor>();
        }

        if (candidateSampleIds.Count > MaxCandidateScope)
        {
            throw new ArgumentException(
                $"Candidate scope exceeds the {MaxCandidateScope}-id bound.", nameof(candidateSampleIds));
        }

        var capped = Math.Clamp(take, 1, MaxCandidateScope);

        if (queryVector.Length == SupportedDimension && await TableExistsAsync(cancellationToken))
        {
            var accelerated = await TrySearchPgvectorAsync(
                profileId, queryVector, candidateSampleIds, capped, cancellationToken);
            if (accelerated is not null)
            {
                return accelerated;
            }
        }

        return await SearchExactAsync(profileId, queryVector, candidateSampleIds, capped, cancellationToken);
    }

    private async Task<IReadOnlyList<VideoSampleNeighbor>?> TrySearchPgvectorAsync(
        Guid profileId, float[] queryVector, IReadOnlyCollection<Guid> candidateSampleIds,
        int take, CancellationToken cancellationToken)
    {
        var ids = candidateSampleIds is Guid[] arr ? arr : candidateSampleIds.ToArray();
        var sql = $@"
SELECT v.""VideoSemanticSampleId"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
WHERE v.""ProfileId"" = @profileId
  AND v.""VideoSemanticSampleId"" = ANY(@ids)
ORDER BY score DESC, v.""VideoSemanticSampleId""
LIMIT @take;";

        try
        {
            var conn = await OpenAsync(cancellationToken);
            var items = new List<VideoSampleNeighbor>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "ids", ids);
            AddParam(cmd, "take", take);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new VideoSampleNeighbor(
                    reader.GetGuid(0), Math.Round(reader.GetDouble(1), 6)));
            }

            return items;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // → exact in-process fallback
        }
    }

    // MANDATORY exact fallback: deserializes the canonical bytes of the
    // candidate rows and ranks by true cosine similarity. Bounded by the
    // candidate-scope cap, so memory and CPU stay bounded too.
    private async Task<IReadOnlyList<VideoSampleNeighbor>> SearchExactAsync(
        Guid profileId, float[] queryVector, IReadOnlyCollection<Guid> candidateSampleIds,
        int take, CancellationToken cancellationToken)
    {
        var ids = candidateSampleIds.ToList();
        var rows = await _db.VideoSemanticSampleEmbeddings.AsNoTracking()
            .Where(e => e.ProfileId == profileId
                && e.Status == AiArtifactStatuses.Completed
                && ids.Contains(e.VideoSemanticSampleId))
            .Select(e => new { e.VideoSemanticSampleId, e.EmbeddingBytes })
            .ToListAsync(cancellationToken);

        var scored = new List<VideoSampleNeighbor>(rows.Count);
        foreach (var row in rows)
        {
            float[] vector;
            try
            {
                vector = _serializer.Deserialize(row.EmbeddingBytes);
            }
            catch
            {
                continue; // unreadable canonical bytes: excluded, never a crash
            }

            if (vector.Length != queryVector.Length)
            {
                continue; // profile-mismatched dimensions never score
            }

            var score = CosineSimilarity(queryVector, vector);
            if (double.IsFinite(score))
            {
                scored.Add(new VideoSampleNeighbor(row.VideoSemanticSampleId, Math.Round(score, 6)));
            }
        }

        return scored
            .OrderByDescending(n => n.Score)
            .ThenBy(n => n.SampleId)
            .Take(take)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
        return denominator <= double.Epsilon ? double.NaN : dot / denominator;
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

    private static string ToVectorLiteral(float[] vector)
    {
        var parts = new string[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            parts[i] = vector[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return "[" + string.Join(",", parts) + "]";
    }
}

// One ranked candidate sample. Sample id + rounded cosine score only — no
// vectors, no blob/owner/file identifiers. The initial score contracts build
// on this: segment score = max of its samples, video score = max of its
// segments (never an averaged whole-video vector).
public sealed record VideoSampleNeighbor(Guid SampleId, double Score);

using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Rag.Storage;

/// One nearest neighbour. A chunk id and a rounded score — never a vector.
public sealed record RagVectorNeighbor(Guid ChunkId, double Score);

/// pgvector acceleration for RAG chunk embeddings.
///
/// Same shape as the photo substrate's PhotoVectorIndexService, and for the same
/// reasons: ALL pgvector access is raw SQL through the EF connection, the
/// `vector` type is never mapped in the EF model, and SQLite or a Postgres
/// without the extension simply reports the backend unavailable. Unit tests then
/// run the whole retrieval stack with no container and no extension, and the
/// production path degrades to lexical instead of failing.
///
/// It is a SEPARATE vector space from photos and faces, in its own table. The
/// concept is shared; the space is not. Mixing a 384-dimension text vector into
/// a table of 1152-dimension image vectors would be arithmetically impossible,
/// and mixing two text profiles into one table would be arithmetically possible
/// and silently meaningless — which is why every read filters by ProfileId
/// exactly, and never by "the latest one".
public sealed class RagVectorIndexService
{
    /// The only dimension with a table and an index in this version. It follows
    /// the selected embedding model; another model means another table, never a
    /// truncation or a pad into this one.
    public const int SupportedDimension = 384;

    private const string Table = "rag_chunk_embedding_vectors_384";
    private const int PageSize = 200;
    public const int MaxEfSearch = 1000;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly TimeProvider _clock;

    // Memoized per scope: the migration state cannot change inside one request
    // or one CLI run, so `to_regclass` is probed at most once rather than once
    // per chunk across an indexing pass.
    private bool? _tableExists;

    public RagVectorIndexService(AppDbContext db, IAiVectorSerializer serializer, TimeProvider clock)
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
        if (!await TableExistsAsync(cancellationToken)) return 0;

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {Table} WHERE \"ProfileId\" = @p;";
        AddParam(cmd, "p", profileId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// Vector rows for chunks of ONE domain under one profile.
    ///
    /// Domain-scoped because that is the question `rag coverage --domain X`
    /// asks. A profile's vectors span every domain it was used for, so the
    /// profile-wide count would answer a different question and look like an
    /// answer to this one.
    public async Task<long> CountIndexedAsync(
        string domainKey, Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) return 0;

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT count(*)
FROM {Table} v
JOIN rag_chunks c ON c.""Id"" = v.""ChunkId""
JOIN rag_domain_sources m ON m.""SourceId"" = c.""SourceId""
WHERE v.""ProfileId"" = @p AND m.""DomainKey"" = @d;";
        AddParam(cmd, "p", profileId);
        AddParam(cmd, "d", domainKey);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// CANONICAL embeddings for chunks of one domain under one profile. These
    /// are the truth; the vector rows above are derived from them, and a gap
    /// between the two counts is exactly what `rag coverage` exists to show.
    public async Task<long> CountCanonicalAsync(
        string domainKey, Guid profileId, CancellationToken cancellationToken = default)
        => await (
            from embedding in _db.RagChunkEmbeddings.AsNoTracking()
            join chunk in _db.RagChunks.AsNoTracking() on embedding.ChunkId equals chunk.Id
            join membership in _db.RagDomainSources.AsNoTracking()
                on chunk.SourceId equals membership.SourceId
            where embedding.ProfileId == profileId && membership.DomainKey == domainKey
            select embedding.Id)
            .LongCountAsync(cancellationToken);

    /// Nearest chunks WITHIN one domain, for one profile.
    ///
    /// The domain filter is pushed into the query rather than applied to the
    /// results, so the ANN limit applies to that domain's chunks only. A
    /// post-filter would mean asking for the ten nearest chunks in the database
    /// and hoping some of them belong to the domain the caller is allowed to
    /// read — which is the shape of an isolation bug even when it happens to
    /// return the right rows.
    ///
    /// Returns null when the backend is unavailable, so the caller falls back to
    /// lexical rather than reporting an empty semantic result as an answer.
    public async Task<IReadOnlyList<RagVectorNeighbor>?> SearchAsync(
        string domainKey,
        Guid profileId,
        float[] queryVector,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != SupportedDimension || !await TableExistsAsync(cancellationToken))
        {
            return null;
        }

        var capped = Math.Clamp(take, 1, 500);
        var sql = $@"
SELECT v.""ChunkId"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN rag_chunks c ON c.""Id"" = v.""ChunkId""
JOIN rag_domain_sources m ON m.""SourceId"" = c.""SourceId""
WHERE v.""ProfileId"" = @profileId
  AND m.""DomainKey"" = @domain
ORDER BY v.embedding <=> @q::vector
LIMIT @take;";

        return await RunAnnQueryAsync(sql, EfSearchFor(capped), cmd =>
        {
            AddParam(cmd, "q", ToVectorLiteral(queryVector));
            AddParam(cmd, "profileId", profileId);
            AddParam(cmd, "domain", domainKey);
            AddParam(cmd, "take", capped);
        }, cancellationToken);
    }

    /// Idempotently mirror a profile's canonical embeddings into the vector
    /// table, and drop vector rows whose canonical row is gone.
    public async Task<RagVectorSyncOutcome> SyncProfileAsync(
        AiProfile profile,
        int? limit,
        bool dryRun,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var dimension = profile.Dimension ?? 0;
        var eligible = await _db.RagChunkEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id, cancellationToken);

        if (!SupportsDimension(profile.Dimension))
        {
            return RagVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, RagFailureReasons.EmbeddingDimensionUnsupported, dryRun);
        }
        if (!await TableExistsAsync(cancellationToken))
        {
            return RagVectorSyncOutcome.Unavailable(
                profile.Key, dimension, eligible, RagFailureReasons.PgvectorUnavailable, dryRun);
        }

        var indexedBefore = await CountIndexedAsync(profile.Id, cancellationToken);
        var missingBefore = Math.Max(0, eligible - indexedBefore);

        if (dryRun)
        {
            var wouldSync = limit is int lim ? Math.Min(missingBefore, lim) : missingBefore;
            log?.Invoke($"rag vector-sync (dry-run): {wouldSync} embedding(s) would be indexed for {profile.Key}.");
            return new RagVectorSyncOutcome(
                true, null, profile.Key, dimension, eligible, indexedBefore, missingBefore,
                Synced: 0, Removed: 0, SkippedDimensionMismatch: 0, Failed: 0, DryRun: true);
        }

        long synced = 0, skippedDim = 0, failed = 0, processed = 0;
        var cursor = Guid.Empty;
        var conn = await OpenAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit is int cap && processed >= cap) break;

            var remaining = limit is int lm ? Math.Min(PageSize, lm - (int)processed) : PageSize;
            var page = await FetchMissingPageAsync(conn, profile.Id, cursor, remaining, cancellationToken);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = row.EmbeddingId;
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
                    case RagVectorRowValidity.DimensionMismatch:
                        skippedDim++; // never truncate or pad
                        continue;
                    case RagVectorRowValidity.NonFinite:
                        failed++;
                        continue;
                }

                try
                {
                    await InsertVectorRowAsync(
                        conn, row.EmbeddingId, row.ChunkId, profile.Id, vector, cancellationToken);
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

        var removed = await RemoveOrphanedAsync(conn, profile.Id, cancellationToken);
        var indexedAfter = await CountIndexedAsync(profile.Id, cancellationToken);
        var missingAfter = Math.Max(0, eligible - indexedAfter);
        log?.Invoke(
            $"rag vector-sync: {profile.Key} synced {synced}, removed {removed} "
            + $"(skipped_dim {skippedDim}, failed {failed}); vector_indexed {indexedAfter}/{eligible}.");

        return new RagVectorSyncOutcome(
            true, null, profile.Key, dimension, eligible, indexedAfter, missingAfter,
            synced, removed, skippedDim, failed, DryRun: false);
    }

    /// Best-effort single-row upsert, used by the indexer as embeddings are
    /// written. NEVER throws for an unavailable backend and never touches the
    /// canonical row: a failure leaves a vector row missing for `vector-sync` to
    /// repair, and the canonical embedding stays the truth.
    public async Task<RagVectorUpsertOutcome> TryUpsertAsync(
        Guid embeddingId, Guid chunkId, Guid profileId, float[] vector, int? dimension,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsDimension(dimension)) return RagVectorUpsertOutcome.SkippedUnsupported;
        if (!await TableExistsAsync(cancellationToken)) return RagVectorUpsertOutcome.SkippedUnavailable;
        if (ClassifyVector(vector, SupportedDimension) != RagVectorRowValidity.Ok)
        {
            return RagVectorUpsertOutcome.Failed;
        }

        try
        {
            var conn = await OpenAsync(cancellationToken);
            await InsertVectorRowAsync(conn, embeddingId, chunkId, profileId, vector, cancellationToken);
            return RagVectorUpsertOutcome.Indexed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return RagVectorUpsertOutcome.Failed;
        }
    }

    /// Drop the vector rows for chunks that no longer exist or whose canonical
    /// embedding was replaced. The foreign key does most of this on its own; the
    /// explicit sweep exists because a database without pgvector has no such
    /// table, so the cleanup has to be a no-op there rather than an error.
    public async Task<long> RemoveOrphanedAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) return 0;
        var conn = await OpenAsync(cancellationToken);
        return await RemoveOrphanedAsync(conn, profileId, cancellationToken);
    }

    // ---- internals ----------------------------------------------------------

    private async Task<long> RemoveOrphanedAsync(
        DbConnection conn, Guid profileId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
DELETE FROM {Table} v
WHERE v.""ProfileId"" = @profileId
  AND NOT EXISTS (
      SELECT 1 FROM rag_chunk_embeddings e
      WHERE e.""Id"" = v.""EmbeddingId"" AND e.""ProfileId"" = v.""ProfileId"");";
        AddParam(cmd, "profileId", profileId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RagVectorNeighbor>?> RunAnnQueryAsync(
        string sql, int efSearch, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        DbTransaction? tx = null;
        try
        {
            var conn = await OpenAsync(cancellationToken);
            // hnsw.ef_search is a per-transaction GUC and must be at least the
            // number of rows we want back, or the index returns fewer than
            // `take` even when more neighbours exist.
            tx = await conn.BeginTransactionAsync(cancellationToken);

            await using (var set = conn.CreateCommand())
            {
                set.Transaction = tx;
                set.CommandText = $"SET LOCAL hnsw.ef_search = {efSearch.ToString(CultureInfo.InvariantCulture)};";
                await set.ExecuteNonQueryAsync(cancellationToken);
            }

            var items = new List<RagVectorNeighbor>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                bind(cmd);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new RagVectorNeighbor(reader.GetGuid(0), Math.Round(reader.GetDouble(1), 6)));
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
            return null; // → caller falls back to lexical
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private static int EfSearchFor(int want) => Math.Clamp(Math.Max(want, 40), 1, MaxEfSearch);

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        if (_tableExists is bool cached) return cached;

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
SELECT e.""Id"", e.""ChunkId"", e.""EmbeddingBytes""
FROM rag_chunk_embeddings e
WHERE e.""ProfileId"" = @profileId
  AND e.""Id"" > @cursor
  AND NOT EXISTS (SELECT 1 FROM {Table} v WHERE v.""EmbeddingId"" = e.""Id"")
ORDER BY e.""Id""
LIMIT @page;";
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "cursor", cursor);
        AddParam(cmd, "page", pageSize);

        var rows = new List<MissingRow>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MissingRow(reader.GetGuid(0), reader.GetGuid(1), (byte[])reader.GetValue(2)));
        }
        return rows;
    }

    private async Task InsertVectorRowAsync(
        DbConnection conn, Guid embeddingId, Guid chunkId, Guid profileId, float[] vector,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {Table} (""EmbeddingId"", ""ChunkId"", ""ProfileId"", embedding, ""CreatedAt"")
VALUES (@id, @chunk, @profile, @vec::vector, @now)
ON CONFLICT (""EmbeddingId"") DO NOTHING;";
        AddParam(cmd, "id", embeddingId);
        AddParam(cmd, "chunk", chunkId);
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

    /// Pure validation against the table's fixed dimension. A non-conforming
    /// vector is never truncated or padded into place.
    public static RagVectorRowValidity ClassifyVector(float[] vector, int expectedDimension)
    {
        if (vector.Length != expectedDimension) return RagVectorRowValidity.DimensionMismatch;
        return IsFiniteNonZero(vector) ? RagVectorRowValidity.Ok : RagVectorRowValidity.NonFinite;
    }

    private static bool IsFiniteNonZero(float[] vector)
    {
        double sumSq = 0;
        foreach (var v in vector)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
            sumSq += (double)v * v;
        }
        return sumSq > double.Epsilon;
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

    private readonly record struct MissingRow(Guid EmbeddingId, Guid ChunkId, byte[] EmbeddingBytes);
}

public enum RagVectorRowValidity
{
    Ok,
    DimensionMismatch,
    NonFinite,
}

public enum RagVectorUpsertOutcome
{
    Indexed,
    SkippedUnsupported,
    SkippedUnavailable,
    Failed,
}

/// Aggregate-only sync outcome. No raw vectors, no chunk text.
public sealed record RagVectorSyncOutcome(
    bool Available,
    string? Reason,
    string ProfileKey,
    int Dimension,
    long EligibleEmbeddings,
    long VectorIndexed,
    long MissingVectors,
    long Synced,
    long Removed,
    long SkippedDimensionMismatch,
    long Failed,
    bool DryRun)
{
    public static RagVectorSyncOutcome Unavailable(
        string profileKey, int dimension, long eligible, string reason, bool dryRun) =>
        new(false, reason, profileKey, dimension, eligible,
            VectorIndexed: 0, MissingVectors: eligible,
            Synced: 0, Removed: 0, SkippedDimensionMismatch: 0, Failed: 0, dryRun);
}

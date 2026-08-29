using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Ai.DocumentVisual;

/// One nearest visual unit. Ids and a score — never a vector, never a page.
public sealed record DocumentVisualNeighbor(Guid VisualUnitId, Guid FileItemId, double Score);

/// The pgvector accelerator for dense document-visual embeddings.
///
/// Canonical bytes in `document_visual_embeddings.EmbeddingBytes` are the truth;
/// this table is derived from them and can be dropped and rebuilt at any time.
/// All pgvector access is raw SQL through the EF connection and the `vector`
/// type is never mapped in the EF model, so SQLite unit tests and a PostgreSQL
/// without the extension simply report the backend unavailable and the caller
/// falls back to an exact scan.
///
/// THE OWNER PREDICATE IS IN THE SQL, ABOVE THE `LIMIT`, AND THERE IS NO ANN
/// INDEX BEHIND IT.
///
/// That combination is the whole point. `ORDER BY embedding <=> q LIMIT 10`
/// against a global HNSW with `WHERE OwnerUserId = …` is not an owner-prefiltered
/// nearest-neighbour search: the graph is traversed over everybody's vectors and
/// the predicate filters whatever the traversal happens to surface, so a person
/// with few documents in a large installation silently gets fewer and worse
/// results while their best match sat one hop off the path. Because the
/// migration creates no `hnsw` index on this table, PostgreSQL has exactly one
/// plan available: restrict through the joins, then rank the survivors exactly.
///
/// What the table still buys is real — the cosine is computed in the database
/// over the filtered rows, instead of shipping every candidate's 4.6 KiB of
/// float32 to the application on every question.
public sealed class DocumentVisualVectorIndexService
{
    public const int SupportedDimension = DocumentVisualProfiles.DenseDimension;

    private const string Table = "document_visual_embedding_vectors_1152";
    private const int PageSize = 200;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;

    /// Memoized per scope: the migration state cannot change inside one request
    /// or one CLI run, so `to_regclass` is probed at most once rather than once
    /// per unit across an indexing pass.
    private bool? _tableExists;

    public DocumentVisualVectorIndexService(AppDbContext db, IAiVectorSerializer serializer)
    {
        _db = db;
        _serializer = serializer;
    }

    public static bool SupportsDimension(int? dimension) => dimension == SupportedDimension;

    public async Task<bool> IsBackendAvailableAsync(
        int? dimension, CancellationToken cancellationToken = default)
        => SupportsDimension(dimension) && await TableExistsAsync(cancellationToken);

    public async Task<long> CountIndexedAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) return 0;

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {Table} WHERE \"ProfileId\" = @p;";
        AddParam(cmd, "p", profileId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// Nearest visual units belonging to ONE owner.
    ///
    /// Returns null when the accelerator is unavailable, which the caller reads
    /// as "fall back to the exact scan" rather than as "no results".
    ///
    /// Every eligibility clause `OwnerDocumentVisualEligibility` states in LINQ
    /// is restated here in SQL, because this query does not go through EF. The
    /// duplication is deliberate and it is TESTED against the LINQ path: two
    /// spellings of one rule are two spellings that will drift unless something
    /// compares them, so `DocumentVisualPgIntegrationTests` runs the same
    /// adversarial fixture through both.
    public async Task<IReadOnlyList<DocumentVisualNeighbor>?> SearchAsync(
        Guid profileId,
        Guid ownerUserId,
        float[] queryVector,
        IReadOnlyCollection<string> activeRenderProfileKeys,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) return Array.Empty<DocumentVisualNeighbor>();
        if (queryVector.Length != SupportedDimension) return null;
        if (activeRenderProfileKeys.Count == 0) return Array.Empty<DocumentVisualNeighbor>();
        if (!await TableExistsAsync(cancellationToken)) return null;

        var capped = Math.Clamp(take, 1, 500);
        var renderKeys = activeRenderProfileKeys.ToArray();

        var sql = $@"
SELECT u.""Id"", i.""FileItemId"", (1.0 - (v.embedding <=> @q::vector))::float8 AS score
FROM {Table} v
JOIN document_visual_units u ON u.""Id"" = v.""DocumentVisualUnitId""
JOIN document_visual_indexes i ON i.""Id"" = u.""DocumentVisualIndexId""
JOIN file_items f ON f.""Id"" = i.""FileItemId""
WHERE v.""ProfileId"" = @profileId
  AND i.""EmbeddingProfileId"" = @profileId
  AND i.""OwnerUserId"" = @ownerId
  AND i.""Status"" = 'completed'
  AND i.""RenderProfileKey"" = ANY(@renderKeys)
  -- THE FILE'S CURRENT BYTES. A document whose content was replaced has an
  -- index describing pixels that are no longer in it.
  AND i.""SourceBlobObjectId"" = f.""BlobObjectId""
  AND f.""OwnerUserId"" = @ownerId
  AND f.""DeletedAt"" IS NULL
  AND f.""PrivateVaultId"" IS NULL
  AND f.""MediaLibraryState"" = {MediaLibraryScopePolicy.ActiveDbValue}
ORDER BY score DESC, u.""Id""
LIMIT @take;";

        var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "q", ToVectorLiteral(queryVector));
        AddParam(cmd, "profileId", profileId);
        AddParam(cmd, "ownerId", ownerUserId);
        AddParam(cmd, "renderKeys", renderKeys);
        AddParam(cmd, "take", capped);

        var results = new List<DocumentVisualNeighbor>(capped);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DocumentVisualNeighbor(
                reader.GetGuid(0), reader.GetGuid(1), Math.Round(reader.GetDouble(2), 6)));
        }

        return results;
    }

    /// Mirror canonical dense embeddings into the accelerator, in pages.
    ///
    /// Idempotent and resumable: rows already present are skipped by the
    /// `NOT EXISTS`, so a run interrupted halfway continues where it stopped.
    public async Task<int> SyncAsync(
        Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) return 0;

        var synced = 0;
        var cursor = Guid.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _db.DocumentVisualEmbeddings.AsNoTracking()
                .Where(e => e.ProfileId == profileId
                            && e.Layout == DocumentVisualEmbeddingLayouts.Dense
                            && e.Dimension == SupportedDimension
                            && e.Id.CompareTo(cursor) > 0)
                .OrderBy(e => e.Id)
                .Take(PageSize)
                .Select(e => new { e.Id, e.DocumentVisualUnitId, e.EmbeddingBytes })
                .ToListAsync(cancellationToken);

            if (page.Count == 0) break;

            var conn = await OpenAsync(cancellationToken);
            foreach (var row in page)
            {
                cursor = row.Id;

                float[] vector;
                try
                {
                    vector = _serializer.Deserialize(row.EmbeddingBytes, SupportedDimension);
                }
                catch (ArgumentException)
                {
                    // A malformed canonical row is skipped, never coerced. It is
                    // a corruption to repair by re-embedding.
                    continue;
                }

                if (!vector.All(float.IsFinite)) continue;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {Table} (""EmbeddingId"", ""DocumentVisualUnitId"", ""ProfileId"", embedding, ""CreatedAt"")
VALUES (@id, @unit, @profile, @v::vector, now())
ON CONFLICT (""EmbeddingId"") DO NOTHING;";
                AddParam(cmd, "id", row.Id);
                AddParam(cmd, "unit", row.DocumentVisualUnitId);
                AddParam(cmd, "profile", profileId);
                AddParam(cmd, "v", ToVectorLiteral(vector));
                synced += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return synced;
    }

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        if (_tableExists is bool cached) return cached;

        if (!_db.Database.IsNpgsql())
        {
            _tableExists = false; // SQLite/other: never run PostgreSQL-only SQL.
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

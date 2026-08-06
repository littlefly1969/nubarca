using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;

namespace NubArca.Api.Ai.Photos;

// Owner-private "similar photos" lookup (photo similarity v0). Exact-scan cosine
// over the stored EmbeddingBytes — NO pgvector. Results are ALWAYS constrained to
// the caller's own active FileItems by joining BlobEmbedding -> FileItem on
// owner; a blob shared by two owners therefore only ever surfaces the caller's
// own files. Returns owner-visible file ids + names + a rounded score; NEVER raw
// vectors, blob ids, SHA, or storage keys.
public sealed class PhotoSimilarityService
{
    // Bounds the in-memory exact scan (v0). The owner's image set is small in
    // practice; pgvector + ANN replaces this in the explicit pgvector phase.
    private const int MaxCandidatesScanned = 50_000;

    private readonly AppDbContext _db;
    private readonly PhotoEmbeddingProfileService _profiles;
    private readonly PhotoVectorIndexService _vectors;
    private readonly IAiVectorSerializer _serializer;

    public PhotoSimilarityService(
        AppDbContext db,
        PhotoEmbeddingProfileService profiles,
        PhotoVectorIndexService vectors,
        IAiVectorSerializer serializer)
    {
        _db = db;
        _profiles = profiles;
        _vectors = vectors;
        _serializer = serializer;
    }

    // Returns null when the query file does not exist or is not owned by the
    // caller (caller maps that to 404). Otherwise returns a result whose Items
    // may be empty (no usable active profile, query not indexed for it, or no
    // neighbours). `profileKeyOverride` is the operator-CLI escape hatch; the
    // normal API path passes null and uses the configured active profile (or the
    // documented default fallback). The search ALWAYS reads a single profile's
    // embeddings — profiles are never mixed.
    public async Task<SimilarPhotosResult?> FindSimilarAsync(
        Guid ownerUserId,
        Guid fileItemId,
        int limit,
        string? profileKeyOverride = null,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 100);

        // Owner ownership of the QUERY file is mandatory.
        var queryBlobId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryBlobId is null)
        {
            return null; // not found / not owned → 404
        }

        var resolution = await _profiles.ResolveActiveProfileAsync(profileKeyOverride, cancellationToken);
        if (!resolution.Usable || resolution.Profile is null)
        {
            // No usable active profile: a clean empty result for the API; the
            // sanitized reason lets the operator CLI explain why.
            return new SimilarPhotosResult(
                ProfileAvailable: false, QueryIndexed: false, Array.Empty<SimilarPhotoItem>(),
                resolution.UnavailableReason);
        }

        var profile = resolution.Profile;

        var queryBytes = await _db.BlobEmbeddings.AsNoTracking()
            .Where(e => e.BlobObjectId == queryBlobId.Value && e.ProfileId == profile.Id)
            .Select(e => e.EmbeddingBytes)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryBytes is null)
        {
            // The query photo has not been indexed for the active profile yet.
            return new SimilarPhotosResult(ProfileAvailable: true, QueryIndexed: false, Array.Empty<SimilarPhotoItem>(), null);
        }

        var queryVector = _serializer.Deserialize(queryBytes);

        // pgvector ANN path: used ONLY when this profile is actually vector-
        // indexed (≥1 row in its dimension's vector table). Profile-keyed and
        // owner-scoped inside the query; never mixes profiles. When the backend
        // is unavailable (e.g. SQLite, no pgvector, non-1152 dim) or the profile
        // has no vector rows, CountIndexedAsync returns 0 and we fall through to
        // the exact-scan fallback below — same owner-private result shape.
        if (await _vectors.CountIndexedAsync(profile.Id, cancellationToken) > 0)
        {
            var neighbours = await _vectors.SearchAsync(
                profile.Id, queryVector, ownerUserId, fileItemId, take, cancellationToken);
            if (neighbours is not null)
            {
                var vectorItems = neighbours
                    .Select(n => new SimilarPhotoItem(n.FileItemId, n.Name, n.Score))
                    .ToList();
                return new SimilarPhotosResult(
                    ProfileAvailable: true, QueryIndexed: true,
                    await WithGeometryAsync(ownerUserId, vectorItems, cancellationToken), null);
            }
        }

        // Exact-scan fallback (no pgvector). Candidate embeddings, owner-scoped
        // via the FileItem join. A blob with
        // several of the owner's FileItems yields one row per file (intentional:
        // results are owner-visible files). Cross-owner files are impossible here.
        //
        // Ordered by FileItem.Id BEFORE the safety Take so the bounded scan is
        // deterministic (and to silence EF's "Take without OrderBy" warning):
        // when an owner has more than MaxCandidatesScanned image embeddings the
        // same stable subset is scanned every time, rather than an arbitrary one.
        // pgvector ANN (Phase 2) removes the cap entirely.
        var candidates = await (
            from e in _db.BlobEmbeddings.AsNoTracking()
            join f in _db.FileItems.AsNoTracking() on e.BlobObjectId equals f.BlobObjectId
            where e.ProfileId == profile.Id
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                // Slice 3: excluded files never appear as similarity candidates.
                && f.MediaLibraryState == MediaLibraryState.Active
                && f.Id != fileItemId
            orderby f.Id
            select new CandidateRow(f.Id, f.Name, e.EmbeddingBytes))
            .Take(MaxCandidatesScanned)
            .ToListAsync(cancellationToken);

        var scored = new List<SimilarPhotoItem>(candidates.Count);
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] vector;
            try
            {
                vector = _serializer.Deserialize(c.EmbeddingBytes);
            }
            catch
            {
                continue; // corrupt/incompatible row → skip, never surface internals
            }

            if (vector.Length != queryVector.Length)
            {
                continue; // different profile dimension → not comparable
            }

            var score = Cosine(queryVector, vector);
            scored.Add(new SimilarPhotoItem(c.FileItemId, c.Name, Math.Round(score, 6)));
        }

        var items = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.FileItemId) // deterministic tie-break
            .Take(take)
            .ToList();

        return new SimilarPhotosResult(
            ProfileAvailable: true, QueryIndexed: true,
            await WithGeometryAsync(ownerUserId, items, cancellationToken), null);
    }

    // ------------------------------------------------------------------
    // Similar Photos Explorer: threshold-filtered, keyset-paginated search.
    // ------------------------------------------------------------------

    // Default/maximum page size for the explorer (the HTTP endpoint also caps).
    public const int DefaultPageSize = 60;
    public const int MaxPageSize = 100;
    // Upper bound on how deep the explorer can page into one source photo's
    // neighbours — a hard, indexed-friendly cap so neither path ever does an
    // unbounded scan. 500 similar photos is far beyond any real exploration.
    private const int MaxExplorable = 500;

    // Paginated, threshold-filtered similar-photo search for the explorer UI.
    // `minSimilarity` keeps only neighbours with cosine similarity >= the value
    // (0..1). Results are ordered most-similar-first with a deterministic
    // (score desc, FileItemId asc) tie-break, and paginated by an opaque keyset
    // `cursor`. Returns null when the source file is missing/not owned (404).
    // Same owner-private guarantees and safe DTO as FindSimilarAsync.
    public async Task<SimilarPhotosPage?> FindSimilarPageAsync(
        Guid ownerUserId,
        Guid fileItemId,
        double minSimilarity,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var minSim = Math.Clamp(minSimilarity, 0.0, 1.0);

        var queryBlobId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryBlobId is null)
        {
            return null; // not found / not owned → 404
        }

        var resolution = await _profiles.ResolveActiveProfileAsync(null, cancellationToken);
        if (!resolution.Usable || resolution.Profile is null)
        {
            return new SimilarPhotosPage(
                ProfileAvailable: false, QueryIndexed: false, Array.Empty<SimilarPhotoItem>(),
                NextCursor: null, HasMore: false, resolution.UnavailableReason);
        }

        var profile = resolution.Profile;

        var queryBytes = await _db.BlobEmbeddings.AsNoTracking()
            .Where(e => e.BlobObjectId == queryBlobId.Value && e.ProfileId == profile.Id)
            .Select(e => e.EmbeddingBytes)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryBytes is null)
        {
            return new SimilarPhotosPage(
                ProfileAvailable: true, QueryIndexed: false, Array.Empty<SimilarPhotoItem>(),
                NextCursor: null, HasMore: false, null);
        }

        var queryVector = _serializer.Deserialize(queryBytes);

        // Build a deterministically ordered candidate list (score desc, id asc),
        // bounded by MaxExplorable, above the threshold. Both backends produce
        // the same shape; the service then applies the keyset cursor uniformly.
        IReadOnlyList<SimilarPhotoItem> ordered;

        if (await _vectors.CountIndexedAsync(profile.Id, cancellationToken) > 0)
        {
            var neighbours = await _vectors.SearchTopAsync(
                profile.Id, queryVector, ownerUserId, fileItemId, minSim, MaxExplorable, cancellationToken);
            if (neighbours is not null)
            {
                ordered = neighbours
                    .Select(n => new SimilarPhotoItem(n.FileItemId, n.Name, n.Score))
                    .OrderByDescending(s => s.Score)
                    .ThenBy(s => s.FileItemId)
                    .ToList();
                var vectorPage = Paginate(ordered, cursor, pageSize, queryIndexed: true);
                return vectorPage with
                {
                    Items = await WithGeometryAsync(ownerUserId, vectorPage.Items, cancellationToken),
                };
            }
        }

        // Exact-scan fallback (no pgvector): score owner-private candidates in
        // memory, filter by threshold, order, then paginate.
        var candidates = await (
            from e in _db.BlobEmbeddings.AsNoTracking()
            join f in _db.FileItems.AsNoTracking() on e.BlobObjectId equals f.BlobObjectId
            where e.ProfileId == profile.Id
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                // Slice 3: excluded files never appear as similarity candidates.
                && f.MediaLibraryState == MediaLibraryState.Active
                && f.Id != fileItemId
            orderby f.Id
            select new CandidateRow(f.Id, f.Name, e.EmbeddingBytes))
            .Take(MaxCandidatesScanned)
            .ToListAsync(cancellationToken);

        var scored = new List<SimilarPhotoItem>(candidates.Count);
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] vector;
            try
            {
                vector = _serializer.Deserialize(c.EmbeddingBytes);
            }
            catch
            {
                continue;
            }

            if (vector.Length != queryVector.Length)
            {
                continue;
            }

            var score = Math.Round(Cosine(queryVector, vector), 6);
            if (score >= minSim)
            {
                scored.Add(new SimilarPhotoItem(c.FileItemId, c.Name, score));
            }
        }

        ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.FileItemId)
            .Take(MaxExplorable)
            .ToList();

        // Geometry is attached to the RETURNED PAGE only, after the cursor has
        // already selected it — never to the full candidate set.
        var page = Paginate(ordered, cursor, pageSize, queryIndexed: true);
        return page with
        {
            Items = await WithGeometryAsync(ownerUserId, page.Items, cancellationToken),
        };
    }

    // Attach each item's DISPLAY pixel dimensions, resolved from the persisted
    // BlobMetadata row extracted at ingestion through the same
    // ImageDisplayDimensions helper the library and album listings use — so an
    // EXIF-rotated photo reports the proportions it is actually rendered at and
    // the shared media wall reserves the identical tile in every surface.
    //
    // Deliberately a separate, final step rather than part of the candidate
    // query: it runs ONLY over the items actually being returned (one page, so
    // at most MaxPageSize rows), it is identical for the pgvector and exact-scan
    // backends, and — most importantly — it cannot influence scoring, the
    // threshold, the ordering or the cursor, because it runs after all of those
    // are decided and preserves the input order exactly.
    //
    // No image bytes are opened and no derivative is generated. A file whose blob
    // has no metadata row (or no extracted dimensions) simply keeps null/null and
    // the client falls back.
    private async Task<IReadOnlyList<SimilarPhotoItem>> WithGeometryAsync(
        Guid ownerUserId,
        IReadOnlyList<SimilarPhotoItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var ids = items.Select(i => i.FileItemId).ToArray();

        // Owner-scoped as defence in depth: the ids already come from an
        // owner-scoped query, so this can only ever narrow, never widen.
        var rows = await (
            from f in _db.FileItems.AsNoTracking()
            join m in _db.BlobMetadata.AsNoTracking() on f.BlobObjectId equals m.BlobObjectId
            where ids.Contains(f.Id) && f.OwnerUserId == ownerUserId
            select new { f.Id, m.Width, m.Height, m.Orientation })
            .ToListAsync(cancellationToken);

        var geometry = new Dictionary<Guid, (int? Width, int? Height)>(rows.Count);
        foreach (var row in rows)
        {
            // DISPLAY dimensions, exactly as the library/album listings resolve
            // them. The stored pair is the CODED size (EXIF orientation is kept
            // apart), while every derivative renderer auto-orients, so a phone
            // portrait shot lands here as a landscape pair. Handing that to the
            // grid reserved a landscape tile for a portrait thumbnail — the
            // mismatch the wall then had to paper over.
            geometry[row.Id] = ImageDisplayDimensions.Resolve(row.Width, row.Height, row.Orientation);
        }

        var enriched = new List<SimilarPhotoItem>(items.Count);
        foreach (var item in items)
        {
            // Only a COMPLETE, positive pair is usable as an aspect ratio; a half
            // known dimension is as unusable as none, so it is reported as
            // unknown rather than as a misleading partial value.
            if (geometry.TryGetValue(item.FileItemId, out var dims)
                && dims.Width is > 0
                && dims.Height is > 0)
            {
                enriched.Add(item with { Width = dims.Width, Height = dims.Height });
            }
            else
            {
                enriched.Add(item);
            }
        }

        return enriched;
    }

    // Apply the (score desc, FileItemId asc) keyset cursor over a pre-ordered
    // list and return one page plus the next cursor. Pure/in-memory so both
    // backends paginate identically and exactly (no DB float-equality concerns).
    private static SimilarPhotosPage Paginate(
        IReadOnlyList<SimilarPhotoItem> ordered, string? cursor, int pageSize, bool queryIndexed)
    {
        var startIndex = 0;
        if (TryDecodeCursor(cursor, out var boundScore, out var boundId))
        {
            // First item strictly after the boundary in (score desc, id asc).
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                var after = s.Score < boundScore
                    || (s.Score == boundScore && s.FileItemId.CompareTo(boundId) > 0);
                if (after)
                {
                    startIndex = i;
                    break;
                }
                startIndex = i + 1;
            }
        }

        var page = new List<SimilarPhotoItem>(pageSize);
        var end = Math.Min(startIndex + pageSize, ordered.Count);
        for (var i = startIndex; i < end; i++)
        {
            page.Add(ordered[i]);
        }

        var hasMore = end < ordered.Count;
        var next = hasMore && page.Count > 0
            ? EncodeCursor(page[^1].Score, page[^1].FileItemId)
            : null;

        return new SimilarPhotosPage(
            ProfileAvailable: true, QueryIndexed: queryIndexed, page, next, hasMore, null);
    }

    // Opaque, URL-safe cursor. Encodes only the last item's already-exposed
    // (rounded score, FileItem id) — no storage internals. "R" round-trips the
    // exact double so the keyset boundary is bit-stable across pages.
    private static string EncodeCursor(double score, Guid fileItemId)
    {
        var raw = $"{score.ToString("R", CultureInfo.InvariantCulture)}|{fileItemId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? cursor, out double score, out Guid fileItemId)
    {
        score = 0;
        fileItemId = Guid.Empty;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var sep = raw.IndexOf('|');
            if (sep <= 0)
            {
                return false;
            }
            return double.TryParse(raw.AsSpan(0, sep), NumberStyles.Float, CultureInfo.InvariantCulture, out score)
                && Guid.TryParse(raw.AsSpan(sep + 1), out fileItemId);
        }
        catch
        {
            return false; // malformed cursor → treat as first page
        }
    }

    // ------------------------------------------------------------------
    // Diagnostic: similarity-score histogram + threshold comparison.
    // ------------------------------------------------------------------

    // The thresholds the explorer histogram reports on (high → low).
    public static readonly double[] HistogramThresholds =
        { 0.95, 0.90, 0.85, 0.80, 0.75, 0.70, 0.65, 0.60, 0.50, 0.40, 0.30 };

    // Owner-private diagnostic for one source photo: the exact (full-scan) score
    // distribution of the owner's other indexed photos against it, plus a
    // per-threshold comparison of three counts — exact-scan (C# ground truth),
    // pgvector exact count (SQL ground truth), and pgvector ANN-returned count
    // (what the explorer actually surfaces, bounded by MaxExplorable). The gap
    // between the exact counts and the ANN-returned count reveals HNSW recall
    // limits. Returns null when the source file is missing/not owned. Counts and
    // score buckets only — never ids/vectors/SHA/paths.
    public async Task<SimilarityHistogram?> ComputeHistogramAsync(
        Guid ownerUserId,
        Guid fileItemId,
        string? profileKeyOverride,
        CancellationToken cancellationToken = default)
    {
        var queryBlobId = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => (Guid?)f.BlobObjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (queryBlobId is null)
        {
            return null;
        }

        var resolution = await _profiles.ResolveActiveProfileAsync(profileKeyOverride, cancellationToken);
        if (!resolution.Usable || resolution.Profile is null)
        {
            return new SimilarityHistogram(
                false, false, false, 0,
                Array.Empty<HistogramBucket>(), Array.Empty<HistogramThreshold>(),
                resolution.UnavailableReason);
        }

        var profile = resolution.Profile;
        var queryBytes = await _db.BlobEmbeddings.AsNoTracking()
            .Where(e => e.BlobObjectId == queryBlobId.Value && e.ProfileId == profile.Id)
            .Select(e => e.EmbeddingBytes)
            .FirstOrDefaultAsync(cancellationToken);
        if (queryBytes is null)
        {
            return new SimilarityHistogram(true, false, false, 0,
                Array.Empty<HistogramBucket>(), Array.Empty<HistogramThreshold>(), null);
        }

        var queryVector = _serializer.Deserialize(queryBytes);

        // Exact-scan ground truth over ALL owner candidates (independent of HNSW).
        var candidates = await (
            from e in _db.BlobEmbeddings.AsNoTracking()
            join f in _db.FileItems.AsNoTracking() on e.BlobObjectId equals f.BlobObjectId
            where e.ProfileId == profile.Id
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                // Slice 3: excluded files never appear as similarity candidates.
                && f.MediaLibraryState == MediaLibraryState.Active
                && f.Id != fileItemId
            orderby f.Id
            select e.EmbeddingBytes)
            .Take(MaxCandidatesScanned)
            .ToListAsync(cancellationToken);

        var scores = new List<double>(candidates.Count);
        foreach (var bytes in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] vector;
            try { vector = _serializer.Deserialize(bytes); }
            catch { continue; }
            if (vector.Length != queryVector.Length) continue;
            scores.Add(Math.Round(Cosine(queryVector, vector), 6));
        }

        // 0.05-wide buckets across [0,1]; only the populated ones are returned.
        var bucketCounts = new int[20];
        foreach (var s in scores)
        {
            var idx = Math.Clamp((int)(s / 0.05), 0, 19);
            bucketCounts[idx]++;
        }
        var buckets = new List<HistogramBucket>();
        for (var i = 0; i < 20; i++)
        {
            if (bucketCounts[i] > 0)
            {
                buckets.Add(new HistogramBucket(Math.Round(i * 0.05, 2), Math.Round((i + 1) * 0.05, 2), bucketCounts[i]));
            }
        }

        var backendAvailable =
            await _vectors.CountIndexedAsync(profile.Id, cancellationToken) > 0;

        var rows = new List<HistogramThreshold>(HistogramThresholds.Length);
        foreach (var t in HistogramThresholds)
        {
            var exactScan = scores.Count(s => s >= t);
            long? pgExact = null;
            int? annReturned = null;
            if (backendAvailable)
            {
                pgExact = await _vectors.CountAboveThresholdAsync(
                    profile.Id, queryVector, ownerUserId, fileItemId, t, cancellationToken);
                var ann = await _vectors.SearchTopAsync(
                    profile.Id, queryVector, ownerUserId, fileItemId, t, MaxExplorable, cancellationToken);
                annReturned = ann?.Count;
            }
            rows.Add(new HistogramThreshold(t, exactScan, pgExact, annReturned));
        }

        return new SimilarityHistogram(
            ProfileAvailable: true, QueryIndexed: true, VectorBackendAvailable: backendAvailable,
            TotalCandidates: scores.Count, buckets, rows, null);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        if (na <= double.Epsilon || nb <= double.Epsilon)
        {
            return 0.0;
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private readonly record struct CandidateRow(Guid FileItemId, string Name, byte[] EmbeddingBytes);
}

// Owner-private result. No raw vectors or storage internals. `UnavailableReason`
// is a sanitized token (e.g. "profile-not-found", "capability-mismatch") set only
// when ProfileAvailable is false — safe to surface to the owner/CLI; never an id,
// path, or secret.
public sealed record SimilarPhotosResult(
    bool ProfileAvailable,
    bool QueryIndexed,
    IReadOnlyList<SimilarPhotoItem> Items,
    string? UnavailableReason = null);

// `Width`/`Height` are the ORIGINAL media's DISPLAY pixel dimensions, derived
// from the already-persisted BlobMetadata extracted at ingestion — never measured
// from the bytes at request time, and never a derivative's size. The stored pair
// is the coded size with EXIF orientation held separately, so it is put through
// ImageDisplayDimensions.Resolve exactly as the library and album listings do;
// a quarter-turn orientation therefore reports the swapped pair and a client lays
// the result out at the proportions it will actually render at. Both are null
// when the blob has no extracted dimensions (pre-metadata import, extraction
// failure), which callers must treat as "unknown" and fall back on. Optional
// positional parameters keep every existing construction site and the JSON shape
// additive/backward-compatible.
public sealed record SimilarPhotoItem(
    Guid FileItemId,
    string Name,
    double Score,
    int? Width = null,
    int? Height = null);

// Paginated explorer result. Same owner-private guarantees as SimilarPhotosResult
// plus a keyset cursor. `NextCursor` is null at the end of the result set.
// `UnavailableReason` is the sanitized token set only when ProfileAvailable is
// false. No raw vectors, blob ids, SHA, storage keys, or profile internals.
public sealed record SimilarPhotosPage(
    bool ProfileAvailable,
    bool QueryIndexed,
    IReadOnlyList<SimilarPhotoItem> Items,
    string? NextCursor,
    bool HasMore,
    string? UnavailableReason = null);

// Diagnostic histogram for one source photo. Counts + score-bucket ranges only;
// no ids, vectors, SHA, or paths. `PgExactCount`/`AnnReturnedCount` are null when
// the pgvector backend is unavailable (exact-scan only).
public sealed record HistogramBucket(double Min, double Max, int Count);

public sealed record HistogramThreshold(
    double Threshold,
    int ExactScanCount,
    long? PgExactCount,
    int? AnnReturnedCount);

public sealed record SimilarityHistogram(
    bool ProfileAvailable,
    bool QueryIndexed,
    bool VectorBackendAvailable,
    int TotalCandidates,
    IReadOnlyList<HistogramBucket> Buckets,
    IReadOnlyList<HistogramThreshold> Thresholds,
    string? UnavailableReason);

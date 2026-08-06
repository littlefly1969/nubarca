using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Files;

namespace NubArca.Api.Ai.Photos;

// PHYSICAL-FILTER-FIRST, semantic-ranked gallery page.
//
// The pipeline is mandatory and enforced here:
//   owner gallery ∩ people ∩ favourites ∩ dates ∩ …   (physical candidate set)
//     → embed the semantic residual with the active profile's text tower
//     → rank ONLY inside that candidate set (exact scan restricted to it)
//     → reduce to the best Top-K
//     → stable (score desc, id asc) cursor pagination
//     → total = size of the reduced semantic result set
//
// It never does a global semantic top-N and filters afterwards, so a valid match
// that lies outside the global semantic prefix stays discoverable once a
// selective physical filter is applied. When the active profile / text tower is
// unavailable it returns an Unavailable page (never fake scores, never originals,
// never a synchronous embed of missing media).
public sealed class GallerySemanticQueryService
{
    // Bumped if the ordering/fingerprint contract changes so old cursors 400.
    public const string OrderingVersion = "sv2";
    // Rank-rehydration is capped at 100 ids per page (ListGalleryImagesByRankAsync).
    public const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly PhotoEmbeddingProfileService _profiles;
    private readonly IAiBackendResolver _backends;
    private readonly PhotoVectorIndexService _vectors;
    private readonly IAiVectorSerializer _serializer;
    private readonly IFileItemService _files;
    private readonly AiNaturalGallerySearchOptions _options;

    public GallerySemanticQueryService(
        AppDbContext db,
        PhotoEmbeddingProfileService profiles,
        IAiBackendResolver backends,
        PhotoVectorIndexService vectors,
        IAiVectorSerializer serializer,
        IFileItemService files,
        IOptions<AiOptions> options)
    {
        _db = db;
        _profiles = profiles;
        _backends = backends;
        _vectors = vectors;
        _serializer = serializer;
        _files = files;
        _options = options.Value.NaturalGallerySearch;
    }

    public async Task<GallerySemanticPage> SearchAsync(
        Guid ownerUserId,
        int limit,
        string? cursor,
        ImageFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (!filters.HasSemanticQuery)
        {
            throw new InvalidOperationException("SearchAsync requires a semantic query.");
        }

        var query = filters.SemanticQuery!.Trim();
        var topK = _options.ClampTopK(filters.SemanticTopK);
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);

        // Resolve the active photo profile + its text tower. Both must be usable
        // and dimension-correct; otherwise semantic ranking is unavailable.
        var profileResolution = await _profiles.ResolveActiveProfileAsync(null, cancellationToken);
        if (!profileResolution.Usable || profileResolution.Profile is null
            || profileResolution.Profile.Dimension != PhotoVectorIndexService.SupportedDimension)
        {
            return GallerySemanticPage.Unavailable(topK,
                profileResolution.UnavailableReason ?? AiUnavailableReasons.ProfileDimensionInvalid);
        }
        var profile = profileResolution.Profile;

        var backendResolution = await _backends.ResolveForProfileKeyAsync<ITextEmbedder>(
            profile.Key, cancellationToken);
        if (!backendResolution.Resolution.IsAvailable || backendResolution.Backend is null)
        {
            return GallerySemanticPage.Unavailable(topK, backendResolution.Resolution.UnavailableReason);
        }

        // Bind (or reject) the cursor BEFORE the expensive work — an invalid or
        // mismatched cursor is a client error, never a stale page.
        var fingerprint = SemanticFingerprint(filters, profile.Key);
        double? cursorScore = null;
        Guid cursorId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!ImageCursor.TryParse(cursor, out var parsed)
                || parsed.PrimaryKind != ImageCursor.KindScore
                || parsed.PrimaryScore is null
                || !parsed.MatchesFilter(fingerprint))
            {
                throw new SemanticSearchCursorException();
            }
            cursorScore = parsed.PrimaryScore;
            cursorId = parsed.Id;
        }

        // PHYSICAL FIRST: build the owner-scoped candidate set from the physical
        // filters, then apply the semantic-only small-image quality gate.
        // Truncation (very broad filter) is disclosed, never silent.
        var candidates = await _files.ListPhysicalGalleryCandidatesAsync(
            ownerUserId, filters, _options.MaxSemanticCandidates + 1, cancellationToken);
        var truncated = candidates.Count > _options.MaxSemanticCandidates;
        if (truncated)
        {
            candidates = candidates.Take(_options.MaxSemanticCandidates).ToList();
        }
        var physicalCount = candidates.Count;

        if (physicalCount == 0)
        {
            return GallerySemanticPage.Empty(topK, truncated, physicalCount);
        }

        // Embed the semantic residual with the active profile's text tower.
        var embedding = await backendResolution.Backend.EmbedTextAsync(query, profile, cancellationToken);
        if (embedding.Dimension != profile.Dimension
            || embedding.Vector.Length != PhotoVectorIndexService.SupportedDimension)
        {
            return GallerySemanticPage.Unavailable(topK, AiUnavailableReasons.ProfileDimensionInvalid);
        }

        // Rank INSIDE the candidate set. pgvector exact-scan-restricted path when
        // available; in-process exact scan over canonical embeddings otherwise.
        // Only media WITH an embedding for the active profile participate.
        var candidateIds = candidates.Select(c => c.Id).ToArray();
        IReadOnlyList<ScoredHit> ordered;
        var vectorHits = await _vectors.SearchWithinCandidatesAsync(
            profile.Id, embedding.Vector, ownerUserId, candidateIds, topK, cancellationToken);
        if (vectorHits is not null)
        {
            ordered = vectorHits
                .Select(h => new ScoredHit(h.FileItemId, h.Score))
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Id)
                .Take(topK)
                .ToList();
        }
        else
        {
            ordered = await RankInProcessAsync(profile.Id, embedding.Vector, candidates, topK, cancellationToken);
        }

        var total = ordered.Count;

        // Stable keyset seek by (score desc, id asc). Bounded (total ≤ Top-K ≤ 500)
        // so we can hold the ranked ids in memory and slice deterministically —
        // no duplicates, no gaps, identical denominator on every page.
        var start = 0;
        if (cursorScore is double cs)
        {
            start = ordered.Count;
            for (var i = 0; i < ordered.Count; i++)
            {
                var h = ordered[i];
                if (h.Score < cs || (h.Score == cs && h.Id.CompareTo(cursorId) > 0))
                {
                    start = i;
                    break;
                }
            }
        }

        var pageHits = ordered.Skip(start).Take(pageSize).ToList();
        var pageIds = pageHits.Select(h => h.Id).ToList();
        var hydrated = await _files.ListGalleryImagesByRankAsync(ownerUserId, pageIds, cancellationToken);

        var hasMore = start + pageHits.Count < total;
        string? nextCursor = null;
        if (hasMore && pageHits.Count > 0)
        {
            var last = pageHits[^1];
            nextCursor = ImageCursor.FromScore(last.Score, last.Id, fingerprint).Encode();
        }

        // Disclose "still indexing" generically when many physical candidates
        // have no embedding yet (never expose model/index internals).
        var embeddedTotal = await _files.CountEmbeddedGalleryCandidatesAsync(
            ownerUserId, filters, profile.Id, cancellationToken);
        var manyUnindexed = physicalCount - embeddedTotal > Math.Max(10, physicalCount / 5);

        return new GallerySemanticPage(
            hydrated, nextCursor, hasMore, total, topK,
            truncated, embeddedTotal, physicalCount,
            Available: true, UnavailableReason: null, StillIndexingManyItems: manyUnindexed);
    }

    // In-process exact cosine rank over the candidate blobs' canonical embeddings.
    // Used on SQLite/non-pgvector (tests) and as a safety fallback. Ranks ONLY
    // inside the candidate set — the same physical-first guarantee as the SQL path.
    private async Task<IReadOnlyList<ScoredHit>> RankInProcessAsync(
        Guid profileId, float[] queryVector, IReadOnlyList<GalleryCandidateRef> candidates,
        int topK, CancellationToken cancellationToken)
    {
        var blobIds = candidates.Select(c => c.BlobObjectId).Distinct().ToList();
        var vectors = await _db.BlobEmbeddings.AsNoTracking()
            .Where(e => e.ProfileId == profileId && blobIds.Contains(e.BlobObjectId))
            .Select(e => new { e.BlobObjectId, e.EmbeddingBytes })
            .ToListAsync(cancellationToken);

        var scoreByBlob = new Dictionary<Guid, double>(vectors.Count);
        foreach (var v in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vector = _serializer.Deserialize(v.EmbeddingBytes);
                if (vector.Length == queryVector.Length)
                {
                    scoreByBlob[v.BlobObjectId] = Math.Round(Cosine(queryVector, vector), 6);
                }
            }
            catch
            {
                // A corrupt canonical row is skipped without leaking internals.
            }
        }

        return candidates
            .Where(c => scoreByBlob.ContainsKey(c.BlobObjectId))
            .Select(c => new ScoredHit(c.Id, scoreByBlob[c.BlobObjectId]))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id)
            .Take(topK)
            .ToList();
    }

    // Query identity for the semantic cursor: physical filter fingerprint +
    // semantic residual + Top-K (all already in ImageFilters.Fingerprint) folded
    // with the active embedding profile key + ordering version. Any change (a
    // filter, the semantic text, Top-K, or the profile) yields a new fingerprint
    // → an old cursor fails safely. Raw query text is NEVER placed in the cursor.
    private static string SemanticFingerprint(ImageFilters filters, string profileKey)
    {
        var raw = $"{filters.Fingerprint() ?? ""}|prof={profileKey}|ov={OrderingVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
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
        return na <= double.Epsilon || nb <= double.Epsilon ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private readonly record struct ScoredHit(Guid Id, double Score);
}

// One physical-filter-first semantic-ranked gallery page. TotalCount is the size
// of the REDUCED semantic result set (≤ Top-K, ≤ embedded candidates), and is
// stable across the pages of one query — the correct slideshow denominator.
public sealed record GallerySemanticPage(
    IReadOnlyList<ImageItem> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount,
    int SemanticTopK,
    bool CandidatesTruncated,
    int EmbeddedCandidateTotal,
    int PhysicalCandidateCount,
    bool Available,
    string? UnavailableReason,
    bool StillIndexingManyItems = false)
{
    public static GallerySemanticPage Unavailable(int topK, string? reason) =>
        new(Array.Empty<ImageItem>(), null, false, 0, topK, false, 0, 0, false, reason);

    public static GallerySemanticPage Empty(int topK, bool truncated, int physicalCount) =>
        new(Array.Empty<ImageItem>(), null, false, 0, topK, truncated, 0, physicalCount, true, null);
}

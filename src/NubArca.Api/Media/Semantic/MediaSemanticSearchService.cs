using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Files;

namespace NubArca.Api.Media.Semantic;

// VSEM-03: ONE query → photos AND temporal video results, in one ranked page.
//
// The mandatory pipeline order is enforced here:
//   authenticated owner scope
//     → deleted/Vault/media-library + requested physical filters
//       (SemanticMediaCandidateService — bounded FileItem candidate scopes)
//     → ONE text embedding with the active paired SigLIP2 profile
//     → photo + video vector ranking, each strictly INSIDE its candidate scope
//     → video samples grouped into their parent segments (bounded, deduped)
//     → same-profile merge by comparable cosine score
//     → stable (score desc, id asc) cursor pagination
//     → owner-visible MediaItem DTO projection
//
// It never ranks globally and filters afterwards. Photos and videos merge ONLY
// because they share the same AiProfile (one image tower + its paired text
// tower); embeddings of any other profile never participate. There is no
// modality boost: scores are the same cosine contract on the same normalized
// space, tie-broken deterministically by FileItem id.
//
// SEARCH-SEM-01 — WHY THIS NO LONGER TRUNCATES BY GUID
// ----------------------------------------------------
// This service used to take the first 20,000 candidates ordered by FileItem id
// (and, for video, the first 20,000 samples ordered by BlobObjectId). GUID order
// has no relationship to relevance, so that was not a sample of the library — it
// was the SAME arbitrary prefix on every query, and everything after it was
// unrankable no matter how well it matched. In production that meant roughly 12%
// of temporal video samples and half the photo embeddings could never be
// returned.
//
// The candidate scopes are now walked in KEYSET batches (ordered by id, which
// the projections already guaranteed) and each batch is ranked and offered to a
// fixed-capacity BoundedTopResults accumulator. Coverage is complete; memory is
// a function of the result policy's safety limit, not of library size.
//
// Because complete coverage makes the first page genuinely expensive, the
// finished ranking is cached per (owner, fingerprint) for a short TTL and every
// later page is a keyset slice of that SAME immutable list — never an offset
// over a freshly recomputed ranking.
public sealed class MediaSemanticSearchService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxQueryLength = 256;

    // Retained for diagnostics: this is now the DEFAULT SOFT result limit of
    // SemanticResultPolicy, not a hard per-modality cut. The policy is the
    // authority; this constant exists so reported figures keep a stable
    // meaning and stay in step with the policy default.
    public const int PerModalityTopK = 300;

    // Bounded additional temporal matches per video result, beyond BestMatch.
    public const int MaxAdditionalMatches = 3;

    // Keyset batch sizes. These bound SQL parameter counts and per-batch memory;
    // they do NOT bound coverage — the walk continues until the candidate set is
    // exhausted.
    private const int PhotoCandidateBatchSize = 2_000;
    private const int VideoCandidateBatchSize = 25;

    // Per-batch sample ceiling. Generous relative to 25 videos; if a batch ever
    // reaches it we fall back to per-video ranking rather than truncate — see
    // RankVideoBatchAsync. This is the last place a silent cut could hide.
    private const int VideoSampleBatchCap = 25_000;
    private const int VideoSampleSingleCap = 200_000;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly SemanticMediaCandidateService _candidates;
    private readonly PhotoEmbeddingProfileService _profiles;
    private readonly IAiBackendResolver _backends;
    private readonly PhotoVectorIndexService _photoVectors;
    private readonly VideoSemanticSampleVectorIndexService _videoVectors;
    private readonly IAiVectorSerializer _serializer;
    private readonly IOptions<VideoSemanticSegmentationOptions> _segmentation;
    private readonly SemanticResultPolicy _policy;
    private readonly SemanticRankingCache _rankingCache;
    private readonly ILogger<MediaSemanticSearchService> _logger;

    public MediaSemanticSearchService(
        AppDbContext db,
        IFileItemService files,
        SemanticMediaCandidateService candidates,
        PhotoEmbeddingProfileService profiles,
        IAiBackendResolver backends,
        PhotoVectorIndexService photoVectors,
        VideoSemanticSampleVectorIndexService videoVectors,
        IAiVectorSerializer serializer,
        IOptions<VideoSemanticSegmentationOptions> segmentation,
        SemanticResultPolicy policy,
        SemanticRankingCache rankingCache,
        ILogger<MediaSemanticSearchService> logger)
    {
        _db = db;
        _files = files;
        _candidates = candidates;
        _profiles = profiles;
        _backends = backends;
        _photoVectors = photoVectors;
        _videoVectors = videoVectors;
        _serializer = serializer;
        _segmentation = segmentation;
        _policy = policy;
        _rankingCache = rankingCache;
        _logger = logger;
    }

    // The total order the accumulator, the merge and pagination all share.
    private static int BetterFirst(SemanticRankedHit a, SemanticRankedHit b)
    {
        var byScore = b.Score.CompareTo(a.Score);
        return byScore != 0 ? byScore : a.FileItemId.CompareTo(b.FileItemId);
    }

    public async Task<SemanticMediaPage> SearchAsync(
        Guid ownerUserId,
        string query,
        MediaKindScope kind,
        int limit,
        string? cursor,
        ImageFilters filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0 || normalizedQuery.Length > MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var segmentationVersion = _segmentation.Value.SegmentationVersion;
        var started = Stopwatch.GetTimestamp();

        // ---- profile + text tower (the SAME paired-profile contract as the
        // photo semantic search — one profile, image tower + text tower) ------
        var profileResolution = await _profiles.ResolveActiveProfileAsync(null, cancellationToken);
        if (!profileResolution.Usable || profileResolution.Profile is null
            || profileResolution.Profile.Dimension is not > 0)
        {
            return SemanticMediaPage.Unavailable(
                profileResolution.UnavailableReason ?? AiUnavailableReasons.ProfileDimensionInvalid);
        }
        var profile = profileResolution.Profile;

        var backendResolution = await _backends.ResolveForProfileKeyAsync<ITextEmbedder>(
            profile.Key, cancellationToken);
        if (!backendResolution.Resolution.IsAvailable || backendResolution.Backend is null)
        {
            return SemanticMediaPage.Unavailable(backendResolution.Resolution.UnavailableReason);
        }

        // ---- cursor binding BEFORE the expensive work ----------------------
        var fingerprint = SemanticMediaCursor.Fingerprint(
            normalizedQuery, profile.Key, kind, filters, segmentationVersion);
        double? cursorScore = null;
        Guid cursorId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!SemanticMediaCursor.TryDecode(cursor, fingerprint, out var score, out var id))
            {
                throw new SemanticSearchCursorException();
            }
            cursorScore = score;
            cursorId = id;
        }

        // ---- the complete ranking, built once per (owner, query identity) ---
        var embedTime = 0L;
        var rankTime = 0L;
        SemanticRankingSnapshot snapshot;
        try
        {
            snapshot = await _rankingCache.GetOrBuildAsync(
                ownerUserId,
                fingerprint,
                async ct =>
                {
                var embedStarted = Stopwatch.GetTimestamp();
                var embedding = await backendResolution.Backend.EmbedTextAsync(
                    normalizedQuery, profile, ct);
                embedTime = ElapsedMs(embedStarted);
                if (embedding.Dimension != profile.Dimension
                    || embedding.Vector.Length != profile.Dimension)
                {
                    throw new SemanticProfileDimensionException();
                }

                var rankStarted = Stopwatch.GetTimestamp();
                var built = await BuildRankingAsync(
                    ownerUserId, profile, embedding.Vector, kind, filters,
                    segmentationVersion, ct);
                rankTime = ElapsedMs(rankStarted);
                return built;
                },
                cancellationToken);
        }
        catch (SemanticProfileDimensionException)
        {
            return SemanticMediaPage.Unavailable(AiUnavailableReasons.ProfileDimensionInvalid);
        }

        var cacheHit = _rankingCache.LastLookupWasHit;
        var ranked = snapshot.Hits;
        var total = ranked.Count;

        // ---- keyset slice of the IMMUTABLE ranked list ----------------------
        var start = 0;
        if (cursorScore is double cs)
        {
            start = ranked.Count;
            for (var i = 0; i < ranked.Count; i++)
            {
                var h = ranked[i];
                if (h.Score < cs || (h.Score == cs && h.FileItemId.CompareTo(cursorId) > 0))
                {
                    start = i;
                    break;
                }
            }
        }

        var pageHits = ranked.Skip(start).Take(pageSize).ToList();
        var pageIds = pageHits.Select(h => h.FileItemId).ToList();

        // Owner-visible DTO projection preserving rank. Hydration re-applies
        // the gallery membership gate, so anything deleted/excluded between
        // ranking and projection silently drops out — which also means a cached
        // ranking can never resurrect media the owner has since removed.
        var hydrated = await _files.ListGalleryMediaByRankAsync(ownerUserId, pageIds, cancellationToken);
        var mediaById = hydrated.ToDictionary(m => m.Id);
        var items = pageHits
            .Where(h => mediaById.ContainsKey(h.FileItemId))
            .Select(h => new SemanticMediaResultItem(
                mediaById[h.FileItemId], h.BestMatch, h.AdditionalMatches))
            .ToList();

        var hasMore = start + pageHits.Count < total;
        string? nextCursor = null;
        if (hasMore && pageHits.Count > 0)
        {
            var last = pageHits[^1];
            nextCursor = SemanticMediaCursor.Encode(last.Score, last.FileItemId, fingerprint);
        }

        // Aggregate diagnostics only. Never the query text, a filename, a score
        // or a vector.
        _logger.LogInformation(
            "media-semantic: operation={Operation} profile={ProfileKey} dim={Dimension} "
            + "calibrated={Calibrated} kind={Kind} cache={Cache} "
            + "photo-candidates={PhotoCandidates} video-samples={VideoSamples} "
            + "videos-covered={VideosCovered} total={Total} still-indexing={StillIndexing} "
            + "embed-ms={EmbedMs} rank-ms={RankMs} elapsed-ms={ElapsedMs}",
            "media.semantic.search",
            profile.Key,
            profile.Dimension,
            _policy.IsCalibrated,
            kind.ToWire(),
            cacheHit ? "hit" : "miss",
            snapshot.PhotoCandidatesExamined,
            snapshot.VideoSamplesExamined,
            snapshot.DistinctVideosCovered,
            total,
            snapshot.StillIndexingManyItems,
            embedTime,
            rankTime,
            ElapsedMs(started));

        return new SemanticMediaPage(
            true, null, items, nextCursor, hasMore, total, snapshot.StillIndexingManyItems);
    }

    // ---- the complete ranking ----------------------------------------------

    private async Task<SemanticRankingSnapshot> BuildRankingAsync(
        Guid ownerUserId,
        Domain.Ai.AiProfile profile,
        float[] queryVector,
        MediaKindScope kind,
        ImageFilters filters,
        int segmentationVersion,
        CancellationToken cancellationToken)
    {
        var accumulator = new BoundedTopResults<SemanticRankedHit>(
            _policy.AccumulatorCapacity, BetterFirst);

        var photoCandidatesExamined = 0;
        var videoSamplesExamined = 0;
        var videosCovered = 0;
        var photoCandidateTotal = 0;
        var videoBlobTotal = 0;

        // ---- photos: keyset walk over the eligible candidate set ------------
        if (kind != MediaKindScope.Video)
        {
            Guid? after = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await _files.ListPhysicalGalleryCandidatesAsync(
                    ownerUserId, filters, PhotoCandidateBatchSize, cancellationToken, after);
                if (batch.Count == 0)
                {
                    break;
                }
                photoCandidateTotal += batch.Count;
                photoCandidatesExamined += await RankPhotoBatchAsync(
                    ownerUserId, profile, queryVector, batch, accumulator, cancellationToken);
                after = batch[^1].Id;
                if (batch.Count < PhotoCandidateBatchSize)
                {
                    break;
                }
            }
        }

        // ---- videos: keyset walk over the eligible video candidates ---------
        if (kind != MediaKindScope.Image)
        {
            Guid? after = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await _files.ListPhysicalVideoCandidatesAsync(
                    ownerUserId, filters, VideoCandidateBatchSize, cancellationToken, after);
                if (batch.Count == 0)
                {
                    break;
                }
                videoBlobTotal += batch.Select(c => c.BlobObjectId).Distinct().Count();
                var (samples, covered) = await RankVideoBatchAsync(
                    profile, queryVector, batch, segmentationVersion, accumulator, cancellationToken);
                videoSamplesExamined += samples;
                videosCovered += covered;
                after = batch[^1].Id;
                if (batch.Count < VideoCandidateBatchSize)
                {
                    break;
                }
            }
        }

        // The accumulator is already in (score desc, id asc) order.
        var policed = _policy.Apply(accumulator.ToOrderedList(), h => h.Score);

        var stillIndexing = await ComputeStillIndexingAsync(
            ownerUserId, profile.Id, kind, filters, photoCandidateTotal, videoBlobTotal,
            segmentationVersion, cancellationToken);

        return new SemanticRankingSnapshot(
            policed, stillIndexing, photoCandidatesExamined, videoSamplesExamined, videosCovered);
    }

    // ---- photos ------------------------------------------------------------

    // Same ranking contract as the photo semantic gallery: pgvector exact scan
    // restricted to the candidate ids when available, in-process exact cosine
    // over canonical embeddings otherwise. Returns how many candidates were
    // actually scored.
    private async Task<int> RankPhotoBatchAsync(
        Guid ownerUserId,
        Domain.Ai.AiProfile profile,
        float[] queryVector,
        IReadOnlyList<GalleryCandidateRef> candidates,
        BoundedTopResults<SemanticRankedHit> accumulator,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var candidateIds = candidates.Select(c => c.Id).ToArray();
        var vectorHits = queryVector.Length == PhotoVectorIndexService.SupportedDimension
            ? await _photoVectors.SearchWithinCandidatesAsync(
                profile.Id, queryVector, ownerUserId, candidateIds,
                Math.Min(candidateIds.Length, _policy.AccumulatorCapacity), cancellationToken)
            : null;

        if (vectorHits is not null)
        {
            foreach (var h in vectorHits)
            {
                if (!_policy.Admits(SemanticModality.Photo, h.Score))
                {
                    continue;
                }
                accumulator.Offer(new SemanticRankedHit(
                    h.FileItemId, h.Score, SemanticBestMatch.Photo,
                    Array.Empty<SemanticBestMatch>()));
            }
            return vectorHits.Count;
        }

        // Exact in-process fallback over the candidate blobs' canonical rows.
        var blobIds = candidates.Select(c => c.BlobObjectId).Distinct().ToList();
        var vectors = await _db.BlobEmbeddings.AsNoTracking()
            .Where(e => e.ProfileId == profile.Id && blobIds.Contains(e.BlobObjectId))
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

        var scored = 0;
        foreach (var c in candidates)
        {
            if (!scoreByBlob.TryGetValue(c.BlobObjectId, out var score))
            {
                continue;
            }
            scored++;
            if (!_policy.Admits(SemanticModality.Photo, score))
            {
                continue;
            }
            accumulator.Offer(new SemanticRankedHit(
                c.Id, score, SemanticBestMatch.Photo, Array.Empty<SemanticBestMatch>()));
        }
        return scored;
    }

    // ---- videos ------------------------------------------------------------

    // sample score → segment score (max eligible sample) → video score
    // (max segment). VSEM-01 segments are contiguous and non-overlapping by
    // construction, so grouping samples into their parent segment IS the
    // interval deduplication; additional matches are further DISTINCT segments,
    // best-first, capped at MaxAdditionalMatches. This aggregation is unchanged
    // by SEARCH-SEM-01 — only the SET of videos reaching it is.
    private async Task<(int Samples, int VideosCovered)> RankVideoBatchAsync(
        Domain.Ai.AiProfile profile,
        float[] queryVector,
        IReadOnlyList<GalleryCandidateRef> candidates,
        int segmentationVersion,
        BoundedTopResults<SemanticRankedHit> accumulator,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (0, 0);
        }

        var fileIdsByBlob = candidates
            .GroupBy(c => c.BlobObjectId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
        var blobIds = fileIdsByBlob.Keys.ToList();

        var scope = await _candidates.GetVideoSampleScopeAsync(
            blobIds, segmentationVersion, VideoSampleBatchCap, cancellationToken);

        // If the batch filled the per-batch ceiling we cannot tell whether it
        // truncated, so re-fetch per video with a far larger ceiling. Coverage
        // must never depend on a cap that a long video could silently hit.
        if (scope.Count >= VideoSampleBatchCap)
        {
            var perVideo = new List<VideoSampleScopeRef>();
            foreach (var blobId in blobIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                perVideo.AddRange(await _candidates.GetVideoSampleScopeAsync(
                    new[] { blobId }, segmentationVersion, VideoSampleSingleCap, cancellationToken));
            }
            scope = perVideo;
        }

        if (scope.Count == 0)
        {
            return (0, 0);
        }

        var sampleIds = scope.Select(s => s.SampleId).ToList();
        var neighbours = await _videoVectors.SearchWithinCandidatesAsync(
            profile.Id, queryVector, sampleIds, sampleIds.Count, cancellationToken);
        if (neighbours.Count == 0)
        {
            return (scope.Count, 0);
        }

        var scoreBySample = neighbours.ToDictionary(n => n.SampleId, n => n.Score);

        // Segment score = max eligible sample; the representative timestamp is
        // that best sample's manifest timestamp.
        var segments = scope
            .Where(s => scoreBySample.ContainsKey(s.SampleId))
            .GroupBy(s => s.SegmentId)
            .Select(g =>
            {
                var best = g
                    .OrderByDescending(s => scoreBySample[s.SampleId])
                    .ThenBy(s => s.SampleTimestampMilliseconds)
                    .First();
                return new
                {
                    best.BlobObjectId,
                    SegmentId = g.Key,
                    Score = scoreBySample[best.SampleId],
                    best.SegmentStartMilliseconds,
                    best.SegmentEndMilliseconds,
                    Representative = best.SampleTimestampMilliseconds,
                };
            })
            .ToList();

        var covered = 0;
        foreach (var blobGroup in segments.GroupBy(s => s.BlobObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = blobGroup
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.SegmentStartMilliseconds)
                .ToList();
            var best = ordered[0];

            // The video enters the results only if its BEST position qualifies.
            if (!_policy.Admits(SemanticModality.Video, best.Score))
            {
                continue;
            }

            var bestMatch = SemanticBestMatch.ForSegment(
                best.SegmentStartMilliseconds, best.SegmentEndMilliseconds, best.Representative);
            var additional = ordered
                .Skip(1)
                .Take(MaxAdditionalMatches)
                .Select(s => SemanticBestMatch.ForSegment(
                    s.SegmentStartMilliseconds, s.SegmentEndMilliseconds, s.Representative))
                .ToList();

            if (!fileIdsByBlob.TryGetValue(blobGroup.Key, out var fileIds))
            {
                continue;
            }

            covered++;
            foreach (var fileId in fileIds)
            {
                accumulator.Offer(new SemanticRankedHit(fileId, best.Score, bestMatch, additional));
            }
        }

        return (scope.Count, covered);
    }

    // ---- status ------------------------------------------------------------

    // Generic "still indexing" disclosure (same heuristic shape as the photo
    // semantic gallery): many eligible candidates without embeddings for the
    // ACTIVE profile. Never exposes model/index internals.
    private async Task<bool> ComputeStillIndexingAsync(
        Guid ownerUserId,
        Guid profileId,
        MediaKindScope kind,
        ImageFilters filters,
        int photoCandidateTotal,
        int videoBlobTotal,
        int segmentationVersion,
        CancellationToken cancellationToken)
    {
        var photosIndexing = false;
        if (kind != MediaKindScope.Video && photoCandidateTotal > 0)
        {
            var embedded = await _files.CountEmbeddedGalleryCandidatesAsync(
                ownerUserId, filters, profileId, cancellationToken);
            photosIndexing = photoCandidateTotal - embedded
                > Math.Max(10, photoCandidateTotal / 5);
        }

        var videosIndexing = false;
        if (kind != MediaKindScope.Image && videoBlobTotal > 0)
        {
            // Counted over the same keyset walk the ranking used, in bounded
            // batches, so this disclosure cannot reintroduce a global cap.
            var covered = 0;
            Guid? after = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await _files.ListPhysicalVideoCandidatesAsync(
                    ownerUserId, filters, PhotoCandidateBatchSize, cancellationToken, after);
                if (batch.Count == 0)
                {
                    break;
                }
                covered += await _candidates.CountVideoBlobsWithEmbeddingsAsync(
                    batch.Select(c => c.BlobObjectId).Distinct().ToList(),
                    profileId, segmentationVersion, cancellationToken);
                after = batch[^1].Id;
                if (batch.Count < PhotoCandidateBatchSize)
                {
                    break;
                }
            }
            videosIndexing = videoBlobTotal - covered > Math.Max(10, videoBlobTotal / 5);
        }

        return photosIndexing || videosIndexing;
    }

    private static long ElapsedMs(long startedTimestamp)
        => (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na <= double.Epsilon || nb <= double.Epsilon
            ? 0
            : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

// Raised when the resolved profile's text tower returns a vector of the wrong
// dimension. Surfaced as the same sanitized "unavailable" reason as before.
public sealed class SemanticProfileDimensionException : Exception
{
}

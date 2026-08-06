using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Owner-private, profile-scoped face clustering (People v0). Produces SUGGESTED
// FaceCluster groups from the owner's persisted face embeddings using a
// conservative, bounded, exact algorithm (union-find over pairwise cosine at the
// configured cluster threshold). NOT global, NOT cross-owner.
//
// Invariants:
//   * Owner + profile scoped everywhere; a blob-level face may land in different
//     owners' clusters independently, never a shared identity.
//   * Private Vault + media-library exclusion: candidate faces must be
//     referenced by an active, non-vault, non-Excluded FileItem OWNED by this
//     owner. The FileItems query carries the global vault filter + an owner
//     predicate + a MediaLibraryState==Active predicate. A face whose only
//     reference is Excluded (or vaulted) never enters the candidate set; a blob
//     referenced by BOTH an Active and an Excluded file stays eligible via the
//     Active one. The filter runs BEFORE clustering (candidate selection) and is
//     re-evaluated on every run, so a file excluded after a candidate was found
//     is simply skipped on the next execution — a controlled skip, no error, no
//     deletion of existing detections/embeddings/clusters.
//   * User overrides win: faces already assigned to a Person, or members of a
//     Confirmed/Ignored cluster, are excluded from (re)clustering; Confirmed and
//     Ignored clusters are never touched. Only Suggested/NeedsReview clusters are
//     rebuilt, so re-runs are idempotent and pick up new faces.
//   * Bounded/CPU-safe: at most MaxFacesToCluster faces per owner are considered
//     (excess is logged, never silently dropped); no raw vectors leave the service.
public sealed class FaceClusteringService
{
    // Hard per-owner bound so one huge library cannot blow up the O(n^2) pass.
    public const int MaxFacesToCluster = 4000;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IAiVectorSerializer _serializer;
    private readonly FaceVectorIndexService _vectors;
    private readonly IOptions<AiOptions> _options;

    public FaceClusteringService(
        AppDbContext db, TimeProvider clock, IAiVectorSerializer serializer,
        FaceVectorIndexService vectors, IOptions<AiOptions> options)
    {
        _db = db;
        _clock = clock;
        _serializer = serializer;
        _vectors = vectors;
        _options = options;
    }

    // Dispatch on the configured clustering mode. pgvector_knn is the scalable
    // default; when the pgvector backend is unavailable (SQLite / no pgvector) the
    // kNN path returns null and we fall back to the bounded exact algorithm — so
    // this is always safe. Both paths honour the same eligibility + privacy rules.
    public async Task<FaceClusterOutcome> ClusterOwnerAsync(
        Guid ownerUserId,
        AiProfile profile,
        FaceSettings settings,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var mode = _options.Value.Face.ClusteringMode;
        if (string.Equals(mode, FaceClusteringModes.PgvectorKnn, StringComparison.OrdinalIgnoreCase))
        {
            var knn = await RunPgvectorKnnClusteringAsync(ownerUserId, profile, settings, log, cancellationToken);
            if (knn is not null)
            {
                return knn;
            }
            log?.Invoke("ai faces cluster: pgvector kNN backend unavailable → falling back to exact.");
        }

        return await ClusterOwnerExactAsync(ownerUserId, profile, settings, log, cancellationToken);
    }

    // Bounded exact O(n^2) clustering — historical path, kept as fallback + test
    // oracle for small datasets.
    public async Task<FaceClusterOutcome> ClusterOwnerExactAsync(
        Guid ownerUserId,
        AiProfile profile,
        FaceSettings settings,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var threshold = settings.ClusterSimilarityThreshold;

        // Faces excluded from (re)clustering (user overrides win):
        //   * assigned to a Person (the authoritative record);
        //   * individually ignored (per-face dismiss — this is the ONLY ignore
        //     mechanism; "Ignora gruppo" writes per-face IgnoredFace rows too).
        // NOTE: cluster Status is NOT used to pin faces. Confirmed-cluster faces are
        // excluded via their Person assignment, and Ignored-cluster faces via their
        // per-face IgnoredFace rows. This keeps exclusion purely per-face, so
        // removing an assignment or restoring an ignored face frees it immediately.
        var assignedFaceIds = await _db.PersonFaceAssignments.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId)
            .Select(a => a.FaceDetectionId)
            .ToListAsync(cancellationToken);
        var assigned = assignedFaceIds.ToHashSet();

        var ignoredFaceIds = await _db.IgnoredFaces.AsNoTracking()
            .Where(i => i.OwnerUserId == ownerUserId)
            .Select(i => i.FaceDetectionId)
            .ToListAsync(cancellationToken);
        foreach (var id in ignoredFaceIds)
        {
            assigned.Add(id);
        }

        // Candidate faces: this owner's visible, non-vault, non-Excluded faces
        // with an embedding for the profile. The FileItems predicate carries the
        // global vault filter (PrivateVaultId == null); the explicit
        // MediaLibraryState == Active term keeps out faces whose only reference
        // was moved to the "Esclusi" media-library state. A blob referenced by
        // both an Active and an Excluded file stays eligible via the Active one.
        var candidates = await (
            from d in _db.FaceDetections.AsNoTracking()
            join e in _db.FaceEmbeddings.AsNoTracking() on d.Id equals e.FaceDetectionId
            where d.ProfileId == profile.Id
                && e.ProfileId == profile.Id
                && e.EmbeddingStatus == AiArtifactStatuses.Completed
                && _db.FileItems.Any(f =>
                    f.BlobObjectId == d.BlobObjectId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
            orderby d.Id
            select new { d.Id, d.FaceQualityScore, d.DetectionScore, e.EmbeddingBytes })
            .Take(MaxFacesToCluster + 1)
            .ToListAsync(cancellationToken);

        var truncated = candidates.Count > MaxFacesToCluster;
        if (truncated)
        {
            candidates = candidates.Take(MaxFacesToCluster).ToList();
            log?.Invoke(
                $"ai faces cluster: owner face set exceeds {MaxFacesToCluster}; clustering the first {MaxFacesToCluster} (bounded).");
        }

        var faces = candidates
            .Where(c => !assigned.Contains(c.Id))
            .Select(c =>
            {
                float[] v;
                try { v = _serializer.Deserialize(c.EmbeddingBytes); }
                catch { v = Array.Empty<float>(); }
                return new FaceRow(c.Id, Normalize(v), c.FaceQualityScore ?? c.DetectionScore ?? 0);
            })
            .Where(f => f.Vector.Length > 0)
            .ToList();

        // Rebuild only the auto layers (Suggested/NeedsReview); Confirmed/Ignored
        // are preserved. Delete members first (cascade also covers it, but explicit
        // keeps SQLite happy and the intent clear).
        await ClearAutoClustersAsync(ownerUserId, profile.Id, cancellationToken);

        if (faces.Count < 2)
        {
            await _db.SaveChangesAsync(cancellationToken);
            log?.Invoke($"ai faces cluster: owner has {faces.Count} clusterable face(s); 0 groups.");
            return new FaceClusterOutcome(faces.Count, 0, 0);
        }

        var components = BuildComponents(faces, threshold);

        var now = _clock.GetUtcNow().UtcDateTime;
        var groupsCreated = 0;
        var facesGrouped = 0;
        foreach (var component in components)
        {
            if (component.Count < 2)
            {
                continue; // singletons are not a group
            }

            // Representative = highest-quality face (centrality proxy).
            var repIndex = component.OrderByDescending(i => faces[i].Quality).First();
            var rep = faces[repIndex];

            // Similarity of each member to the representative → cohesion.
            var sims = component.ToDictionary(i => i, i => i == repIndex ? 1.0 : Cosine(faces[i].Vector, rep.Vector));
            var confidence = Math.Round(sims.Values.Average(), 6);

            // A tight group is a confident Suggestion; a loose transitive group
            // (avg cohesion below the cluster threshold) is surfaced under Review.
            var status = confidence >= threshold
                ? FaceClusterStatuses.Suggested
                : FaceClusterStatuses.NeedsReview;

            var cluster = new FaceCluster
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ProfileId = profile.Id,
                RepresentativeFaceDetectionId = rep.Id,
                Status = status,
                ConfidenceAggregate = confidence,
                MemberCount = component.Count,
                ClusterKey = $"auto:{now:yyyyMMddHHmmss}",
                CreatedAt = now,
            };
            _db.FaceClusters.Add(cluster);

            foreach (var i in component)
            {
                _db.FaceClusterMembers.Add(new FaceClusterMember
                {
                    Id = Guid.NewGuid(),
                    FaceClusterId = cluster.Id,
                    FaceDetectionId = faces[i].Id,
                    SimilarityScore = Math.Round(sims[i], 6),
                    MembershipSource = FaceClusterMemberSources.AutoCluster,
                    CreatedAt = now,
                });
            }

            groupsCreated++;
            facesGrouped += component.Count;
        }

        await _db.SaveChangesAsync(cancellationToken);
        log?.Invoke(
            $"ai faces cluster: owner clustered {faces.Count} face(s) → {groupsCreated} group(s) "
            + $"({facesGrouped} grouped) at threshold {threshold:0.###}.");
        return new FaceClusterOutcome(faces.Count, groupsCreated, facesGrouped);
    }

    // Scalable pgvector kNN clustering. Returns null when the pgvector backend is
    // unavailable (caller falls back to exact). Owner + profile scoped; honours the
    // same eligibility/privacy rules as exact (assigned/ignored/vault/deleted
    // excluded — enforced in SQL). Builds a sparse kNN similarity graph via the HNSW
    // cosine index, extracts connected components, and rebuilds only the auto
    // (Suggested/NeedsReview) layers; Confirmed/Ignored clusters + manual
    // assignments are never touched.
    private async Task<FaceClusterOutcome?> RunPgvectorKnnClusteringAsync(
        Guid ownerUserId, AiProfile profile, FaceSettings settings, Action<string>? log, CancellationToken cancellationToken)
    {
        var o = _options.Value.Face;
        var maxEligible = o.KnnMaxEligibleFacesPerRun > 0 ? o.KnnMaxEligibleFacesPerRun : 100_000;
        var k = Math.Clamp(o.KnnNeighbors, 1, 500);
        var ef = Math.Clamp(o.KnnEfSearch, 1, FaceVectorIndexService.MaxEfSearch);
        // The edge/cohesion threshold and the Louvain resolution are the ADMIN-EDITABLE
        // knobs (config default overlaid by the ai_settings override) — so the admin UI
        // sliders actually retune the active pgvector_knn+Louvain path. 0.40 default:
        // conservative — Suggested cohesion.
        var clusterThreshold = settings.ClusterSimilarityThreshold;
        var resolution = settings.KnnLouvainResolution;
        // Graph EDGES use the cluster threshold (0.40), NOT the looser candidate
        // (0.30). At 0.30 different people (relatives, hard same-person) connect and
        // transitive chaining collapses the whole library into one mega-cluster
        // (observed on 180: a single 91k-face "cluster"). At 0.40 cross-person edges
        // are rare, so components stay person-sized (matches the exact algorithm).
        var edgeThreshold = clusterThreshold;
        var minSize = Math.Max(2, o.KnnMinClusterSize);
        // Safety net against any residual over-merge: components larger than this are
        // surfaced as NeedsReview instead of a confident Suggested group.
        var maxSize = o.KnnMaxClusterSize > 0 ? o.KnnMaxClusterSize : 300;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var eligible = await _vectors.GetEligibleClusterFaceIdsAsync(ownerUserId, profile.Id, maxEligible, cancellationToken);
        if (eligible is null)
        {
            return null; // pgvector unavailable → fall back to exact
        }

        if (eligible.Count < minSize)
        {
            await ClearAutoClustersAsync(ownerUserId, profile.Id, cancellationToken);
            log?.Invoke($"ai faces cluster (pgvector_knn): owner has {eligible.Count} eligible face(s); 0 groups.");
            return new FaceClusterOutcome(eligible.Count, 0, 0);
        }

        var edges = await _vectors.CollectKnnEdgesAsync(
            ownerUserId, profile.Id, eligible, edgeThreshold, k, ef,
            processed => log?.Invoke($"ai faces cluster (pgvector_knn): {processed}/{eligible.Count} anchors processed."),
            cancellationToken);
        if (edges is null)
        {
            return null; // backend went away mid-run → fall back
        }

        // MUTUAL-kNN: keep an undirected edge A–B only when it is RECIPROCAL — A is in
        // B's top-k AND B is in A's top-k. This removes the weak, asymmetric "bridge"
        // edges that make single-linkage union-find percolate a whole library into one
        // giant component (observed: a 75k-face blob even at 0.40). True same-person
        // pairs are mutually nearest, so real clusters survive; spurious bridges (one
        // outlier listing another) are asymmetric and dropped. Cosine is symmetric, so
        // the two directions carry the same similarity. Deduped to one edge per pair.
        var directed = new HashSet<(Guid, Guid)>(edges.Select(e => (e.AnchorFaceId, e.NeighborFaceId)));
        var seenPair = new HashSet<(Guid, Guid)>();
        var graphEdges = new List<FaceEdge>();
        foreach (var e in edges)
        {
            if (!directed.Contains((e.NeighborFaceId, e.AnchorFaceId)))
            {
                continue; // not reciprocal → drop the bridge
            }
            var key = e.AnchorFaceId.CompareTo(e.NeighborFaceId) < 0
                ? (e.AnchorFaceId, e.NeighborFaceId)
                : (e.NeighborFaceId, e.AnchorFaceId);
            if (seenPair.Add(key))
            {
                graphEdges.Add(e);
            }
        }

        // Node set = eligible faces ∪ any edge endpoints (neighbour beyond the cap).
        var index = new Dictionary<Guid, int>();
        var nodeIds = new List<Guid>();
        int NodeOf(Guid id)
        {
            if (!index.TryGetValue(id, out var ix)) { ix = nodeIds.Count; index[id] = ix; nodeIds.Add(id); }
            return ix;
        }
        foreach (var f in eligible) NodeOf(f);
        foreach (var e in graphEdges) { NodeOf(e.AnchorFaceId); NodeOf(e.NeighborFaceId); }

        var n = nodeIds.Count;
        var bestIncident = new double[n]; // centrality proxy: best edge similarity at a node
        var louvainEdges = new List<(int, int, double)>(graphEdges.Count);
        foreach (var e in graphEdges)
        {
            var u = index[e.AnchorFaceId];
            var v = index[e.NeighborFaceId];
            louvainEdges.Add((u, v, e.Similarity));
            if (e.Similarity > bestIncident[u]) bestIncident[u] = e.Similarity;
            if (e.Similarity > bestIncident[v]) bestIncident[v] = e.Similarity;
        }

        // Community detection (weighted Louvain) rather than raw connected components:
        // splits a large single-linkage-percolated component into dense person
        // communities, cutting the sparse bridges that would otherwise chain the whole
        // library into one blob. Disconnected parts naturally land in different
        // communities, so this also subsumes plain component extraction.
        var community = LouvainCommunityDetection.Detect(n, louvainEdges, resolution);

        var comps = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var c = community[i];
            if (!comps.TryGetValue(c, out var list)) { list = new List<int>(); comps[c] = list; }
            list.Add(i);
        }
        var compEdgeSims = new Dictionary<int, List<double>>();
        foreach (var e in graphEdges)
        {
            var cu = community[index[e.AnchorFaceId]];
            var cv = community[index[e.NeighborFaceId]];
            if (cu != cv) continue; // only intra-community edges contribute to cohesion
            if (!compEdgeSims.TryGetValue(cu, out var list)) { list = new List<double>(); compEdgeSims[cu] = list; }
            list.Add(e.Similarity);
        }

        await ClearAutoClustersAsync(ownerUserId, profile.Id, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        int groupsCreated = 0, facesGrouped = 0, suggested = 0, review = 0, communitiesFound = 0;
        foreach (var (root, members) in comps)
        {
            if (members.Count < minSize) continue;
            communitiesFound++;

            var repIdx = members.OrderByDescending(i => bestIncident[i]).ThenBy(i => nodeIds[i]).First();
            var sims = compEdgeSims.GetValueOrDefault(root) ?? new List<double>();
            var confidence = sims.Count > 0 ? Math.Round(sims.Average(), 6) : clusterThreshold;
            var status = confidence >= clusterThreshold ? FaceClusterStatuses.Suggested : FaceClusterStatuses.NeedsReview;
            if (maxSize > 0 && members.Count > maxSize) status = FaceClusterStatuses.NeedsReview;
            if (status == FaceClusterStatuses.Suggested) suggested++; else review++;

            var cluster = new FaceCluster
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ProfileId = profile.Id,
                RepresentativeFaceDetectionId = nodeIds[repIdx],
                Status = status,
                ConfidenceAggregate = confidence,
                MemberCount = members.Count,
                ClusterKey = $"knn:{now:yyyyMMddHHmmss}",
                CreatedAt = now,
            };
            _db.FaceClusters.Add(cluster);
            foreach (var i in members)
            {
                var score = i == repIdx ? 1.0 : (bestIncident[i] > 0 ? bestIncident[i] : confidence);
                _db.FaceClusterMembers.Add(new FaceClusterMember
                {
                    Id = Guid.NewGuid(),
                    FaceClusterId = cluster.Id,
                    FaceDetectionId = nodeIds[i],
                    SimilarityScore = Math.Round(score, 6),
                    MembershipSource = FaceClusterMemberSources.AutoCluster,
                    CreatedAt = now,
                });
            }
            groupsCreated++;
            facesGrouped += members.Count;
        }

        await _db.SaveChangesAsync(cancellationToken);
        sw.Stop();

        var skippedAssigned = await _db.PersonFaceAssignments.CountAsync(a => a.OwnerUserId == ownerUserId, cancellationToken);
        var skippedIgnored = await _db.IgnoredFaces.CountAsync(g => g.OwnerUserId == ownerUserId, cancellationToken);
        log?.Invoke(
            $"ai faces cluster (pgvector_knn+louvain): eligible {eligible.Count}, edges {edges.Count}, mutual {graphEdges.Count}, "
            + $"communities {communitiesFound}, clusters {groupsCreated} (suggested {suggested}, review {review}), "
            + $"facesGrouped {facesGrouped}, skippedAssigned {skippedAssigned}, skippedIgnored {skippedIgnored}, "
            + $"k={k}, efSearch={ef}, edgeSim={edgeThreshold:0.###}, louvainRes={resolution:0.##}, maxClusterSize={maxSize}, elapsedMs {sw.ElapsedMilliseconds}.");

        return new FaceClusterOutcome(eligible.Count, groupsCreated, facesGrouped);
    }

    private async Task ClearAutoClustersAsync(Guid ownerUserId, Guid profileId, CancellationToken cancellationToken)
    {
        var autoClusterIds = await _db.FaceClusters
            .Where(c => c.OwnerUserId == ownerUserId
                && c.ProfileId == profileId
                && (c.Status == FaceClusterStatuses.Suggested || c.Status == FaceClusterStatuses.NeedsReview))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (autoClusterIds.Count == 0)
        {
            return;
        }

        var members = await _db.FaceClusterMembers
            .Where(m => autoClusterIds.Contains(m.FaceClusterId))
            .ToListAsync(cancellationToken);
        _db.FaceClusterMembers.RemoveRange(members);
        var clusters = await _db.FaceClusters
            .Where(c => autoClusterIds.Contains(c.Id))
            .ToListAsync(cancellationToken);
        _db.FaceClusters.RemoveRange(clusters);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Union-find over pairwise cosine >= threshold → connected components.
    private static List<List<int>> BuildComponents(List<FaceRow> faces, double threshold)
    {
        var n = faces.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (Cosine(faces[i].Vector, faces[j].Vector) >= threshold)
                {
                    Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        return groups.Values.ToList();
    }

    private static float[] Normalize(float[] v)
    {
        if (v.Length == 0) return v;
        double sumSq = 0;
        foreach (var x in v) sumSq += (double)x * x;
        var norm = Math.Sqrt(sumSq);
        if (norm <= double.Epsilon) return Array.Empty<float>(); // zero vector → drop
        var outv = new float[v.Length];
        for (var i = 0; i < v.Length; i++) outv[i] = (float)(v[i] / norm);
        return outv;
    }

    // Both inputs are pre-normalized → cosine == dot product.
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    private readonly record struct FaceRow(Guid Id, float[] Vector, double Quality);
}

public sealed record FaceClusterOutcome(int FacesConsidered, int GroupsCreated, int FacesGrouped);

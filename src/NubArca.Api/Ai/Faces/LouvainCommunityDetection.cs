namespace NubArca.Api.Ai.Faces;

// Pure, deterministic weighted Louvain modularity community detection.
//
// Face clustering builds a mutual-kNN similarity graph; taking raw connected
// components (single linkage) percolates a large library into one giant blob
// because a handful of genuine cross-person edges chain otherwise-separate people.
// Louvain instead maximises modularity: it finds DENSE communities, so the giant
// component is split into person-sized groups while sparse bridges between people
// are cut. Edge weight = cosine similarity (stronger links matter more).
//
// Multi-level: local moving (greedily move each node to the neighbouring community
// with the best modularity gain) → aggregate communities into super-nodes → repeat
// until modularity stops improving. Nodes are visited in index order so the result
// is deterministic for a given edge list.
public static class LouvainCommunityDetection
{
    private const double Epsilon = 1e-9;

    // Returns the community id (0-based, arbitrary but stable) for each node [0, n).
    // A node with no edges is its own singleton community.
    //
    // resolution = the Louvain γ. 1.0 is standard modularity; γ > 1 penalises large
    // communities more, so it produces smaller, tighter communities (useful to split
    // a dense over-merged blob); γ < 1 produces larger communities.
    public static int[] Detect(
        int n, IReadOnlyList<(int U, int V, double W)> edges, double resolution = 1.0, int maxLevels = 20)
    {
        if (double.IsNaN(resolution) || resolution <= 0)
        {
            resolution = 1.0;
        }
        if (n <= 0)
        {
            return Array.Empty<int>();
        }

        // Level 0 graph: undirected adjacency (merge parallel edges) + zero self-loops.
        var adj = new Dictionary<int, double>[n];
        for (var i = 0; i < n; i++) adj[i] = new Dictionary<int, double>();
        var selfLoop = new double[n];
        foreach (var (u, v, w) in edges)
        {
            if (u == v) { selfLoop[u] += w; continue; }
            adj[u][v] = adj[u].GetValueOrDefault(v) + w;
            adj[v][u] = adj[v].GetValueOrDefault(u) + w;
        }

        // node -> final community, composed across levels. Start as identity.
        var mapping = new int[n];
        for (var i = 0; i < n; i++) mapping[i] = i;

        var levelNodes = n;
        for (var level = 0; level < maxLevels; level++)
        {
            var comm = LocalMoving(levelNodes, adj, selfLoop, resolution);
            var (renumbered, communityCount) = Renumber(comm);

            // Compose: every original node currently mapped to a level node now maps
            // to that level node's new community.
            for (var i = 0; i < n; i++) mapping[i] = renumbered[mapping[i]];

            if (communityCount == levelNodes)
            {
                break; // no merging happened → converged
            }

            // Aggregate into the next-level graph (communities become super-nodes).
            var newAdj = new Dictionary<int, double>[communityCount];
            for (var i = 0; i < communityCount; i++) newAdj[i] = new Dictionary<int, double>();
            var newSelf = new double[communityCount];
            for (var u = 0; u < levelNodes; u++)
            {
                var cu = renumbered[u];
                newSelf[cu] += selfLoop[u];
                foreach (var (v, w) in adj[u])
                {
                    var cv = renumbered[v];
                    if (cu == cv)
                    {
                        newSelf[cu] += w / 2.0; // each internal undirected edge seen twice
                    }
                    else
                    {
                        newAdj[cu][cv] = newAdj[cu].GetValueOrDefault(cv) + w;
                    }
                }
            }

            adj = newAdj;
            selfLoop = newSelf;
            levelNodes = communityCount;
        }

        return mapping;
    }

    // One Louvain level: greedy local moving until no node changes community.
    private static int[] LocalMoving(int n, Dictionary<int, double>[] adj, double[] selfLoop, double resolution)
    {
        var deg = new double[n];
        double m2 = 0; // 2m = sum of all degrees
        for (var i = 0; i < n; i++)
        {
            var d = selfLoop[i] * 2.0;
            foreach (var w in adj[i].Values) d += w;
            deg[i] = d;
            m2 += d;
        }

        var comm = new int[n];
        var commTot = new double[n];
        for (var i = 0; i < n; i++) { comm[i] = i; commTot[i] = deg[i]; }

        if (m2 <= Epsilon)
        {
            return comm; // no edges → all singletons
        }

        var neighWeight = new Dictionary<int, double>();
        for (var pass = 0; pass < 100; pass++)
        {
            var improved = false;
            for (var i = 0; i < n; i++)
            {
                var ci = comm[i];
                var ki = deg[i];

                neighWeight.Clear();
                foreach (var (j, w) in adj[i])
                {
                    var cj = comm[j];
                    neighWeight[cj] = neighWeight.GetValueOrDefault(cj) + w;
                }

                // Remove i from its community.
                commTot[ci] -= ki;

                var bestC = ci;
                var bestGain = neighWeight.GetValueOrDefault(ci) - resolution * ki * commTot[ci] / m2;
                foreach (var (c, wic) in neighWeight)
                {
                    var gain = wic - resolution * ki * commTot[c] / m2;
                    if (gain > bestGain + Epsilon || (gain > bestGain - Epsilon && c < bestC))
                    {
                        bestGain = gain;
                        bestC = c;
                    }
                }

                commTot[bestC] += ki;
                comm[i] = bestC;
                if (bestC != ci) improved = true;
            }

            if (!improved) break;
        }

        return comm;
    }

    // Compact community ids to a dense [0, k) range.
    private static (int[] Renumbered, int Count) Renumber(int[] comm)
    {
        var map = new Dictionary<int, int>();
        var outv = new int[comm.Length];
        for (var i = 0; i < comm.Length; i++)
        {
            if (!map.TryGetValue(comm[i], out var id))
            {
                id = map.Count;
                map[comm[i]] = id;
            }
            outv[i] = id;
        }
        return (outv, map.Count);
    }
}

using NubArca.Api.Ai.Faces;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Pure unit tests for the weighted Louvain community detection that powers B2:
// splitting the mutual-kNN graph into dense person communities instead of one
// single-linkage-percolated blob. No pgvector required — runs everywhere.
public class LouvainCommunityDetectionTests
{
    private static int CommunityCount(int[] labels) => labels.Distinct().Count();

    [Fact]
    public void EmptyGraph_YieldsSingletons()
    {
        var labels = LouvainCommunityDetection.Detect(3, Array.Empty<(int, int, double)>());
        Assert.Equal(3, labels.Length);
        Assert.Equal(3, CommunityCount(labels)); // each isolated node is its own community
    }

    [Fact]
    public void TwoCliquesJoinedByOneWeakBridge_SplitIntoTwoCommunities()
    {
        // Clique A = {0,1,2}, clique B = {3,4,5}, all internal edges strong (0.9),
        // a single weak bridge 2-3 (0.41). Single-linkage would chain all six into
        // one component; Louvain must cut the bridge and return two communities.
        var edges = new List<(int, int, double)>
        {
            (0, 1, 0.9), (0, 2, 0.9), (1, 2, 0.9),
            (3, 4, 0.9), (3, 5, 0.9), (4, 5, 0.9),
            (2, 3, 0.41), // the percolating bridge
        };

        var labels = LouvainCommunityDetection.Detect(6, edges);

        Assert.Equal(2, CommunityCount(labels));
        // clique A shares a label...
        Assert.Equal(labels[0], labels[1]);
        Assert.Equal(labels[1], labels[2]);
        // ...clique B shares a different one...
        Assert.Equal(labels[3], labels[4]);
        Assert.Equal(labels[4], labels[5]);
        // ...and the two are distinct.
        Assert.NotEqual(labels[0], labels[3]);
    }

    [Fact]
    public void DisconnectedComponents_NeverMerge()
    {
        // Two separate strong pairs with no edge between them.
        var edges = new List<(int, int, double)>
        {
            (0, 1, 0.8),
            (2, 3, 0.8),
        };

        var labels = LouvainCommunityDetection.Detect(4, edges);

        Assert.Equal(labels[0], labels[1]);
        Assert.Equal(labels[2], labels[3]);
        Assert.NotEqual(labels[0], labels[2]);
    }

    [Fact]
    public void ChainOfCliques_SplitsRatherThanPercolates()
    {
        // Three cliques of 4 nodes chained by single weak bridges:
        // {0..3} - {4..7} - {8..11}. A giant connected component that Louvain
        // should partition into (at least) the three dense groups.
        var edges = new List<(int, int, double)>();
        void Clique(int b)
        {
            for (var i = b; i < b + 4; i++)
                for (var j = i + 1; j < b + 4; j++)
                    edges.Add((i, j, 0.95));
        }
        Clique(0); Clique(4); Clique(8);
        edges.Add((3, 4, 0.41));
        edges.Add((7, 8, 0.41));

        var labels = LouvainCommunityDetection.Detect(12, edges);

        Assert.True(CommunityCount(labels) >= 3);
        // Each clique stays internally coherent.
        Assert.Equal(labels[0], labels[3]);
        Assert.Equal(labels[4], labels[7]);
        Assert.Equal(labels[8], labels[11]);
        // The chain does not collapse into one community.
        Assert.False(labels[0] == labels[4] && labels[4] == labels[8]);
    }

    [Fact]
    public void HigherResolution_SplitsADenseCliqueIntoMoreCommunities()
    {
        // A single 6-clique (every pair linked, weight 1). At the default resolution
        // it is one community; at a high resolution γ the modularity penalty on large
        // communities fragments it — the knob that breaks up an over-merged blob.
        var edges = new List<(int, int, double)>();
        for (var i = 0; i < 6; i++)
            for (var j = i + 1; j < 6; j++)
                edges.Add((i, j, 1.0));

        var atDefault = LouvainCommunityDetection.Detect(6, edges, 1.0);
        var atHigh = LouvainCommunityDetection.Detect(6, edges, 2.0);

        Assert.Equal(1, CommunityCount(atDefault));
        Assert.True(CommunityCount(atHigh) > 1, "high γ should fragment a dense clique");
    }

    [Fact]
    public void InvalidResolution_FallsBackToDefault()
    {
        var edges = new List<(int, int, double)> { (0, 1, 0.9), (0, 2, 0.9), (1, 2, 0.9) };
        var normal = LouvainCommunityDetection.Detect(3, edges, 1.0);
        var zero = LouvainCommunityDetection.Detect(3, edges, 0.0);      // invalid → treated as 1.0
        var nan = LouvainCommunityDetection.Detect(3, edges, double.NaN); // invalid → treated as 1.0
        Assert.Equal(normal, zero);
        Assert.Equal(normal, nan);
    }

    [Fact]
    public void Deterministic_SameEdgesSameResult()
    {
        var edges = new List<(int, int, double)>
        {
            (0, 1, 0.9), (0, 2, 0.9), (1, 2, 0.9),
            (3, 4, 0.9), (3, 5, 0.9), (4, 5, 0.9),
            (2, 3, 0.41),
        };

        var a = LouvainCommunityDetection.Detect(6, edges);
        var b = LouvainCommunityDetection.Detect(6, edges);

        Assert.Equal(a, b);
    }
}

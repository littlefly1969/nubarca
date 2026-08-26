using NubArca.Api.Rag;
using NubArca.Api.Rag.Retrieval;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Reciprocal Rank Fusion, tested as the pure function it is.
//
// Fusion is where a semantic regression would hide: a change that makes vectors
// dominate looks like "semantic search is working" right up to the point where
// an exact identifier stops being findable. So the two directions are asserted
// separately — a paraphrase lexical missed must be promoted, AND an exact hit
// must stay first when the semantic side is merely plausible.
public sealed class RrfFusionTests
{
    [Fact]
    public void Rrf_PromotesSemanticParaphrase()
    {
        // The question is a paraphrase: it shares no vocabulary with the chunk
        // that answers it, so lexical ranks that chunk nowhere. Vector ranks it
        // first. Fusion has to surface it.
        var lexical = new[] { Lexical("a", 1), Lexical("b", 2) };
        var vector = new[] { Vector("paraphrase", 1, 0.91), Vector("a", 2, 0.80) };

        var fused = RrfFusion.Fuse(lexical, vector, 10);

        // "a" agrees across both paths and leads; the paraphrase, found by one
        // path only, still beats the lexical-only "b".
        Assert.Equal("a", fused[0].Chunk.Id);
        Assert.Equal("paraphrase", fused[1].Chunk.Id);
        Assert.Equal("b", fused[2].Chunk.Id);
        Assert.Equal(1, fused[1].VectorRank);
        Assert.Null(fused[1].LexicalRank);
    }

    [Fact]
    public void Rrf_PreservesStrongExactLexicalHit()
    {
        // A rare identifier. Lexical is certain; the semantic side has returned
        // four vaguely related files. The exact hit must still lead — this is
        // the direction that would quietly break if scores were averaged.
        var lexical = new[] { Lexical("PhotoVectorIndexService.cs#3", 1) };
        var vector = new[]
        {
            Vector("vaguely-related-1", 1, 0.74),
            Vector("vaguely-related-2", 2, 0.73),
            Vector("vaguely-related-3", 3, 0.72),
            Vector("PhotoVectorIndexService.cs#3", 4, 0.70),
        };

        var fused = RrfFusion.Fuse(lexical, vector, 10);

        Assert.Equal("PhotoVectorIndexService.cs#3", fused[0].Chunk.Id);
        Assert.Equal(1, fused[0].LexicalRank);
        Assert.Equal(4, fused[0].VectorRank);
    }

    [Fact]
    public void Rrf_IsDeterministicOnTies()
    {
        // RRF's values come from a small set of ranks, so exact ties are
        // constant rather than rare. Two candidates that tie must order the same
        // way on every machine, or a golden test is testing the sort's mood.
        var lexical = new[] { Lexical("zeta", 1), Lexical("alpha", 1) };
        var vector = Array.Empty<RagVectorHit>();

        var first = RrfFusion.Fuse(lexical, vector, 10).Select(c => c.Chunk.Id).ToList();
        var second = RrfFusion.Fuse(lexical.Reverse().ToArray(), vector, 10)
            .Select(c => c.Chunk.Id).ToList();

        Assert.Equal(first, second);
        Assert.Equal(new[] { "alpha", "zeta" }, first);
    }

    [Fact]
    public void Rrf_Works_When_Either_Path_Returns_Nothing()
    {
        // Semantic off, or a corpus with no lexical overlap. Neither is an
        // error, and fusion must not need both to produce a result.
        var lexicalOnly = RrfFusion.Fuse(new[] { Lexical("a", 1) }, Array.Empty<RagVectorHit>(), 5);
        Assert.Equal("a", Assert.Single(lexicalOnly).Chunk.Id);

        var vectorOnly = RrfFusion.Fuse(
            Array.Empty<RagLexicalHit>(), new[] { Vector("b", 1, 0.9) }, 5);
        Assert.Equal("b", Assert.Single(vectorOnly).Chunk.Id);

        Assert.Empty(RrfFusion.Fuse(Array.Empty<RagLexicalHit>(), Array.Empty<RagVectorHit>(), 5));
    }

    [Fact]
    public void Rrf_Respects_Its_Bound()
    {
        var lexical = Enumerable.Range(1, 50).Select(i => Lexical($"l{i:D2}", i)).ToArray();
        var vector = Enumerable.Range(1, 50).Select(i => Vector($"v{i:D2}", i, 0.9)).ToArray();

        Assert.Equal(7, RrfFusion.Fuse(lexical, vector, 7).Count);
        Assert.Empty(RrfFusion.Fuse(lexical, vector, 0));
    }

    [Fact]
    public void Rrf_Ranks_Are_Contiguous_From_One()
    {
        var fused = RrfFusion.Fuse(
            new[] { Lexical("a", 1), Lexical("b", 2) },
            new[] { Vector("c", 1, 0.9) },
            10);

        Assert.Equal(new[] { 1, 2, 3 }, fused.Select(c => c.Rank).ToArray());
    }

    [Fact]
    public void Agreement_Beats_Either_Path_Alone()
    {
        // The property k=60 is chosen for: a candidate both paths rank modestly
        // beats one that a single path ranks first. That is what makes fusion
        // worth doing rather than concatenating two result lists.
        var fused = RrfFusion.Fuse(
            new[] { Lexical("agreed", 3), Lexical("lexical-only", 1) },
            new[] { Vector("agreed", 3, 0.8), Vector("vector-only", 1, 0.9) },
            10);

        Assert.Equal("agreed", fused[0].Chunk.Id);
    }

    private static RagLexicalHit Lexical(string id, int rank)
        => new(Chunk(id), Score: 10.0 / rank, MatchedAny: 2, MatchedLiteral: 2,
            MatchedHighField: true, Rank: rank);

    private static RagVectorHit Vector(string id, int rank, double score)
        => new(Chunk(id), score, rank);

    private static RagIndexedChunk Chunk(string id) => new(
        Id: id,
        Domain: RagDomainKey.NubArcaRepository,
        SourceKey: id,
        Path: id,
        Title: id,
        Section: string.Empty,
        Text: $"body of {id}",
        SourceKind: RagSourceKinds.SourceCode,
        Language: RagLanguages.Unknown,
        Revision: "r",
        Feature: string.Empty,
        Aliases: Array.Empty<string>(),
        Audience: string.Empty,
        Intent: string.Empty,
        Priority: 50);
}

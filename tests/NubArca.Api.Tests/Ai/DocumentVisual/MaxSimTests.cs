using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE LATE-INTERACTION PRIMITIVE, against arithmetic rather than against a
// model.
//
// MaxSim is the whole of what NubArca depends on from the ColVision family:
// swap ColPali for ColQwen for whatever comes next, and this function is what
// must keep meaning the same thing. So it is tested against numbers a person
// can verify on paper, not against "the reranker moved the right document up",
// which would pass for a checkpoint and fail for its successor while the code
// stayed correct.
//
// The refusals matter as much as the sum. Every one of them is a case where a
// plausible implementation returns a FINITE NUMBER for inputs that do not
// describe comparable sequences — and a finite number ranks.
public sealed class MaxSimTests
{
    private static readonly IAiVectorSerializer Serializer = new AiVectorSerializer();

    [Fact]
    public void Score_Matches_A_Hand_Computed_Fixture()
    {
        // Two query vectors, three page vectors, two dimensions.
        //
        //   q1 = (1, 0)   against  (1,0)->1   (0,1)->0    (0.6,0.8)->0.6   max = 1.0
        //   q2 = (0, 1)   against  (1,0)->0   (0,1)->1    (0.6,0.8)->0.8   max = 1.0
        //   total = 2.0
        var query = new[] { new[] { 1f, 0f }, new[] { 0f, 1f } };
        var page = new[] { new[] { 1f, 0f }, new[] { 0f, 1f }, new[] { 0.6f, 0.8f } };

        Assert.Equal(2.0, MaxSim.Score(query, page, 2), precision: 6);
    }

    [Fact]
    public void Score_Sums_The_Best_Match_Per_Query_Vector_Not_The_Best_Overall()
    {
        // The property that makes late interaction useful: two query ideas may
        // match two DIFFERENT regions, and both count.
        //
        //   q1 = (1, 0)   best against (1,0) = 1.0
        //   q2 = (0, 1)   best against (0,1) = 1.0
        //   a single-best-pair implementation would answer 1.0
        var query = new[] { new[] { 1f, 0f }, new[] { 0f, 1f } };
        var page = new[] { new[] { 1f, 0f }, new[] { 0f, 1f } };

        Assert.Equal(2.0, MaxSim.Score(query, page, 2), precision: 6);
    }

    [Fact]
    public void Score_Is_Deterministic()
    {
        var query = new[] { new[] { 0.1f, 0.9f }, new[] { -0.3f, 0.4f } };
        var page = new[] { new[] { 0.5f, 0.5f }, new[] { 0.2f, -0.7f } };

        var first = MaxSim.Score(query, page, 2);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, MaxSim.Score(query, page, 2));
        }
    }

    [Theory]
    [InlineData(3)] // page vectors are 2-D
    [InlineData(1)]
    public void Score_Refuses_A_Dimension_Mismatch(int dimension)
    {
        var query = new[] { new[] { 1f, 0f } };
        var page = new[] { new[] { 1f, 0f } };

        // NO SILENT RESHAPE. Truncating to the shorter side, or padding to the
        // longer, both produce a number — and a number ranks.
        Assert.True(double.IsNaN(MaxSim.Score(query, page, dimension)));
    }

    [Fact]
    public void Score_Refuses_A_Ragged_Sequence()
    {
        var query = new[] { new[] { 1f, 0f } };
        var page = new[] { new[] { 1f, 0f }, new[] { 1f, 0f, 0f } };

        Assert.True(double.IsNaN(MaxSim.Score(query, page, 2)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Score_Refuses_A_NonFinite_Component(float poison)
    {
        var query = new[] { new[] { poison, 0f } };
        var page = new[] { new[] { 1f, 0f } };

        Assert.True(double.IsNaN(MaxSim.Score(query, page, 2)));
    }

    [Fact]
    public void Score_Refuses_An_Empty_Side()
    {
        Assert.True(double.IsNaN(MaxSim.Score(Array.Empty<float[]>(), new[] { new[] { 1f } }, 1)));
        Assert.True(double.IsNaN(MaxSim.Score(new[] { new[] { 1f } }, Array.Empty<float[]>(), 1)));
    }

    // ---- storage round-trip -------------------------------------------------

    [Fact]
    public void Encode_Then_Decode_Preserves_Every_Vector()
    {
        var vectors = new[]
        {
            new[] { 0.1f, -0.2f, 0.3f },
            new[] { 0.4f, 0.5f, -0.6f },
        };

        var bytes = MaxSim.Encode(Serializer, vectors, 3);
        var decoded = MaxSim.Decode(Serializer, bytes, 2, 3);

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Count);
        Assert.Equal(vectors[0], decoded[0]);
        Assert.Equal(vectors[1], decoded[1]);
    }

    [Fact]
    public void Decode_Refuses_A_Blob_Whose_Length_Contradicts_Its_Declaration()
    {
        // THE FAILURE THIS PREVENTS IS SILENT. A blob of six floats read as two
        // vectors of two would decode cleanly, score finitely and rank a page
        // that does not exist. So the declared shape is checked against the
        // byte length rather than trusted.
        var bytes = MaxSim.Encode(Serializer, new[] { new[] { 1f, 2f, 3f } }, 3);

        Assert.Null(MaxSim.Decode(Serializer, bytes, 2, 2));
        Assert.Null(MaxSim.Decode(Serializer, bytes, 1, 2));
        Assert.Null(MaxSim.Decode(Serializer, bytes, 3, 3));
        Assert.NotNull(MaxSim.Decode(Serializer, bytes, 1, 3));
    }

    [Fact]
    public void Decode_Refuses_A_NonPositive_Shape()
    {
        var bytes = MaxSim.Encode(Serializer, new[] { new[] { 1f } }, 1);

        Assert.Null(MaxSim.Decode(Serializer, bytes, 0, 1));
        Assert.Null(MaxSim.Decode(Serializer, bytes, 1, 0));
    }

    [Fact]
    public void Encode_Refuses_A_Vector_Of_The_Wrong_Width()
    {
        Assert.Throws<ArgumentException>(
            () => MaxSim.Encode(Serializer, new[] { new[] { 1f, 2f }, new[] { 3f } }, 2));
    }

    // ---- the float16 question, recorded rather than guessed ------------------

    [Fact]
    public void Stored_Multi_Vectors_Are_Exact_Float32()
    {
        // Section 25 of the specification asks for a MEASUREMENT before float16
        // is adopted, and this release has not made it. So the canonical
        // encoding is the same exact float32 every other NubArca embedding uses,
        // and this test is what fails if somebody halves it without doing the
        // work: a lossy round-trip changes MaxSim scores, and by how much is
        // precisely the unmeasured quantity.
        var vectors = new[] { new[] { 0.1234567f, -0.7654321f } };
        var decoded = MaxSim.Decode(
            Serializer, MaxSim.Encode(Serializer, vectors, 2), 1, 2);

        Assert.NotNull(decoded);
        Assert.Equal(0.1234567f, decoded![0][0]);
        Assert.Equal(-0.7654321f, decoded[0][1]);
    }
}

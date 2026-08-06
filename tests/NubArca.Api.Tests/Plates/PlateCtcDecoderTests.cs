using NubArca.Api.Plates.Alpr;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Pure synthetic-tensor coverage for the greedy CTC OCR decoder: collapse of
// consecutive repeats, blank removal, empty output, batch-dim shape, and safe
// failure on an unsupported output shape. No ONNX / model files.
public sealed class PlateCtcDecoderTests
{
    private const string Alphabet = "ABC"; // classes = 4 (blank=0, A=1, B=2, C=3)

    // Build a [T, C] logit tensor from per-timestep argmax class indices.
    private static float[] Logits(params int[] argmaxPerStep)
    {
        const int classes = 4;
        var data = new float[argmaxPerStep.Length * classes];
        for (var t = 0; t < argmaxPerStep.Length; t++)
        {
            data[t * classes + argmaxPerStep[t]] = 10f; // dominant logit
        }
        return data;
    }

    [Fact]
    public void Decodes_Collapsing_Repeats_And_Dropping_Blanks()
    {
        // blank, A, A, blank, B → "AB" (A collapses, blanks drop).
        var data = Logits(0, 1, 1, 0, 2);
        var decoded = PlateCtcDecoder.Decode(data, new[] { 5, 4 }, Alphabet);

        Assert.Equal("AB", decoded.Text);
        Assert.True(decoded.Confidence > 0.9, $"confidence was {decoded.Confidence}");
    }

    [Fact]
    public void Keeps_Repeats_Separated_By_Blank()
    {
        // A, blank, A → "AA" (the blank breaks the CTC collapse).
        var data = Logits(1, 0, 1);
        var decoded = PlateCtcDecoder.Decode(data, new[] { 3, 4 }, Alphabet);
        Assert.Equal("AA", decoded.Text);
    }

    [Fact]
    public void Empty_When_All_Blank()
    {
        var data = Logits(0, 0, 0);
        var decoded = PlateCtcDecoder.Decode(data, new[] { 3, 4 }, Alphabet);
        Assert.Equal(string.Empty, decoded.Text);
        Assert.Equal(0.0, decoded.Confidence, 6);
    }

    [Fact]
    public void Accepts_Leading_Batch_Dim()
    {
        var data = Logits(1, 2, 3); // A B C
        var decoded = PlateCtcDecoder.Decode(data, new[] { 1, 3, 4 }, Alphabet);
        Assert.Equal("ABC", decoded.Text);
    }

    [Fact]
    public void Throws_On_Unsupported_Output_Shape()
    {
        var data = new float[5 * 7];
        Assert.Throws<PlateModelOutputException>(() =>
            PlateCtcDecoder.Decode(data, new[] { 5, 7 }, Alphabet));
    }
}

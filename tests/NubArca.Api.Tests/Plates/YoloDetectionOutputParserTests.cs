using NubArca.Api.Plates.Alpr;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Pure synthetic-tensor coverage for the YOLO detector output parser: layout
// detection, confidence filtering, letterbox unmap, NMS, and safe failure on an
// unsupported output shape. No ONNX / model files.
public sealed class YoloDetectionOutputParserTests
{
    // scale=1, no padding, input == original: normalized = pixel / dim.
    private static YoloDetectionOutputParser.Letterbox Identity(int dim) =>
        new(1.0, 0.0, 0.0, dim, dim, dim, dim);

    [Fact]
    public void Parses_ChannelsFirst_Filters_By_Confidence()
    {
        // [1, F=5, N=2] channels-first: index = f*N + i.
        // Det0: cx=320,cy=320,w=64,h=32,score=0.9 ; Det1: score 0.1 (filtered).
        var data = new float[]
        {
            320, 10,   // cx
            320, 10,   // cy
            64, 5,     // w
            32, 5,     // h
            0.9f, 0.1f // class score
        };
        var boxes = YoloDetectionOutputParser.Parse(
            data, new[] { 1, 5, 2 }, numClasses: 1,
            confidenceThreshold: 0.35, nmsThreshold: 0.45, Identity(640));

        var box = Assert.Single(boxes);
        Assert.Equal(0.9, box.Score, 3);
        Assert.Equal(0.45, box.X, 3);   // (320-32)/640
        Assert.Equal(0.475, box.Y, 3);  // (320-16)/640
        Assert.Equal(0.10, box.Width, 3);
        Assert.Equal(0.05, box.Height, 3);
    }

    [Fact]
    public void Parses_CandidatesFirst_Layout()
    {
        // [1, N=1, F=5] candidates-first: [cx,cy,w,h,score].
        var data = new float[] { 160, 160, 32, 32, 0.8f };
        var boxes = YoloDetectionOutputParser.Parse(
            data, new[] { 1, 1, 5 }, numClasses: 1,
            confidenceThreshold: 0.35, nmsThreshold: 0.45, Identity(320));

        var box = Assert.Single(boxes);
        Assert.Equal(0.45, box.X, 3);  // (160-16)/320
        Assert.Equal(0.10, box.Width, 3);
    }

    [Fact]
    public void Unmaps_Letterbox_To_Original_Coordinates()
    {
        // Original 320x320 scaled by 2 into a 640 input, no padding.
        var lb = new YoloDetectionOutputParser.Letterbox(2.0, 0.0, 0.0, 640, 640, 320, 320);
        var data = new float[] { 320, 320, 64, 64, 0.9f }; // input space
        var boxes = YoloDetectionOutputParser.Parse(
            data, new[] { 1, 1, 5 }, numClasses: 1, 0.35, 0.45, lb);

        var box = Assert.Single(boxes);
        // center 320/scale=160 px → 160/320 = 0.5 center; w 64/2=32 px → 0.1.
        Assert.Equal(0.45, box.X, 3);
        Assert.Equal(0.10, box.Width, 3);
    }

    [Fact]
    public void Applies_NonMaxSuppression()
    {
        // Two near-identical boxes; NMS keeps the higher-scoring one.
        var data = new float[]
        {
            160, 160,   // cx
            160, 160,   // cy
            80, 80,     // w
            80, 80,     // h
            0.9f, 0.6f  // scores
        };
        var boxes = YoloDetectionOutputParser.Parse(
            data, new[] { 1, 5, 2 }, numClasses: 1, 0.35, 0.45, Identity(320));

        Assert.Single(boxes);
        Assert.Equal(0.9, boxes[0].Score, 3);
    }

    [Fact]
    public void Throws_On_Unsupported_Output_Shape()
    {
        // numClasses=1 → F=5, but neither dim is 5.
        var data = new float[6];
        Assert.Throws<PlateModelOutputException>(() =>
            YoloDetectionOutputParser.Parse(data, new[] { 1, 3, 2 }, numClasses: 1, 0.35, 0.45, Identity(640)));
    }
}

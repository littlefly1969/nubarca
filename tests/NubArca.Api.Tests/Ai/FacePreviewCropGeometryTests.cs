using NubArca.Api.Ai.Faces;
using SixLabors.ImageSharp;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Pure geometry of the face-preview crop. The context viewer overlays the raw
// normalized FaceDetection bounding box on the EXIF-oriented image, so the crop
// must equal that same box expanded 15% per side, squared (centered on the
// expanded rect), and clamped to image bounds. No landmark centering, no
// detection change.
public sealed class FacePreviewCropGeometryTests
{
    private const double Padding = 0.15; // 15% per side (production default)

    [Fact]
    public void Crop_Equals_Bbox_Expanded_15pct_Per_Side_Then_Squared()
    {
        // 1000x1000 image, face bbox (0.4, 0.3, 0.2, 0.1) → pixels (400, 300, 200, 100).
        var rect = FacePreviewService.ComputeCropRect(1000, 1000, 0.4, 0.3, 0.2, 0.1, Padding);

        // Expanded rectangle: x=400-30=370, y=300-15=285, w=260, h=130.
        // Squared on the expanded centre (500, 350): side = max(260,130) = 260.
        // x = 500 - 130 = 370, y = 350 - 130 = 220.
        Assert.Equal(new Rectangle(370, 220, 260, 260), rect);

        // The square is centered on the original box centre (0.5, 0.35) — no shift.
        Assert.Equal(370 + 260 / 2, 500);
        Assert.Equal(220 + 260 / 2, 350);
    }

    [Fact]
    public void Square_Fully_Contains_The_Expanded_Box()
    {
        // Tall, narrow face: expanded rect is 130px wide × 195px tall; the square
        // (side 195) must contain it, with the width margin split evenly.
        var rect = FacePreviewService.ComputeCropRect(1000, 1000, 0.4, 0.3, 0.1, 0.15, Padding);
        // fw=100, fh=150 → ew=130, eh=195; centre (450, 375+? ) → side 195.
        Assert.Equal(195, rect.Width);
        Assert.Equal(rect.Width, rect.Height);
        // Expanded box x-range [385, 515] ⊆ square x-range; y-range fills the square.
        double ex = 0.4 * 1000 - (0.1 * 1000) * Padding; // 385
        double ew = (0.1 * 1000) * (1 + 2 * Padding);     // 130
        Assert.True(rect.X <= ex);
        Assert.True(rect.X + rect.Width >= ex + ew);
    }

    [Fact]
    public void Crop_Clamps_At_Top_Left_Corner()
    {
        // Face flush in the corner: the expanded/centered square would run off the
        // top-left; it must clamp to (0, 0) and stay square.
        var rect = FacePreviewService.ComputeCropRect(400, 400, 0.0, 0.0, 0.1, 0.1, Padding);
        // 40px face → side 52; centre would be (20,20) → x=y=-6 → clamped to 0.
        Assert.Equal(new Rectangle(0, 0, 52, 52), rect);
    }

    [Fact]
    public void Crop_Never_Exceeds_Image_And_Stays_In_Bounds()
    {
        // A near-full-frame face on a non-square image: the square is capped at the
        // shorter edge and clamped so it never leaves the image.
        var rect = FacePreviewService.ComputeCropRect(300, 500, 0.0, 0.0, 1.0, 1.0, Padding);
        Assert.Equal(300, rect.Width); // capped at min(300, 500)
        Assert.Equal(rect.Width, rect.Height);
        Assert.True(rect.X >= 0 && rect.X + rect.Width <= 300);
        Assert.True(rect.Y >= 0 && rect.Y + rect.Height <= 500);
    }

    [Fact]
    public void Degenerate_Zero_Box_Falls_Back_To_Centre_Square()
    {
        var rect = FacePreviewService.ComputeCropRect(400, 600, 0.5, 0.5, 0.0, 0.0, Padding);
        Assert.Equal(400, rect.Width); // min(width, height)
        Assert.Equal(rect.Width, rect.Height);
        Assert.True(rect.X >= 0 && rect.X + rect.Width <= 400);
        Assert.True(rect.Y >= 0 && rect.Y + rect.Height <= 600);
    }
}

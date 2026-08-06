using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Ai.Onnx.Face;

// Deterministic ImageSharp preprocessing for the ONNX face harness: detector
// letterboxing, 5-point similarity alignment, and recognition-crop normalization.
// Pure (ImageSharp only, no ONNX runtime) so it is unit-testable. One image at a
// time; the source blob is never modified and NO persistent face crop / thumbnail
// is produced as a side effect — aligned crops live only in memory here.
//
// Channel order: SCRFD and ArcFace ONNX exports are trained with swapRB=True, so
// the network input is RGB — exactly what ImageSharp decodes into Rgb24. Both
// stages emit an NCHW (1×3×H×W) float tensor with value (pixel − mean[c]) / std[c].
public sealed class OnnxFacePreprocessor
{
    // Detector input: a square letterboxed tensor plus the scale that maps
    // detector-input pixel coordinates back to the oriented-image space
    // (orientedCoord = inputCoord / Scale). The image is placed top-left and the
    // remainder padded with black (matching InsightFace's zero pad).
    public readonly record struct DetectionInput(float[] Data, int Size, float Scale);

    public DetectionInput PrepareDetection(Image<Rgb24> oriented, OnnxFaceModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(oriented);
        ArgumentNullException.ThrowIfNull(config);

        var size = config.DetectorInputSize;
        var scale = Math.Min(size / (float)oriented.Width, size / (float)oriented.Height);
        var newW = Math.Max(1, (int)Math.Round(oriented.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(oriented.Height * scale));

        using var resized = oriented.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(newW, newH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        // Black square canvas, resized image pasted at the top-left corner.
        using var canvas = new Image<Rgb24>(size, size, new Rgb24(0, 0, 0));
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(0, 0), 1f));

        var data = ToChw(canvas, config.DetectorMean, config.DetectorStd);
        return new DetectionInput(data, size, scale);
    }

    // Align the face described by srcLandmarks (oriented-image pixel coords,
    // x0,y0,x1,y1,…) to the canonical recognition crop and return the recognition
    // input tensor. Returns false when the similarity estimate is degenerate.
    public bool TryPrepareRecognition(
        Image<Rgb24> oriented, IReadOnlyList<float> srcLandmarks, OnnxFaceModelConfig config, out float[] data)
    {
        ArgumentNullException.ThrowIfNull(oriented);
        ArgumentNullException.ThrowIfNull(config);
        data = Array.Empty<float>();

        var outSize = config.RecognitionInputSize;
        if (!FaceAlignment.TryEstimateSimilarity(srcLandmarks, outSize, out var matrix))
        {
            return false;
        }

        using var aligned = oriented.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, oriented.Width, oriented.Height),
            matrix,
            new Size(outSize, outSize),
            KnownResamplers.Bicubic));

        data = ToChw(aligned, config.RecognitionMean, config.RecognitionStd);
        return true;
    }

    // Recognition preprocessing for an ALREADY-ALIGNED crop (the IFaceEmbedder
    // contract path): stretch to the recognition input and normalize. No
    // detection/alignment is performed here.
    public float[] PrepareAlignedCrop(ReadOnlySpan<byte> cropBytes, OnnxFaceModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var outSize = config.RecognitionInputSize;

        using var image = Image.Load<Rgb24>(cropBytes.ToArray());
        image.Mutate(ctx =>
        {
            ctx.AutoOrient();
            ctx.Resize(new ResizeOptions
            {
                Size = new Size(outSize, outSize),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic,
            });
        });

        return ToChw(image, config.RecognitionMean, config.RecognitionStd);
    }

    // NCHW (N=1) channel-major float tensor: all R, then all G, then all B, with
    // value (pixel − mean[c]) / std[c].
    private static float[] ToChw(Image<Rgb24> image, float[] mean, float[] std)
    {
        var w = image.Width;
        var h = image.Height;
        var plane = w * h;
        var data = new float[3 * plane];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * w;
                for (var x = 0; x < row.Length; x++)
                {
                    var px = row[x];
                    var idx = rowOffset + x;
                    data[idx] = (px.R - mean[0]) / std[0];
                    data[plane + idx] = (px.G - mean[1]) / std[1];
                    data[2 * plane + idx] = (px.B - mean[2]) / std[2];
                }
            }
        });

        return data;
    }
}

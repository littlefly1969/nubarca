using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Ai.Onnx;

// Phase 2A: deterministic, model-specific image preprocessing for ONNX image
// embedders. Pure (ImageSharp only, no ONNX runtime) so it is unit-testable.
//
// Pipeline (documented in docs/ai-image-onnx-evaluation.md):
//   1. decode bytes as RGB24 (alpha dropped),
//   2. apply EXIF orientation (AutoOrient) so portrait/rotated images embed
//      the same as when viewed,
//   3. resize to the model's square input — Stretch (resize-to-square) or
//      ShortestCrop (resize shortest side then center-crop), with a fixed
//      Bicubic resampler for determinism,
//   4. emit a CHW float tensor with value = (pixel/255 - mean[c]) / std[c].
//
// One image at a time; the source blob is never modified and no derived
// artifact (thumbnail/preview) is produced as a side effect.
public sealed class OnnxImagePreprocessor
{
    // Result tensor in NCHW layout with N=1: data length == 3 * Height * Width,
    // channel-major (all R, then all G, then all B).
    public readonly record struct PreprocessedImage(float[] Data, int Channels, int Height, int Width);

    public PreprocessedImage Preprocess(ReadOnlySpan<byte> imageBytes, OnnxImageModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Decode + EXIF-orient + resize. Image.Load throws on corrupt/unsupported
        // input — the caller treats that as a per-image failure, never a crash.
        using var image = Image.Load<Rgb24>(imageBytes.ToArray());
        image.Mutate(ctx =>
        {
            ctx.AutoOrient();
            ctx.Resize(new ResizeOptions
            {
                Size = new Size(config.InputSize, config.InputSize),
                Mode = config.ResizeMode == OnnxResizeModes.ShortestCrop
                    ? ResizeMode.Crop      // resize-to-cover + center-crop
                    : ResizeMode.Stretch,  // resize directly to the square
                Sampler = KnownResamplers.Bicubic,
                Position = AnchorPositionMode.Center,
            });
        });

        var size = config.InputSize;
        var plane = size * size;
        var data = new float[3 * plane];
        var mean = config.Mean;
        var std = config.Std;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * size;
                for (var x = 0; x < row.Length; x++)
                {
                    var px = row[x];
                    var idx = rowOffset + x;
                    data[idx] = (px.R / 255f - mean[0]) / std[0];                 // R plane
                    data[plane + idx] = (px.G / 255f - mean[1]) / std[1];         // G plane
                    data[2 * plane + idx] = (px.B / 255f - mean[2]) / std[2];     // B plane
                }
            }
        });

        return new PreprocessedImage(data, 3, size, size);
    }
}

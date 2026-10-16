using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Ai.SubjectCutout;

// The pixel side of "copy subject", kept pure so it is tested without a model:
// the photo becomes the model's input tensor, and the model's logits become the
// alpha channel of a cutout trimmed to the subject.
public static class SubjectCutoutImaging
{
    // ImageNet statistics: what BiRefNet was trained with.
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    // A pixel more transparent than this is background: it is written fully
    // transparent and does not count toward the subject's bounding box, so the
    // faint haze a soft mask leaves around a photo does not widen the crop.
    internal const byte AlphaFloor = 16;

    // Below this share of the photo, the mask found nothing worth lifting.
    internal const double MinSubjectFraction = 0.002;

    // Breathing room kept around the subject, as a share of the photo's long edge.
    internal const double PaddingFraction = 0.01;

    // The photo stretched to the model's square (BiRefNet is trained on
    // stretched squares, not letterboxed ones), ImageNet-normalized, NCHW.
    public static float[] ToInputTensor(Image<Rgb24> photo, int size)
    {
        using var square = photo.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle,
        }));

        var plane = size * size;
        var data = new float[3 * plane];
        square.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * size;
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    data[offset + x] = (p.R / 255f - Mean[0]) / Std[0];
                    data[plane + offset + x] = (p.G / 255f - Mean[1]) / Std[1];
                    data[2 * plane + offset + x] = (p.B / 255f - Mean[2]) / Std[2];
                }
            }
        });
        return data;
    }

    // The cutout at the photo's own resolution, trimmed to the subject, or null
    // when the mask holds no subject at all.
    public static Image<Rgba32>? ToCutout(Image<Rgb24> photo, float[] logits, int size)
    {
        if (logits.Length != size * size)
        {
            throw new ArgumentException("Mask does not match the model size.", nameof(logits));
        }

        using var mask = new Image<L8>(size, size);
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * size;
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new L8(ToAlpha(logits[offset + x]));
                }
            }
        });
        mask.Mutate(ctx => ctx.Resize(photo.Width, photo.Height, KnownResamplers.Triangle));

        var (minX, minY, maxX, maxY, subjectPixels) = (photo.Width, photo.Height, -1, -1, 0L);
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].PackedValue < AlphaFloor) continue;
                    subjectPixels++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        });
        if (maxX < 0 || subjectPixels < (long)photo.Width * photo.Height * MinSubjectFraction)
        {
            return null;
        }

        var pad = (int)Math.Round(Math.Max(photo.Width, photo.Height) * PaddingFraction);
        var left = Math.Max(0, minX - pad);
        var top = Math.Max(0, minY - pad);
        var right = Math.Min(photo.Width - 1, maxX + pad);
        var bottom = Math.Min(photo.Height - 1, maxY + pad);

        var cutout = new Image<Rgba32>(right - left + 1, bottom - top + 1);
        photo.ProcessPixelRows(mask, cutout, (source, alpha, target) =>
        {
            for (var y = 0; y < target.Height; y++)
            {
                var sourceRow = source.GetRowSpan(top + y);
                var alphaRow = alpha.GetRowSpan(top + y);
                var targetRow = target.GetRowSpan(y);
                for (var x = 0; x < targetRow.Length; x++)
                {
                    var a = alphaRow[left + x].PackedValue;
                    var p = sourceRow[left + x];
                    targetRow[x] = a < AlphaFloor ? default : new Rgba32(p.R, p.G, p.B, a);
                }
            }
        });
        return cutout;
    }

    internal static byte ToAlpha(float logit)
    {
        var probability = 1.0 / (1.0 + Math.Exp(-logit));
        return (byte)Math.Clamp(Math.Round(probability * 255.0), 0, 255);
    }
}

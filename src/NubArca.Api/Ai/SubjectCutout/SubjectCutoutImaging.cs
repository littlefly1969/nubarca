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

    // A tapped candidate counts as a subject when at least this share of it is
    // the cutout model's foreground.
    internal const double MinInsideFraction = 0.9;

    // How far a tapped selection is grown before it bounds the cutout's alpha,
    // as a share of the photo's long edge: enough for hair and soft edges the
    // point model leaves out, too little to reach a neighbour's.
    internal const double GrowFraction = 0.02;

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
        using var mask = ToAlphaMask(photo, logits, size);
        return Trim(photo, mask, out _);
    }

    // The cutout model's logits as an alpha plane at the photo's resolution.
    public static Image<L8> ToAlphaMask(Image<Rgb24> photo, float[] logits, int size)
    {
        if (logits.Length != size * size)
        {
            throw new ArgumentException("Mask does not match the model size.", nameof(logits));
        }

        var mask = ToPlane(logits, 0, size);
        mask.Mutate(ctx => ctx.Resize(photo.Width, photo.Height, KnownResamplers.Triangle));
        return mask;
    }

    // The photo under `mask`, trimmed to the subject, or null when the mask
    // holds no subject at all. `region` is where the cutout sits in the photo,
    // so a tap on the cutout can be mapped back onto the photo.
    public static Image<Rgba32>? Trim(Image<Rgb24> photo, Image<L8> mask, out Rectangle region)
    {
        region = default;
        if (mask.Width != photo.Width || mask.Height != photo.Height)
        {
            throw new ArgumentException("Mask does not match the photo.", nameof(mask));
        }

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

        region = new Rectangle(left, top, right - left + 1, bottom - top + 1);
        var cutout = new Image<Rgba32>(region.Width, region.Height);
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

    // "Tap a subject": of the candidate masks the point model proposed around
    // the touched point (a shirt, the whole person, two people…), the subject is
    // the LARGEST one that lies within the cutout model's foreground. A
    // candidate mostly outside it is background the touch happened to hit.
    // When the touch lands on a foreground region that stands apart from the
    // others, the choice is also kept to that region: a promptable segmenter
    // happily proposes "both people" for two similar subjects, and the
    // touched one is what was asked for.
    // Returns the chosen candidate at the photo's resolution (row-major), or
    // null when the touch hit no subject. `x`/`y` are normalized to the photo.
    public static bool[]? SelectSubject(Image<L8> alpha, float[] candidates, int count, int edge, float x, float y)
    {
        if (count < 1 || candidates.Length != count * edge * edge)
        {
            throw new ArgumentException("Candidates do not match their shape.", nameof(candidates));
        }

        var (width, height) = (alpha.Width, alpha.Height);
        var foreground = new bool[width * height];
        alpha.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) foreground[y * width + x] = row[x].PackedValue >= 128;
            }
        });

        bool[]? best = null;
        var bestArea = 0L;
        for (var k = 0; k < count; k++)
        {
            using var plane = ToPlane(candidates, k * edge * edge, edge);
            plane.Mutate(ctx => ctx.Resize(width, height, KnownResamplers.Triangle));
            var selected = new bool[width * height];
            var (area, inside) = (0L, 0L);
            plane.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        if (row[x].PackedValue < 128) continue;
                        var i = y * width + x;
                        selected[i] = true;
                        area++;
                        if (foreground[i]) inside++;
                    }
                }
            });
            if (area > bestArea && inside >= area * MinInsideFraction)
            {
                (best, bestArea) = (selected, area);
            }
        }
        if (best is null) return null;

        var tapX = Math.Clamp((int)(x * width), 0, width - 1);
        var tapY = Math.Clamp((int)(y * height), 0, height - 1);
        var region = RegionAt(foreground, width, height, tapX, tapY);
        if (region is null) return best;
        var kept = 0L;
        for (var i = 0; i < best.Length; i++)
        {
            best[i] &= region[i];
            if (best[i]) kept++;
        }
        return kept > 0 ? best : null;
    }

    // The 8-connected foreground region under (x, y), or null when that pixel
    // is background.
    private static bool[]? RegionAt(bool[] foreground, int width, int height, int x, int y)
    {
        var start = y * width + x;
        if (!foreground[start]) return null;
        var region = new bool[foreground.Length];
        var pending = new Stack<int>();
        region[start] = true;
        pending.Push(start);
        while (pending.Count > 0)
        {
            var index = pending.Pop();
            var (px, py) = (index % width, index / width);
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var (nx, ny) = (px + dx, py + dy);
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                var next = ny * width + nx;
                if (region[next] || !foreground[next]) continue;
                region[next] = true;
                pending.Push(next);
            }
        }
        return region;
    }

    // Keeps the cutout model's alpha only around the chosen subject. The
    // selection is grown and softened first, so the cutout model — not the
    // coarser point model — still draws every edge, hair included.
    public static void RestrictTo(Image<L8> alpha, bool[] selected)
    {
        var (width, height) = (alpha.Width, alpha.Height);
        if (selected.Length != width * height)
        {
            throw new ArgumentException("Selection does not match the mask.", nameof(selected));
        }

        var radius = Math.Max(2, (int)Math.Round(Math.Max(width, height) * GrowFraction));
        var grown = Dilate(Dilate(selected, width, height, radius, horizontal: true), width, height, radius, horizontal: false);
        using var keep = new Image<L8>(width, height);
        keep.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = new L8(grown[y * width + x] ? (byte)255 : (byte)0);
            }
        });
        keep.Mutate(ctx => ctx.GaussianBlur(radius / 3f));
        alpha.ProcessPixelRows(keep, (target, weight) =>
        {
            for (var y = 0; y < target.Height; y++)
            {
                var row = target.GetRowSpan(y);
                var weights = weight.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new L8((byte)((row[x].PackedValue * weights[x].PackedValue + 127) / 255));
                }
            }
        });
    }

    // One pass of a square dilation, along rows or columns: a pixel is set when
    // any pixel within `radius` of it on that line is set (prefix sums, O(n)).
    private static bool[] Dilate(bool[] source, int width, int height, int radius, bool horizontal)
    {
        var result = new bool[source.Length];
        var (lines, length) = horizontal ? (height, width) : (width, height);
        var prefix = new int[length + 1];
        for (var line = 0; line < lines; line++)
        {
            for (var i = 0; i < length; i++)
            {
                var index = horizontal ? line * width + i : i * width + line;
                prefix[i + 1] = prefix[i] + (source[index] ? 1 : 0);
            }
            for (var i = 0; i < length; i++)
            {
                var (from, to) = (Math.Max(0, i - radius), Math.Min(length, i + radius + 1));
                if (prefix[to] - prefix[from] > 0)
                {
                    result[horizontal ? line * width + i : i * width + line] = true;
                }
            }
        }
        return result;
    }

    // A square plane of logits as an alpha image.
    private static Image<L8> ToPlane(float[] logits, int offset, int size)
    {
        var plane = new Image<L8>(size, size);
        plane.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var start = offset + y * size;
                for (var x = 0; x < row.Length; x++) row[x] = new L8(ToAlpha(logits[start + x]));
            }
        });
        return plane;
    }

    internal static byte ToAlpha(float logit)
    {
        var probability = 1.0 / (1.0 + Math.Exp(-logit));
        return (byte)Math.Clamp(Math.Round(probability * 255.0), 0, 255);
    }
}

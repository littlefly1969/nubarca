using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Plates.Redaction;

// Applies PRIVACY-ONLY face redaction to an already-decoded plate image. Given
// the source bytes and a set of NORMALIZED [0..1] face boxes, it expands each
// box by a safety margin, clamps it to the image bounds, and heavily pixelates
// (then lightly blurs) that region — preserving the image DIMENSIONS and aspect
// ratio. Output is a re-encoded JPEG; it NEVER writes over the original blob and
// carries no ids/paths. Stateless and thread-safe (singleton).
public sealed class ImageRedactionRenderer
{
    // A produced redacted rendition: JPEG bytes + the preserved pixel dims.
    public sealed record RedactedImage(byte[] Jpeg, int Width, int Height);

    // Normalized [0..1] redaction rectangle.
    public readonly record struct NormalizedBox(double X, double Y, double Width, double Height);

    // Renders the redacted image. `expansionRatio` grows each box by that
    // fraction of its own size on every side; `pixelBlockSize` is the nominal
    // pixelation block (scaled down for small regions so tiny previews stay
    // obscured); `quality` is the JPEG quality. Throws if the source can't be
    // decoded (the caller treats that as an unrenderable source).
    public RedactedImage Render(
        ReadOnlySpan<byte> source,
        IReadOnlyList<NormalizedBox> boxes,
        double expansionRatio,
        int pixelBlockSize,
        int quality)
    {
        using var image = Image.Load<Rgba32>(source);
        var width = image.Width;
        var height = image.Height;

        foreach (var box in boxes)
        {
            var rect = ToPixelRect(box, width, height, expansionRatio);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }
            Redact(image, rect, pixelBlockSize);
        }

        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
        return new RedactedImage(buffer.ToArray(), width, height);
    }

    // Normalized box → expanded, clamped integer pixel rectangle fully inside
    // the image (defends against a box near/over the edge or slightly out of
    // range).
    private static Rectangle ToPixelRect(NormalizedBox box, int width, int height, double expansionRatio)
    {
        var expand = Math.Max(0.0, expansionRatio);

        var x = box.X - box.Width * expand;
        var y = box.Y - box.Height * expand;
        var w = box.Width * (1.0 + 2.0 * expand);
        var h = box.Height * (1.0 + 2.0 * expand);

        // Clamp to [0..1] in normalized space first.
        var x0 = Math.Clamp(x, 0.0, 1.0);
        var y0 = Math.Clamp(y, 0.0, 1.0);
        var x1 = Math.Clamp(x + w, 0.0, 1.0);
        var y1 = Math.Clamp(y + h, 0.0, 1.0);

        var px = (int)Math.Floor(x0 * width);
        var py = (int)Math.Floor(y0 * height);
        var pw = (int)Math.Ceiling((x1 - x0) * width);
        var ph = (int)Math.Ceiling((y1 - y0) * height);

        // Final clamp in pixel space so the rectangle never exceeds the image.
        px = Math.Clamp(px, 0, Math.Max(0, width - 1));
        py = Math.Clamp(py, 0, Math.Max(0, height - 1));
        pw = Math.Clamp(pw, 0, width - px);
        ph = Math.Clamp(ph, 0, height - py);

        return new Rectangle(px, py, pw, ph);
    }

    // Aggressive pixelation of a single region: crop it out, pixelate the crop,
    // then a light blur to erase residual block edges, and composite it back.
    // Working on a cropped clone keeps the effect strictly inside the box.
    private static void Redact(Image<Rgba32> image, Rectangle rect, int pixelBlockSize)
    {
        // Block size scaled to the region so a small preview stays heavily
        // obscured (never fewer than a handful of blocks across the face), and
        // never larger than the region itself.
        var minEdge = Math.Max(1, Math.Min(rect.Width, rect.Height));
        var block = Math.Clamp(pixelBlockSize, 2, minEdge);
        var regionBlock = Math.Max(2, Math.Min(block, Math.Max(2, minEdge / 4)));
        var blurSigma = Math.Max(1f, regionBlock / 2f);

        using var region = image.Clone(ctx => ctx.Crop(rect));
        region.Mutate(ctx =>
        {
            ctx.Pixelate(regionBlock);
            ctx.GaussianBlur(blurSigma);
        });
        image.Mutate(ctx => ctx.DrawImage(region, new Point(rect.X, rect.Y), 1f));
    }
}

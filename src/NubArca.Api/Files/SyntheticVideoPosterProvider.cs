using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Files;

/// <summary>
/// Generates a deterministic 1280×720 synthetic JPEG poster. Requires no
/// external dependencies (no FFmpeg, no native libs, no font/drawing
/// packages — pure per-pixel math) and never reads the video blob content.
///
/// Slice 95: redesigned so the placeholder is VISIBLY intentional rather
/// than a near-black frame users mistake for a broken real poster: a subtle
/// vertical gradient, film-strip sprocket bands along the top and bottom
/// edges, and a ringed play button in the centre. Deterministic: identical
/// bytes on every run, so the content-addressed derived blob dedups across
/// all videos sharing the synthetic poster.
/// </summary>
public sealed class SyntheticVideoPosterProvider : IVideoPosterProvider
{
    private readonly MediaDerivativesOptions _derivatives;

    public SyntheticVideoPosterProvider(
        IOptions<MediaDerivativesOptions>? derivatives = null)
    {
        _derivatives = derivatives?.Value ?? new MediaDerivativesOptions();
    }

    public Task<VideoPosterResult?> TryGetPosterAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
    {
        // Synthetic poster never reads blob content.
        _ = openBlobContent;
        var (width, height) = _derivatives.PosterSize;
        var scale = Math.Min(width / (double)VideoPosterSpec.DefaultWidth,
            height / (double)VideoPosterSpec.DefaultHeight);
        var bandHeight = Math.Max(1, (int)Math.Round(56 * scale));
        var holeWidth = Math.Max(1, (int)Math.Round(26 * scale));
        var holeHeight = Math.Max(1, (int)Math.Round(26 * scale));
        var holeSpacing = Math.Max(holeWidth + 1, (int)Math.Round(72 * scale));
        var centerX = width / 2;
        var centerY = height / 2;
        var ringOuter = Math.Max(4, (int)Math.Round(118 * scale));
        var ringInner = Math.Max(2, (int)Math.Round(104 * scale));
        var triLeft = centerX - (int)Math.Round(32 * scale);
        var triRight = centerX + (int)Math.Round(52 * scale);
        var triHalfHeight = Math.Max(1, (int)Math.Round(52 * scale));

        var gradientTop = new Rgba32(40, 46, 62, 255);
        var gradientBottom = new Rgba32(20, 23, 32, 255);
        var band = new Rgba32(12, 14, 20, 255);
        var hole = new Rgba32(58, 64, 82, 255);
        var accent = new Rgba32(226, 229, 238, 255);

        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                // Vertical gradient backdrop.
                var t = height == 1 ? 0 : (float)y / (height - 1);
                var baseColor = Lerp(gradientTop, gradientBottom, t);

                var inTopBand = y < bandHeight;
                var inBottomBand = y >= height - bandHeight;
                var holeRow = (inTopBand && y >= (bandHeight - holeHeight) / 2
                        && y < (bandHeight + holeHeight) / 2)
                    || (inBottomBand && y >= height - (bandHeight + holeHeight) / 2
                        && y < height - (bandHeight - holeHeight) / 2);

                for (var x = 0; x < row.Length; x++)
                {
                    if (inTopBand || inBottomBand)
                    {
                        // Film-strip band with sprocket holes.
                        var phase = x % holeSpacing;
                        row[x] = holeRow && phase < holeWidth ? hole : band;
                        continue;
                    }

                    row[x] = baseColor;

                    // Play-button ring.
                    var dx = x - centerX;
                    var dy = y - centerY;
                    var distSq = dx * dx + dy * dy;
                    if (distSq <= ringOuter * ringOuter && distSq >= ringInner * ringInner)
                    {
                        row[x] = accent;
                        continue;
                    }

                    // Play triangle (points right), inside the ring.
                    if (x >= triLeft && x <= triRight && Math.Abs(dy) <= triHalfHeight)
                    {
                        var xRight = triLeft
                            + (int)(((double)(triHalfHeight - Math.Abs(dy)) / triHalfHeight)
                                * (triRight - triLeft));
                        if (x <= xRight)
                        {
                            row[x] = accent;
                        }
                    }
                }
            }
        });

        var encoded = new MemoryStream();
        image.Save(encoded, new JpegEncoder { Quality = 82 });
        encoded.Position = 0;
        return Task.FromResult<VideoPosterResult?>(
            new VideoPosterResult(encoded, VideoPosterSources.Synthetic));
    }

    // A synthetic provider cannot create a meaningful motion preview. Returning
    // null lets the caller keep the static poster and persist a no-retry
    // diagnostic instead of fabricating six identical frames.
    public Task<VideoPreviewStripResult?> TryGetPreviewStripAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        _ = openBlobContent;
        _ = durationSeconds;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<VideoPreviewStripResult?>(null);
    }

    private static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        255);
}

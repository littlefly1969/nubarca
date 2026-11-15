using NubArca.Api.Print;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Lithophanes;

// The owner's words on the sphere. They are drawn into the same map every
// preview and every strip is made from, as the thickest wall, so backlit they
// read dark — and what the preview shows is what the STL carries.
//
// The words are set flat, in millimetres, and then laid on the surface: a
// vertex's distance from the block's centre is measured along its own circle
// of latitude, and its height along the meridian. Letters keep their shape on
// the curve instead of widening towards the poles as an equirectangular
// drawing would.
public static class LithophaneText
{
    // Arc distances, in mm, of the photograph's visible area on the sphere: the
    // centre longitude and its top and bottom measured from the north pole.
    public readonly record struct PhotoArea(double CenterLonDegrees, double TopMm, double BottomMm);

    // centred: the words sit on the middle of the area whatever the position
    // says (a lithophane of words only, whose area is a single line).
    public static void Apply(Image<La16> map, LithophaneSettings settings, PhotoArea photo,
        bool centred = false, string? assetRoot = null, CancellationToken ct = default)
    {
        if (!settings.HasText) return;
        var lines = settings.Text!.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (lines.Length == 0) return;

        var grid = new LithophaneGrid(settings);
        // Fine enough that a letter's edge is smooth at any surface sampling.
        var pxPerMm = Math.Clamp(2 / settings.ResolutionMm, 4, 8);
        using var mask = Render(string.Join('\n', lines), settings.TextFont, settings.TextHeight * pxPerMm, assetRoot);
        var widthMm = mask.Width / pxPerMm;
        var heightMm = mask.Height / pxPerMm;

        var gap = LithophaneSettings.BlendMm + settings.TextHeight * 0.25;
        var top = centred ? (photo.TopMm + photo.BottomMm - heightMm) / 2 : settings.TextPlacement switch
        {
            "above" => photo.TopMm - gap - heightMm,
            "onPhoto" => photo.BottomMm - gap - heightMm,
            _ => photo.BottomMm + gap,
        };
        var mmPerRadian = grid.Radius;
        // The pole and the flat rim are never written: they are not a wall the words can be on.
        for (var row = 1; row < grid.Rows; row++)
        {
            ct.ThrowIfCancellationRequested();
            var theta = grid.Theta(row);
            var y = (theta * mmPerRadian - top) * pxPerMm;
            if (y < 0 || y > mask.Height - 1) continue;
            var ringMmPerDegree = mmPerRadian * Math.Sin(theta) * Math.PI / 180;
            for (var col = 0; col < grid.Columns; col++)
            {
                var lon = 360d * col / grid.Columns - 180 - photo.CenterLonDegrees;
                lon = lon - 360 * Math.Round(lon / 360);
                var x = (lon * ringMmPerDegree + widthMm / 2) * pxPerMm;
                if (x < 0 || x > mask.Width - 1) continue;
                var ink = Sample(mask, x, y);
                if (ink <= 0) continue;
                map[col, row] = Ink(map[col, row], ink, settings);
            }
        }
    }

    // The thickness under a letter is mixed toward the thickest photo wall by
    // the letter's coverage t. A map pixel holds a tone and a photo coverage
    // (see LithophaneImage); this finds the pixel whose thickness is exactly
    //   (1 − t) · current + t · MaxThickness.
    internal static La16 Ink(La16 pixel, double t, LithophaneSettings s)
    {
        var a = pixel.A / 255d;
        var alpha = a + t * (1 - a);
        if (alpha <= 0) return pixel;
        var photoWall = a > 0 ? s.Thickness(pixel.L) : 0;
        var wall = ((1 - t) * a * photoWall + t * s.MaxThicknessMm) / alpha;
        var tone = 255 * (1 - (wall - s.MinThicknessMm) / (s.MaxThicknessMm - s.MinThicknessMm));
        return new La16((byte)Math.Clamp(Math.Round(tone), 0, 255), (byte)Math.Clamp(Math.Round(255 * alpha), 0, 255));
    }

    // White letters on black, the block's cap height capPx, lines centred.
    internal static Image<L8> Render(string text, string? fontId, double capPx, string? assetRoot = null)
    {
        var family = TextFonts.Family(fontId, assetRoot);
        var probe = family.CreateFont(100);
        var capHeight = TextMeasurer.MeasureBounds("H", new TextOptions(probe)).Height;
        var font = family.CreateFont((float)(100 * capPx / Math.Max(1, capHeight)));
        var options = new RichTextOptions(font) { TextAlignment = TextAlignment.Center };
        var bounds = TextMeasurer.MeasureBounds(text, options);
        const int pad = 2;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width) + 2 * pad);
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height) + 2 * pad);
        var image = new Image<L8>(width, height, new L8(0));
        options.Origin = new PointF(pad - bounds.X, pad - bounds.Y);
        image.Mutate(x => x.DrawText(options, text, Color.White));
        return image;
    }

    private static double Sample(Image<L8> mask, double x, double y)
    {
        var x0 = (int)x; var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, mask.Width - 1); var y1 = Math.Min(y0 + 1, mask.Height - 1);
        double fx = x - x0, fy = y - y0;
        var top = mask[x0, y0].PackedValue * (1 - fx) + mask[x1, y0].PackedValue * fx;
        var bottom = mask[x0, y1].PackedValue * (1 - fx) + mask[x1, y1].PackedValue * fx;
        return (top * (1 - fy) + bottom * fy) / 255;
    }
}

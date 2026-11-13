using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Lithophanes;

public static class LithophaneImage
{
    public static Image<L8> CreateMap(Image<Rgba32> photo, LithophaneSettings settings, CancellationToken ct = default)
    {
        var grid = new LithophaneGrid(settings);
        var result = new Image<L8>(grid.Columns, grid.Rows + 1, new L8(255));
        var placed = PhotoPlacementGeometry.Place((double)photo.Width / photo.Height,
            settings.PictureWidthDegrees / settings.PictureHeightDegrees,
            new(settings.CenterX, settings.CenterY, settings.Zoom));
        try
        {
            for (var row = 1; row < grid.Rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                var v = (grid.Theta(row) * 180 / Math.PI - 90) / settings.PictureHeightDegrees + 0.5;
                if (v < 0 || v > 1) continue;
                var y = (v - placed.Top) / placed.Height;
                if (y < 0 || y > 1) continue;
                for (var col = 0; col < grid.Columns; col++)
                {
                    var u = (360d * col / grid.Columns - 180) / settings.PictureWidthDegrees + 0.5;
                    if (u < 0 || u > 1) continue;
                    var x = (u - placed.Left) / placed.Width;
                    if (x < 0 || x > 1) continue;
                    // Bilinear sampling avoids a nearest-neighbour stair pattern.
                    var px = x * (photo.Width - 1); var py = y * (photo.Height - 1);
                    var x0 = (int)px; var y0 = (int)py;
                    var x1 = Math.Min(x0 + 1, photo.Width - 1); var y1 = Math.Min(y0 + 1, photo.Height - 1);
                    var top = Luminance(photo[x0, y0]) * (1 - px + x0) + Luminance(photo[x1, y0]) * (px - x0);
                    var bottom = Luminance(photo[x0, y1]) * (1 - px + x0) + Luminance(photo[x1, y1]) * (px - x0);
                    var value = top * (1 - py + y0) + bottom * (py - y0);
                    value = Math.Clamp((value - 0.5) * settings.Contrast + 0.5 + settings.Brightness, 0, 1);
                    result[col, row] = new L8((byte)Math.Round(255 * Math.Pow(value, 1 / settings.Gamma)));
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static double Luminance(Rgba32 p)
    {
        var a = p.A / 255d;
        return a * (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255 + 1 - a;
    }
}

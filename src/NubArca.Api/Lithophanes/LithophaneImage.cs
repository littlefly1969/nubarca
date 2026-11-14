using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Lithophanes;

// One pixel per mesh vertex. L is the photograph's tone; A is how much of the
// photograph covers the vertex: 255 on the photo, 0 on the plain background,
// and a smooth ramp across the LithophaneSettings.BlendMm band just outside
// the photo's edge, so the wall never steps from photo to background. Inside
// the ramp L continues the nearest edge tone. A map saved before coverage
// existed loads with A = 255 everywhere and keeps its exact old thickness.
public static class LithophaneImage
{
    public static Image<La16> CreateMap(Image<Rgba32> photo, LithophaneSettings settings, CancellationToken ct = default)
    {
        var grid = new LithophaneGrid(settings);
        var result = new Image<La16>(grid.Columns, grid.Rows + 1, new La16(255, 0));
        var placed = PhotoPlacementGeometry.Place((double)photo.Width / photo.Height,
            settings.PictureWidthDegrees / settings.PictureHeightDegrees,
            new(settings.CenterX, settings.CenterY, settings.Zoom));
        // The photograph's visible rectangle on the sphere, in degrees of
        // longitude (−180…180) and of latitude from the north pole (0…180).
        var lonLeft = (Math.Max(0, placed.Left) - 0.5) * settings.PictureWidthDegrees;
        var lonRight = (Math.Min(1, placed.Left + placed.Width) - 0.5) * settings.PictureWidthDegrees;
        var latTop = 90 + (Math.Max(0, placed.Top) - 0.5) * settings.PictureHeightDegrees;
        var latBottom = 90 + (Math.Min(1, placed.Top + placed.Height) - 0.5) * settings.PictureHeightDegrees;
        var mmPerDegree = grid.Radius * Math.PI / 180;
        try
        {
            if (lonRight <= lonLeft || latBottom <= latTop) return result;
            for (var row = 1; row < grid.Rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                var theta = grid.Theta(row) * 180 / Math.PI;
                var lat = Math.Clamp(theta, latTop, latBottom);
                var latMm = Math.Abs(theta - lat) * mmPerDegree;
                if (latMm >= LithophaneSettings.BlendMm) continue;
                var ringMmPerDegree = mmPerDegree * Math.Sin(grid.Theta(row));
                for (var col = 0; col < grid.Columns; col++)
                {
                    // The nearest point of the photo, across the seam when that is closer.
                    var lon0 = 360d * col / grid.Columns - 180;
                    var lon = lon0; var lonDistance = double.MaxValue;
                    foreach (var candidate in new[] { lon0 - 360, lon0, lon0 + 360 })
                    {
                        var clamped = Math.Clamp(candidate, lonLeft, lonRight);
                        if (Math.Abs(candidate - clamped) < lonDistance) { lonDistance = Math.Abs(candidate - clamped); lon = clamped; }
                    }
                    var distance = Math.Sqrt(latMm * latMm + Math.Pow(lonDistance * ringMmPerDegree, 2));
                    if (distance >= LithophaneSettings.BlendMm) continue;
                    var s = 1 - distance / LithophaneSettings.BlendMm;
                    var coverage = latMm == 0 && lonDistance == 0 ? (byte)255 : (byte)Math.Round(255 * s * s * (3 - 2 * s));
                    if (coverage == 0) continue;
                    var u = lon / settings.PictureWidthDegrees + 0.5;
                    var v = (lat - 90) / settings.PictureHeightDegrees + 0.5;
                    var x = Math.Clamp((u - placed.Left) / placed.Width, 0, 1);
                    var y = Math.Clamp((v - placed.Top) / placed.Height, 0, 1);
                    result[col, row] = new La16(Tone(photo, x, y, settings), coverage);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    // What the browser preview shows: light through the actual wall, 255 where
    // it is thinnest. With the background as thin as the photo's whitest tone
    // this is exactly the photograph's tone, as before coverage existed.
    public static Image<L8> Transmission(Image<La16> map, LithophaneSettings settings)
    {
        var thinnest = Math.Min(settings.MinThicknessMm, settings.BackgroundWallMm);
        var thickest = Math.Max(settings.MaxThicknessMm, settings.BackgroundWallMm);
        var result = new Image<L8>(map.Width, map.Height);
        for (var row = 0; row < map.Height; row++)
            for (var col = 0; col < map.Width; col++)
            {
                result[col, row] = new L8((byte)Math.Round(255 * (thickest - settings.Thickness(map[col, row])) / (thickest - thinnest)));
            }
        return result;
    }

    private static byte Tone(Image<Rgba32> photo, double x, double y, LithophaneSettings settings)
    {
        // Bilinear sampling avoids a nearest-neighbour stair pattern.
        var px = x * (photo.Width - 1); var py = y * (photo.Height - 1);
        var x0 = (int)px; var y0 = (int)py;
        var x1 = Math.Min(x0 + 1, photo.Width - 1); var y1 = Math.Min(y0 + 1, photo.Height - 1);
        var top = Luminance(photo[x0, y0]) * (1 - px + x0) + Luminance(photo[x1, y0]) * (px - x0);
        var bottom = Luminance(photo[x0, y1]) * (1 - px + x0) + Luminance(photo[x1, y1]) * (px - x0);
        var value = top * (1 - py + y0) + bottom * (py - y0);
        value = Math.Clamp((value - 0.5) * settings.Contrast + 0.5 + settings.Brightness, 0, 1);
        return (byte)Math.Round(255 * Math.Pow(value, 1 / settings.Gamma));
    }

    private static double Luminance(Rgba32 p)
    {
        var a = p.A / 255d;
        return a * (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255 + 1 - a;
    }
}

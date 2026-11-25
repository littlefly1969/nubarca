using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Lithophanes;

// A deterministic continuous field in physical millimetres: no seam, no
// per-strip random state, no extra triangles, and no removal of wall material.
public static class LithophaneSurface
{
    public static double Wall(LithophaneSettings settings, LithophaneGrid grid, La16 pixel, int row, int col)
    {
        var wall = settings.Thickness(pixel);
        if (settings.TextureHeight == 0 || pixel.A == 255 || row == grid.Rows) return wall;
        var theta = grid.Theta(row);
        var rim = Smooth(Math.Clamp((grid.BottomAngle - theta) * grid.Radius / Math.Max(2, settings.TextureGrain), 0, 1));
        var phi = 2 * Math.PI * (col % grid.Columns) / grid.Columns - Math.PI;
        var scale = grid.Radius / settings.TextureGrain;
        var x = row == 0 ? 0 : scale * Math.Sin(theta) * Math.Sin(phi);
        var y = row == 0 ? 0 : -scale * Math.Sin(theta) * Math.Cos(phi);
        var z = scale * Math.Cos(theta);
        var noise = 0.75 * Noise(x, y, z) + 0.25 * Noise(x * 2, y * 2, z * 2);
        return wall + settings.TextureHeight * Smooth(1 - pixel.A / 255d) * rim * noise;
    }

    private static double Smooth(double v) => v * v * (3 - 2 * v);
    private static double Mix(double a, double b, double t) => a + (b - a) * t;

    private static double Noise(double x, double y, double z)
    {
        var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y); var iz = (int)Math.Floor(z);
        var fx = Smooth(x - ix); var fy = Smooth(y - iy); var fz = Smooth(z - iz);
        double Plane(int k) => Mix(
            Mix(Hash(ix, iy, k), Hash(ix + 1, iy, k), fx),
            Mix(Hash(ix, iy + 1, k), Hash(ix + 1, iy + 1, k), fx), fy);
        return Mix(Plane(iz), Plane(iz + 1), fz);
    }

    private static double Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u ^ (uint)y * 668265263u ^ (uint)z * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16)) / (double)uint.MaxValue;
        }
    }
}

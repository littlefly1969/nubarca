using System.Numerics;
using System.Text;

namespace NubArca.Api.Lithophanes;

// A complete, separate cap, not a partial sphere export. Its outer dome
// continues the nominal sphere. New projects use a 2 mm straight locating
// spigot; legacy projects retain their adhesive lip. Neither is a bayonet.
public static class LithophaneCap
{
    public const double SeatDepthMm = 0.8;
    public const double RadialClearanceMm = 0.2;
    public const double SeatWallMm = 0.8;
    public const double FitRadialClearanceMm = 0.15;
    public const double CrownRadiusMm = 5;
    public const double CableRadiusMm = 2;
    public const double CrownHeightMm = 5;
    private const int Columns = 128;
    private const int WindowWidth = 8;

    public static void Write(Stream destination, LithophaneSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid() || !settings.Hanging) throw new LithophaneException("invalid_settings");
        var grid = new LithophaneGrid(settings);
        var radius = grid.Radius;
        var inner = settings.InnerRadiusMm;
        var z = -radius * Math.Cos(grid.BottomAngle);
        var cutAngle = Math.Acos(z / radius);
        var crownAngle = Math.Asin(CrownRadiusMm / radius);
        var rows = Math.Clamp((int)Math.Ceiling((cutAngle - crownAngle) * radius / settings.ResolutionMm), 2, 128);
        var profile = new List<Vector2>();
        void Point(double r, double h) => profile.Add(new((float)r, (float)h));
        // Outer dome, from the mating edge to the small crown.
        for (var row = 0; row <= rows; row++)
        {
            var a = cutAngle + (crownAngle - cutAngle) * row / rows;
            Point(radius * Math.Sin(a), radius * Math.Cos(a));
        }
        var crownZ = radius * Math.Cos(crownAngle);
        Point(CrownRadiusMm, crownZ + 1);
        var outerWindow = profile.Count - 1;
        Point(CrownRadiusMm, crownZ + 4);
        Point(CrownRadiusMm, crownZ + CrownHeightMm);
        Point(CableRadiusMm, crownZ + CrownHeightMm);
        Point(CableRadiusMm, crownZ + 4);
        var innerWindow = profile.Count - 1;
        Point(CableRadiusMm, crownZ + 1);
        Point(CableRadiusMm, crownZ);
        // Hollow crown roof and inner spherical dome, ending at the locating
        // spigot. Only this short lip thickens the joint, to limit its shadow.
        var depth = settings.InternalFit ? LithophaneSettings.FitDepthMm : SeatDepthMm;
        var plugOuter = settings.OpeningMm / 2 - (settings.InternalFit ? FitRadialClearanceMm : RadialClearanceMm);
        var plugInner = plugOuter - (settings.InternalFit ? LithophaneSettings.FitWallMm : SeatWallMm);
        var innerEnd = Math.Asin(plugInner / inner);
        for (var row = 0; row <= rows; row++)
        {
            var a = crownAngle + (innerEnd - crownAngle) * row / rows;
            Point(inner * Math.Sin(a), inner * Math.Cos(a));
        }
        if (settings.PrintBrim)
        {
            // First-layer adhesion on the INSIDE of the spigot only. Peel this
            // thin annulus away after printing; the exterior and fit stay clean.
            Point(plugInner, z - depth + LithophaneSettings.BrimThicknessMm);
            Point(plugInner - LithophaneSettings.CapBrimWidthMm, z - depth + LithophaneSettings.BrimThicknessMm);
            Point(plugInner - LithophaneSettings.CapBrimWidthMm, z - depth);
        }
        else Point(plugInner, z - depth);
        Point(plugOuter, z - depth);
        Point(plugOuter, z);

        var rings = profile.Select(p => Enumerable.Range(0, Columns).Select(col =>
        {
            var phi = 2 * Math.PI * col / Columns - Math.PI / 2;
            return new Vector3((float)(p.X * Math.Sin(phi)), (float)(-p.X * Math.Cos(phi)), p.Y);
        }).ToArray()).ToArray();
        var header = new byte[80];
        Encoding.ASCII.GetBytes("NubArca flush lithophane cap; millimetres").CopyTo(header, 0);
        destination.Write(header);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        // Removed outer/inner window faces are replaced by floors/ceilings;
        // the two windows add four side-wall triangles each.
        writer.Write((uint)(2 * Columns * profile.Count + 8));
        for (var ring = 0; ring < profile.Count; ring++)
        {
            ct.ThrowIfCancellationRequested();
            for (var col = 0; col < Columns; col++)
            {
                if ((ring == outerWindow || ring == innerWindow) && InWindow(col)) continue;
                var next = (ring + 1) % profile.Count;
                var c = (col + 1) % Columns;
                Triangle(rings[ring][col], rings[next][c], rings[next][col]);
                Triangle(rings[ring][col], rings[ring][c], rings[next][c]);
            }
        }
        foreach (var start in new[] { Columns / 4 - WindowWidth / 2, Columns * 3 / 4 - WindowWidth / 2 })
        {
            // Slots pierce both sides of the crown for a hanging thread. The
            // independent axial bore stays open for a small LED cable.
            for (var col = start; col < start + WindowWidth; col++)
            {
                var c = (col + 1) % Columns;
                Face(rings[outerWindow][col], rings[outerWindow][c], rings[innerWindow + 1][c], rings[innerWindow + 1][col], Vector3.UnitZ);
                Face(rings[outerWindow + 1][col], rings[outerWindow + 1][c], rings[innerWindow][c], rings[innerWindow][col], -Vector3.UnitZ);
            }
            foreach (var col in new[] { start, start + WindowWidth })
            {
                var phi = 2 * Math.PI * col / Columns - Math.PI / 2;
                var normal = new Vector3((float)Math.Cos(phi), (float)Math.Sin(phi), 0) * (col == start ? 1 : -1);
                Face(rings[outerWindow][col], rings[outerWindow + 1][col], rings[innerWindow][col], rings[innerWindow + 1][col], normal);
            }
        }

        bool InWindow(int c) => Math.Abs(c - Columns / 4 + 0.5) < WindowWidth / 2d
            || Math.Abs(c - Columns * 3 / 4 + 0.5) < WindowWidth / 2d;
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            Directed(a, b, c); Directed(a, c, d);
            void Directed(Vector3 x, Vector3 y, Vector3 v)
            {
                if (Vector3.Dot(Vector3.Cross(y - x, v - x), normal) < 0) Triangle(x, v, y);
                else Triangle(x, y, v);
            }
        }
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Vertex(n); Vertex(a); Vertex(b); Vertex(c); writer.Write((ushort)0);
        }
        void Vertex(Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
    }
}

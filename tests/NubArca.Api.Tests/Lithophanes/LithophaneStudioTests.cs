using System.Numerics;
using System.Text.Json;
using NubArca.Api.Lithophanes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Lithophanes;

public sealed class LithophaneStudioTests
{
    private static LithophaneSettings Settings(double diameter = 80, double opening = 30) => new()
    {
        DiameterMm = diameter, OpeningMm = opening, ResolutionMm = 1, ExternalRelief = true,
        BackgroundThicknessMm = 0.8, Hanging = true, PrintAnchor = true, FrameAspect = 4d / 3, PictureScale = 0.8,
    };

    [Theory]
    [InlineData(80, 30, 0.4, 0.3, false, false, 1179360)]
    [InlineData(80, 30, 0.4, 0.3, true, true, 1221360)]
    [InlineData(80, 40, 0.8, 0.3, true, true, 1164240)]
    [InlineData(40, 16, 0.8, 1, true, true, 28928)]
    [InlineData(200, 130, 0.8, 0.6, true, false, 1693568)]
    public void Studio_Triangle_Budget_Matches_The_Browser(double diameter, double opening, double min, double step, bool hanging, bool anchor, int expected)
    {
        var s = Settings(diameter, opening) with { MinThicknessMm = min, ResolutionMm = step, Hanging = hanging, PrintAnchor = anchor, BaseCollar = true };
        Assert.Equal(expected, new LithophaneGrid(s).TriangleCount);
        Assert.True(s.IsValid());
    }

    [Theory]
    [InlineData(40, 16)]
    [InlineData(40, 26)]
    [InlineData(80, 30)]
    [InlineData(80, 40)]
    [InlineData(200, 130)]
    public void Body_And_Cap_Are_Independently_Closed_And_Oriented(double diameter, double opening)
    {
        foreach (var anchor in new[] { false, true })
        {
            var s = Settings(diameter, opening) with { PrintAnchor = anchor };
            Assert.True(s.IsValid());
            var grid = new LithophaneGrid(s);
            using var map = LithophaneImage.CreateMap(null, s);
            using var body = new MemoryStream();
            LithophaneMesh.WriteHeader(body, grid); LithophaneMesh.WriteStrip(body, s, map, 0, grid.Rows);
            Assert.Equal(grid.StlBytes, body.Length);
            var vertices = CheckMesh(body);
            var minZ = vertices.Min(v => v.Z);
            var joinZ = (float)Math.Cos(grid.BottomAngle) * (float)grid.Radius;
            Assert.Equal(joinZ - grid.BaseHeightMm, minZ, 4);
            // The extraction aperture remains unobstructed before capping.
            Assert.All(vertices.Where(v => v.Z < joinZ + 1e-4), v =>
                Assert.True(Math.Sqrt(v.X * v.X + v.Y * v.Y) >= opening / 2 - 1e-4));
            if (anchor) Assert.True(grid.PrintFootRadiusMm >= grid.BaseRadiusMm + 3);
        }
        using var cap = new MemoryStream();
        var settings = Settings(diameter, opening);
        LithophaneCap.Write(cap, settings);
        var points = CheckMesh(cap);
        var capJoin = -new LithophaneGrid(settings).Radius * Math.Cos(new LithophaneGrid(settings).BottomAngle);
        Assert.Equal(capJoin - LithophaneCap.SeatDepthMm, points.Min(v => v.Z), 4);
        var lip = points.Where(v => Math.Abs(v.Z - (capJoin - LithophaneCap.SeatDepthMm)) < 1e-4).ToList();
        Assert.Equal(opening / 2 - LithophaneCap.RadialClearanceMm, lip.Max(v => Math.Sqrt(v.X * v.X + v.Y * v.Y)), 4);
        // No triangle seals the axial cable bore, including the crown roof.
        Assert.All(points, v => Assert.True(Math.Sqrt(v.X * v.X + v.Y * v.Y) >= LithophaneCap.CableRadiusMm - 1e-5));
    }

    [Fact]
    public void External_Relief_Moves_The_Outer_Wall_And_Keeps_The_Inner_Sphere()
    {
        var s = Settings() with { Hanging = false, BaseCollar = true };
        using var white = new Image<Rgba32>(10, 10, new Rgba32(255, 255, 255));
        using var black = new Image<Rgba32>(10, 10, new Rgba32(0, 0, 0));
        var a = Equator(s, white); var b = Equator(s, black);
        Assert.Equal(s.InnerRadiusMm, a.Inner, 4);
        Assert.Equal(a.Inner, b.Inner, 4);
        Assert.Equal(s.MinThicknessMm, a.Outer - a.Inner, 4);
        Assert.Equal(s.MaxThicknessMm, b.Outer - b.Inner, 4);
        Assert.True(b.Outer > a.Outer);

        static (double Outer, double Inner) Equator(LithophaneSettings settings, Image<Rgba32> photo)
        {
            var grid = new LithophaneGrid(settings);
            using var map = LithophaneImage.CreateMap(photo, settings);
            using var output = new MemoryStream();
            LithophaneMesh.WriteHeader(output, grid);
            LithophaneMesh.WriteStrip(output, settings, map, grid.TopRows, grid.TopRows + 1);
            var points = Read(output).SelectMany(t => new[] { t.A, t.B, t.C }).Where(v => Math.Abs(v.X) < 1e-5 && v.Y < 0 && Math.Abs(v.Z) < 1e-5).Select(v => (double)v.Length()).ToList();
            return (points.Max(), points.Min());
        }
    }

    [Fact]
    public void Crop_Zoom_And_Sphere_Size_Are_Independent_And_Keep_The_Aspect()
    {
        var s = Settings() with { FrameAspect = 1, CenterX = 0.25, CenterY = 0.5, Zoom = 2 };
        using var photo = new Image<Rgba32>(120, 60, new Rgba32(255, 255, 255));
        for (var y = 0; y < photo.Height; y++) for (var x = 0; x < 60; x++) photo[x, y] = new Rgba32(0, 0, 0);
        var g = new LithophaneGrid(s);
        using var large = LithophaneImage.CreateMap(photo, s);
        using var small = LithophaneImage.CreateMap(photo, s with { PictureScale = 0.4 });
        Assert.Equal(0, large[g.Columns / 2, g.TopRows].L);
        Assert.Equal(large[g.Columns / 2, g.TopRows], small[g.Columns / 2, g.TopRows]);
        Assert.True(Covered(large) > Covered(small));
        static int Covered(Image<La16> image)
        {
            var count = 0;
            for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++) if (image[x, y].A > 0) count++;
            return count;
        }
        Assert.Equal(s.FrameAspect!.Value, s.PhotoWidthDegrees / s.PhotoHeightDegrees, 8);
        Assert.Equal(s.PhotoWidthDegrees / 2, (s with { PictureScale = 0.4 }).PhotoWidthDegrees, 8);
    }

    [Fact]
    public void Hanging_Photo_And_Words_Are_Rotated_Together_Without_Mirroring()
    {
        var standing = Settings() with { Hanging = false, BaseCollar = false, FrameAspect = null, PictureScale = null,
            PictureWidthDegrees = 100, PictureHeightDegrees = 70, Text = "R", TextPosition = "above", TextHeightMm = 6 };
        var hanging = standing with { Hanging = true };
        var grid = new LithophaneGrid(standing);
        using var photo = new Image<Rgba32>(100, 70);
        for (var y = 0; y < photo.Height; y++) for (var x = 0; x < photo.Width; x++)
            photo[x, y] = new Rgba32((byte)(x * 2), (byte)(y * 3), 0);
        using var a = LithophaneImage.CreateMap(photo, standing);
        using var b = LithophaneImage.CreateMap(photo, hanging);
        double error = 0; var count = 0;
        for (var row = 1; row < grid.Rows; row++)
        {
            var target = Math.PI - grid.Theta(row);
            if (target > grid.BottomAngle) continue;
            var otherRow = Enumerable.Range(1, grid.Rows - 1).MinBy(r => Math.Abs(grid.Theta(r) - target));
            for (var col = 0; col < grid.Columns; col++)
            {
                error += Math.Abs(standing.Thickness(a[col, row]) - hanging.Thickness(b[(grid.Columns - col) % grid.Columns, otherRow])); count++;
            }
        }
        Assert.True(error / count < 0.025, $"Mean wall error: {error / count}");
    }

    [Fact]
    public void Legacy_Settings_Omit_New_Fields_And_Invalid_Studio_Settings_Are_Rejected()
    {
        var old = new LithophaneSettings();
        var json = JsonSerializer.Serialize(old);
        foreach (var name in new[] { "ExternalRelief", "Hanging", "PrintAnchor", "InternalFit", "PrintBrim", "FrameAspect", "PictureScale", "PhotoWidthDegrees", "InnerRadiusMm" }) Assert.DoesNotContain(name, json);
        foreach (var s in new[] { Settings() with { FrameAspect = double.NaN }, Settings() with { PictureScale = 0 },
            Settings() with { FrameAspect = null }, Settings() with { OpeningMm = 10 },
            Settings() with { BackgroundThicknessMm = 0.4 }, Settings() with { ExternalRelief = false } }) Assert.False(s.IsValid());
    }

    [Theory]
    [InlineData(40, 16)]
    [InlineData(40, 26)]
    [InlineData(80, 30)]
    [InlineData(80, 40)]
    [InlineData(200, 130)]
    public void Short_Internal_Joint_And_Inward_Brims_Are_Closed_And_Leave_The_Exterior_Unchanged(double diameter, double opening)
    {
        foreach (var brim in new[] { false, true })
        {
            var s = Settings(diameter, opening) with { InternalFit = true, PrintAnchor = false, PrintBrim = brim };
            Assert.True(s.IsValid());
            var grid = new LithophaneGrid(s);
            Assert.Equal(0, grid.BaseHeightMm);
            Assert.Equal(brim ? 6 : 4, grid.RimSegments);
            using var map = LithophaneImage.CreateMap(null, s);
            using var body = new MemoryStream();
            LithophaneMesh.WriteHeader(body, grid);
            LithophaneMesh.WriteStrip(body, s, map, 0, grid.Rows);
            Assert.Equal(grid.StlBytes, body.Length);
            var vertices = CheckMesh(body);
            var z = vertices.Min(v => v.Z);
            Assert.Equal(grid.Radius * Math.Cos(grid.BottomAngle), z, 4);
            var rim = vertices.Where(v => Math.Abs(v.Z - z) < 1e-4).ToList();
            Assert.Equal(opening / 2 - (brim ? LithophaneSettings.BodyBrimWidthMm : 0), rim.Min(Radial), 4);
            Assert.Equal(grid.BaseRadiusMm, rim.Max(Radial), 4);
            var tip = vertices.Where(v => Math.Abs(v.Z - z - 2) < 1e-4 && Radial(v) <= opening / 2 + 1.001).ToList();
            Assert.NotEmpty(tip);
            Assert.Equal(opening / 2, tip.Min(Radial), 4);
            Assert.Equal(opening / 2 + 1, tip.Max(Radial), 4);
            Assert.All(vertices.Where(v => v.Z > z + LithophaneSettings.BrimThicknessMm + 1e-4 && v.Z < z + 2.001),
                v => Assert.True(Radial(v) >= opening / 2 - 1e-4));
            // Every exterior ring is byte-for-byte the ordinary spherical wall.
            var plain = s with { InternalFit = false, PrintBrim = false };
            using var plainBody = new MemoryStream();
            LithophaneMesh.WriteHeader(plainBody, new LithophaneGrid(plain));
            LithophaneMesh.WriteStrip(plainBody, plain, map, 0, grid.Rows);
            var expectedOuter = Read(plainBody).SelectMany(t => new[] { t.A, t.B, t.C })
                .Where(v => Math.Abs(v.Length() - grid.Radius) < 1e-4).ToHashSet();
            var actualOuter = vertices.Where(v => Math.Abs(v.Length() - grid.Radius) < 1e-4).ToHashSet();
            Assert.True(expectedOuter.SetEquals(actualOuter));

            using var cap = new MemoryStream();
            LithophaneCap.Write(cap, s);
            var capVertices = CheckMesh(cap);
            var seatZ = -grid.Radius * Math.Cos(grid.BottomAngle) - 2;
            Assert.Equal(seatZ, capVertices.Min(v => v.Z), 4);
            var seat = capVertices.Where(v => Math.Abs(v.Z - seatZ) < 1e-4).ToList();
            var plugOuter = opening / 2 - LithophaneCap.FitRadialClearanceMm;
            Assert.Equal(plugOuter, seat.Max(Radial), 4);
            Assert.Equal(plugOuter - 1 - (brim ? LithophaneSettings.CapBrimWidthMm : 0), seat.Min(Radial), 4);
            Assert.All(capVertices, v => Assert.True(Radial(v) >= LithophaneCap.CableRadiusMm - 1e-4));
            // Brim never extends outside the fitting cylinder or into its mating surface.
            Assert.All(capVertices.Where(v => v.Z < seatZ + LithophaneSettings.BrimThicknessMm + 1e-4),
                v => Assert.True(Radial(v) <= plugOuter + 1e-4));
            if (brim)
            {
                Assert.Contains(vertices, v => Math.Abs(v.Z - z - 0.16) < 1e-4 && Math.Abs(Radial(v) - opening / 2 + 5) < 1e-4);
                Assert.Contains(capVertices, v => Math.Abs(v.Z - seatZ - 0.16) < 1e-4 && Math.Abs(Radial(v) - plugOuter + 4) < 1e-4);
            }
        }

        static double Radial(Vector3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_Joint_Is_Identical_When_Resuming_At_Every_Latitude(bool brim)
    {
        var s = Settings() with { PrintAnchor = false, InternalFit = true, PrintBrim = brim };
        var grid = new LithophaneGrid(s);
        using var map = LithophaneImage.CreateMap(null, s);
        using var whole = new MemoryStream();
        using var slices = new MemoryStream();
        LithophaneMesh.WriteHeader(whole, grid); LithophaneMesh.WriteHeader(slices, grid);
        LithophaneMesh.WriteStrip(whole, s, map, 0, grid.Rows);
        for (var row = 0; row < grid.Rows; row++) LithophaneMesh.WriteStrip(slices, s, map, row, row + 1);
        Assert.Equal(whole.ToArray(), slices.ToArray());
    }

    [Fact]
    public void Internal_Joint_Refuses_Legacy_Anchor_And_Orphan_Brims()
    {
        var s = Settings() with { PrintAnchor = false, InternalFit = true };
        Assert.True(s.IsValid());
        Assert.False((s with { Hanging = false }).IsValid());
        Assert.False((s with { PrintAnchor = true }).IsValid());
        Assert.False((s with { InternalFit = false, PrintBrim = true }).IsValid());
    }

    [Theory]
    [InlineData(80, 30, 0.3, false, 1218000)]
    [InlineData(80, 30, 0.3, true, 1221360)]
    [InlineData(40, 16, 1, false, 28416)]
    [InlineData(40, 16, 1, true, 28928)]
    [InlineData(200, 130, 0.6, false, 1699856)]
    [InlineData(200, 130, 0.6, true, 1704048)]
    public void Internal_Joint_Triangle_Budget_Matches_The_Browser(double diameter, double opening, double step, bool brim, int expected)
    {
        var s = Settings(diameter, opening) with { ResolutionMm = step, InternalFit = true, PrintBrim = brim, PrintAnchor = false };
        Assert.Equal(expected, new LithophaneGrid(s).TriangleCount);
        Assert.True(s.IsValid());
    }

    private static List<Vector3> CheckMesh(MemoryStream stream)
    {
        stream.Position = 80;
        using (var header = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            Assert.Equal(84L + 50L * header.ReadUInt32(), stream.Length);
        var edges = new Dictionary<(Vector3, Vector3), (int Count, int Balance)>();
        var vertices = new List<Vector3>();
        double volume = 0;
        foreach (var (a, b, c, normal) in Read(stream))
        {
            Assert.True(float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z));
            Assert.True(Vector3.Cross(b - a, c - a).LengthSquared() > 1e-12);
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6d;
            vertices.AddRange([a, b, c]);
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        Assert.All(edges.Values, e => { Assert.Equal(2, e.Count); Assert.Equal(0, e.Balance); });
        Assert.True(volume > 0);
        return vertices;
        void Edge(Vector3 a, Vector3 b)
        {
            var cmp = a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
            var key = cmp < 0 ? (a, b) : (b, a);
            edges.TryGetValue(key, out var e); edges[key] = (e.Count + 1, e.Balance + (cmp < 0 ? 1 : -1));
        }
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Read(MemoryStream stream)
    {
        stream.Position = 80;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var count = reader.ReadUInt32();
        // Equator tests intentionally contain one strip after a whole-file header.
        count = (uint)((stream.Length - 84) / 50);
        var result = new List<(Vector3, Vector3, Vector3, Vector3)>();
        for (var i = 0; i < count; i++)
        {
            var n = Vertex(); var a = Vertex(); var b = Vertex(); var c = Vertex(); reader.ReadUInt16(); result.Add((a, b, c, n));
        }
        return result;
        Vector3 Vertex() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}

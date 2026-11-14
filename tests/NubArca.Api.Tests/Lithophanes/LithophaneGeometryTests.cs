using System.Numerics;
using NubArca.Api.Lithophanes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Lithophanes;

public sealed class LithophaneGeometryTests
{
    private static readonly LithophaneSettings Coarse = new() { DiameterMm = 40, OpeningMm = 10, ResolutionMm = 1 };

    [Theory]
    [InlineData(40, 10, 1, null)]
    [InlineData(80, 20, 0.6, null)]
    [InlineData(40, 10, 1, 2.0)]
    [InlineData(40, 26, 0.5, 2.0)]
    [InlineData(80, 20, 0.6, 0.4)]
    public void Stl_Is_Closed_Oriented_And_Has_Exact_External_Diameter(double diameter, double opening, double resolution, double? background)
    {
        var s = Coarse with { DiameterMm = diameter, OpeningMm = opening, ResolutionMm = resolution,
            BackgroundThicknessMm = background, PictureWidthDegrees = 90, PictureHeightDegrees = 60 };
        var grid = new LithophaneGrid(s);
        using var photo = new Image<Rgba32>(9, 7, new Rgba32(70, 80, 90));
        using var map = LithophaneImage.CreateMap(photo, s);
        using var file = new MemoryStream();
        LithophaneMesh.WriteHeader(file, grid); LithophaneMesh.WriteStrip(file, s, map, 0, grid.Rows);
        Assert.Equal(grid.StlBytes, file.Length);
        file.Position = 80;
        using var reader = new BinaryReader(file);
        var count = reader.ReadUInt32();
        Assert.Equal(grid.TriangleCount, (int)count);
        var edges = new Dictionary<(Vector3, Vector3), (int Count, int Balance)>();
        var bottomRadii = new List<double>();
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        double volume = 0;
        for (var i = 0; i < count; i++)
        {
            var normal = Read(); var a = Read(); var b = Read(); var c = Read(); reader.ReadUInt16();
            var cross = Vector3.Cross(b - a, c - a);
            Assert.True(cross.LengthSquared() > 1e-12, "Degenerate triangle");
            Assert.True(Vector3.Dot(Vector3.Normalize(cross), normal) > 0.999);
            foreach (var v in new[] { a, b, c })
            {
                // The outside is a perfect sphere whatever the walls do inside.
                Assert.True(v.Length() <= grid.Radius + 1e-4);
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
                if (Math.Abs(v.Z - (float)Math.Cos(grid.BottomAngle) * (float)grid.Radius) < 1e-4)
                    bottomRadii.Add(Math.Sqrt(v.X * v.X + v.Y * v.Y));
            }
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6d;
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        Assert.All(edges.Values, edge => { Assert.Equal(2, edge.Count); Assert.Equal(0, edge.Balance); });
        Assert.Equal(diameter, max.X - min.X, 4); Assert.Equal(diameter, max.Y - min.Y, 4);
        Assert.Equal(diameter / 2, max.Z, 4);
        Assert.NotEmpty(bottomRadii);
        Assert.Equal(opening / 2, bottomRadii.Min(), 3); // clear aperture and planar printable rim
        Assert.True(volume > 0);
        // Hollow shell, never the filled ball.
        Assert.True(volume < 4d / 3 * Math.PI * Math.Pow(diameter / 2, 3) * 0.5);

        Vector3 Read() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        void Edge(Vector3 a, Vector3 b)
        {
            var order = Compare(a, b);
            var key = order < 0 ? (a, b) : (b, a);
            var value = edges.GetValueOrDefault(key);
            edges[key] = (value.Count + 1, value.Balance + (order < 0 ? 1 : -1));
        }
    }

    private static int Compare(Vector3 a, Vector3 b)
    {
        var x = a.X.CompareTo(b.X); if (x != 0) return x;
        var y = a.Y.CompareTo(b.Y); return y != 0 ? y : a.Z.CompareTo(b.Z);
    }

    [Fact]
    public void Arbitrary_Strip_Boundaries_Produce_Byte_Identical_Stl()
    {
        var g = new LithophaneGrid(Coarse);
        using var photo = new Image<Rgba32>(23, 17);
        for (var y = 0; y < photo.Height; y++) for (var x = 0; x < photo.Width; x++)
            photo[x, y] = new Rgba32((byte)(x * 11), (byte)(y * 13), 50);
        using var map = LithophaneImage.CreateMap(photo, Coarse);
        using var whole = new MemoryStream(); using var sliced = new MemoryStream();
        LithophaneMesh.WriteHeader(whole, g); LithophaneMesh.WriteHeader(sliced, g);
        LithophaneMesh.WriteStrip(whole, Coarse, map, 0, g.Rows);
        for (var row = 0; row < g.Rows; row += 7)
            LithophaneMesh.WriteStrip(sliced, Coarse, map, row, Math.Min(row + 7, g.Rows));
        Assert.Equal(whole.ToArray(), sliced.ToArray());
    }

    [Theory]
    [InlineData(0, 2.8)]
    [InlineData(255, 0.8)]
    public void Luminance_Controls_Thickness_And_Poles_Stay_Uniform(byte value, double thickness)
    {
        var grid = new LithophaneGrid(Coarse);
        using var photo = new Image<Rgba32>(12, 8, new Rgba32(value, value, value));
        using var map = LithophaneImage.CreateMap(photo, Coarse);
        Assert.Equal(value, map[grid.Columns / 2, grid.TopRows].L);
        Assert.Equal(thickness, Coarse.Thickness(map[grid.Columns / 2, grid.TopRows]), 8);
        for (var col = 0; col < grid.Columns; col++)
        {
            Assert.Equal(new La16(255, 0), map[col, 0]);
            Assert.Equal(new La16(255, 0), map[col, grid.Rows]);
        }
    }

    [Fact]
    public void Framing_Uses_The_Existing_Centre_And_Zoom_Policy()
    {
        using var photo = new Image<Rgba32>(120, 60, new Rgba32(0, 0, 0));
        for (var y = 0; y < 60; y++) for (var x = 60; x < 120; x++) photo[x, y] = new Rgba32(255, 255, 255);
        var s = Coarse with { Zoom = 4, CenterX = 0 };
        var grid = new LithophaneGrid(s);
        using var left = LithophaneImage.CreateMap(photo, s);
        using var right = LithophaneImage.CreateMap(photo, s with { CenterX = 1 });
        Assert.Equal(0, left[grid.Columns / 2, grid.TopRows].L);
        Assert.Equal(255, right[grid.Columns / 2, grid.TopRows].L);
    }

    [Fact]
    public void Transparent_Pixels_Are_White_And_Tonal_Correction_Is_Deterministic()
    {
        using var photo = new Image<Rgba32>(2, 2, new Rgba32(0, 0, 0, 0));
        using var transparent = LithophaneImage.CreateMap(photo, Coarse);
        var g = new LithophaneGrid(Coarse);
        Assert.Equal(255, transparent[g.Columns / 2, g.TopRows].L);
        using var grey = new Image<Rgba32>(2, 2, new Rgba32(64, 64, 64));
        using var corrected = LithophaneImage.CreateMap(grey, Coarse with { Gamma = 2 });
        Assert.InRange(corrected[g.Columns / 2, g.TopRows].L, (byte)127, (byte)129);
    }

    // Same table as lithophaneModel.test.ts: the browser form must accept and
    // reject exactly what the server does, including next to the budget.
    [Theory]
    [InlineData(80, 20, 0.8, 0.3, 1266720)]
    [InlineData(95, 20, 0.8, 0.3, 1804752)]
    [InlineData(96, 20, 0.8, 0.3, 1850688)]
    [InlineData(100, 30, 1.2, 0.3, 1932512)]
    [InlineData(120, 40, 0.8, 0.35, 2052000)]
    [InlineData(120, 40, 0.8, 0.4, 1567040)]
    [InlineData(150, 20, 0.8, 0.45, 2058272)]
    [InlineData(150, 20, 0.8, 0.5, 1668992)]
    [InlineData(200, 130, 2, 0.6, 1676800)]
    [InlineData(200, 4, 0.4, 0.65, 1819840)]
    [InlineData(40, 4, 0.4, 0.15, 1310400)]
    [InlineData(60, 39, 0.6, 0.2, 1359360)]
    public void Triangle_Count_Matches_The_Browser_Form(double diameter, double opening, double minWall, double step, int triangles)
    {
        var s = new LithophaneSettings
        {
            DiameterMm = diameter, OpeningMm = opening, MinThicknessMm = minWall,
            MaxThicknessMm = minWall + 2, ResolutionMm = step,
        };
        Assert.Equal(triangles, new LithophaneGrid(s).TriangleCount);
        Assert.Equal(triangles <= LithophaneSettings.MaxTriangles, s.IsValid());
    }

    [Fact]
    public void Bounds_Reject_NonFinite_Impossible_And_Excessively_Detailed_Models()
    {
        Assert.True(new LithophaneSettings().IsValid());
        Assert.False((Coarse with { DiameterMm = double.NaN }).IsValid());
        Assert.False((Coarse with { MaxThicknessMm = 0.5 }).IsValid());
        Assert.False((Coarse with { OpeningMm = 39 }).IsValid());
        Assert.False((Coarse with { DiameterMm = 200, ResolutionMm = 0.15 }).IsValid());
        Assert.False((Coarse with { Gamma = double.PositiveInfinity }).IsValid());
        using var map = new Image<La16>(new LithophaneGrid(Coarse).Columns, new LithophaneGrid(Coarse).Rows + 1);
        Assert.Throws<OperationCanceledException>(() => LithophaneMesh.WriteStrip(Stream.Null, Coarse, map, 0, 1, new CancellationToken(true)));
    }

    // A plain photo in a small window: background everywhere else.
    private static readonly LithophaneSettings Window = Coarse with
    {
        PictureWidthDegrees = 90, PictureHeightDegrees = 60, BackgroundThicknessMm = 2, ResolutionMm = 0.5,
    };

    [Fact]
    public void White_Photo_Pixels_Use_The_Thinnest_Wall_And_The_Background_Its_Own()
    {
        var grid = new LithophaneGrid(Window);
        using var photo = new Image<Rgba32>(12, 8, new Rgba32(255, 255, 255));
        using var map = LithophaneImage.CreateMap(photo, Window);
        var centre = map[grid.Columns / 2, grid.TopRows];
        Assert.Equal(new La16(255, 255), centre);
        Assert.Equal(0.8, Window.Thickness(centre), 8);
        Assert.Equal(2.0, Window.Thickness(map[0, grid.TopRows]), 8); // behind the sphere
        Assert.Equal(2.0, Window.Thickness(map[grid.Columns / 2, 1]), 8); // near the north pole
    }

    [Fact]
    public void Photo_Edge_Blends_Into_The_Background_Without_A_Step()
    {
        var grid = new LithophaneGrid(Window);
        using var photo = new Image<Rgba32>(12, 8, new Rgba32(0, 0, 0));
        using var map = LithophaneImage.CreateMap(photo, Window);
        // Along the equator from the photo's centre to the back of the sphere.
        var walls = Enumerable.Range(0, grid.Columns / 2 + 1)
            .Select(i => Window.Thickness(map[grid.Columns / 2 + i == grid.Columns ? 0 : grid.Columns / 2 + i, grid.TopRows])).ToList();
        Assert.Equal(2.8, walls[0], 8);
        Assert.Equal(2.0, walls[^1], 8);
        var ramp = walls.Where(w => w < 2.8 - 1e-9 && w > 2.0 + 1e-9).Count();
        Assert.InRange(ramp, 1, (int)Math.Ceiling(LithophaneSettings.BlendMm / Window.ResolutionMm) + 1);
        var largestStep = walls.Zip(walls.Skip(1), (a, b) => Math.Abs(a - b)).Max();
        Assert.True(largestStep < 0.8 * 0.75, $"Step {largestStep} mm between neighbouring vertices.");
    }

    [Fact]
    public void Background_As_Thin_As_The_Photo_Matches_Projects_Saved_Before_It_Existed()
    {
        var old = Coarse with { PictureWidthDegrees = 90, PictureHeightDegrees = 60 };
        var explicitThin = old with { BackgroundThicknessMm = old.MinThicknessMm };
        using var photo = new Image<Rgba32>(23, 17);
        for (var y = 0; y < photo.Height; y++) for (var x = 0; x < photo.Width; x++)
            photo[x, y] = new Rgba32((byte)(x * 11), (byte)(y * 13), 50);
        using var map = LithophaneImage.CreateMap(photo, old);
        var g = new LithophaneGrid(old);
        using var a = new MemoryStream(); using var b = new MemoryStream();
        LithophaneMesh.WriteStrip(a, old, map, 0, g.Rows); LithophaneMesh.WriteStrip(b, explicitThin, map, 0, g.Rows);
        Assert.Equal(a.ToArray(), b.ToArray());
        // A map saved as plain grey before coverage existed is read fully covered:
        // every wall is exactly the old luminance thickness, whatever the background.
        using var grey = new Image<L8>(g.Columns, g.Rows + 1, new L8(37));
        using var png = new MemoryStream(); grey.SaveAsPng(png); png.Position = 0;
        using var loaded = Image.Load<La16>(png);
        Assert.Equal(old.Thickness((byte)37), (old with { BackgroundThicknessMm = 2 }).Thickness(loaded[3, 4]), 12);
        // And the saved map keeps its coverage through PNG.
        using var saved = new MemoryStream(); map.SaveAsPng(saved); saved.Position = 0;
        using var reread = Image.Load<La16>(saved);
        for (var y = 0; y < map.Height; y++) for (var x = 0; x < map.Width; x++) Assert.Equal(map[x, y], reread[x, y]);
    }

    [Fact]
    public void Background_Thickness_Is_Bounded()
    {
        Assert.True((Coarse with { BackgroundThicknessMm = 0.4 }).IsValid());
        Assert.True((Coarse with { BackgroundThicknessMm = 2 }).IsValid());
        Assert.False((Coarse with { BackgroundThicknessMm = 0.3 }).IsValid());
        Assert.False((Coarse with { BackgroundThicknessMm = 2.1 }).IsValid());
        Assert.False((Coarse with { BackgroundThicknessMm = double.NaN }).IsValid());
    }
}

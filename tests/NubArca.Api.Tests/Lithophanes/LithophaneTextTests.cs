using System.Text.Json;
using NubArca.Api.Lithophanes;
using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Lithophanes;

public sealed class LithophaneTextTests
{
    private static readonly LithophaneSettings Base = new()
    {
        DiameterMm = 80, OpeningMm = 20, ResolutionMm = 0.6, PictureWidthDegrees = 120, PictureHeightDegrees = 60,
        BackgroundThicknessMm = 0.8,
    };

    private static Image<La16> Map(LithophaneSettings s)
    {
        using var photo = new Image<Rgba32>(30, 15, new Rgba32(255, 255, 255));
        return LithophaneImage.CreateMap(photo, s);
    }

    // Rows (and columns) where the wall reaches the letters' thickness.
    private static List<(int Col, int Row)> Inked(Image<La16> map, LithophaneSettings s)
    {
        var result = new List<(int, int)>();
        for (var row = 0; row < map.Height; row++)
            for (var col = 0; col < map.Width; col++)
                if (s.Thickness(map[col, row]) > s.MaxThicknessMm - 0.2) result.Add((col, row));
        return result;
    }

    private static (double Top, double Bottom) PhotoRows(LithophaneGrid grid, LithophaneSettings s)
    {
        // A white photo on a thin background: rows whose centre column is covered.
        double top = double.MaxValue, bottom = double.MinValue;
        for (var row = 1; row < grid.Rows; row++)
        {
            var degrees = grid.Theta(row) * 180 / Math.PI;
            if (degrees >= 90 - s.PictureHeightDegrees / 2 && degrees <= 90 + s.PictureHeightDegrees / 2)
            { top = Math.Min(top, row); bottom = Math.Max(bottom, row); }
        }
        return (top, bottom);
    }

    [Fact]
    public void Settings_Without_Text_Serialise_Exactly_As_Before()
    {
        var json = JsonSerializer.Serialize(Base);
        Assert.DoesNotContain("Text", json);
        using var a = Map(Base);
        using var b = Map(Base with { Text = "   " });
        for (var row = 0; row < a.Height; row++)
            for (var col = 0; col < a.Width; col++) Assert.Equal(a[col, row], b[col, row]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_coordinates_place_words_relative_to_sphere_centre(bool hanging)
    {
        var s = Base with { Text = "CIAO", TextHeightMm = 8, TextLatitudeDegrees = 25,
            TextLongitudeDegrees = 30, Hanging = hanging, ExternalRelief = true };
        Assert.True(s.IsValid());
        var grid = new LithophaneGrid(s);
        using var map = Map(s);
        var ink = Inked(map, s);
        Assert.NotEmpty(ink);
        var latitude = ink.Average(p => 90 - grid.DesignTheta(p.Row, hanging) * 180 / Math.PI);
        var longitude = ink.Average(p => grid.DesignLongitude(p.Col, hanging));
        Assert.InRange(latitude, 24, 26);
        Assert.InRange(longitude, 29, 31);
        using var wordsOnly = LithophaneImage.CreateMap(null, s);
        var wordsInk = Inked(wordsOnly, s);
        Assert.Equal(ink, wordsInk);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(61, 0)]
    [InlineData(0, -181)]
    public void Invalid_text_coordinates_are_refused(double latitude, double longitude) =>
        Assert.False((Base with { Text = "CIAO", TextLatitudeDegrees = latitude, TextLongitudeDegrees = longitude }).IsValid());

    [Theory]
    [InlineData("below")]
    [InlineData("above")]
    [InlineData("onPhoto")]
    public void Words_Are_Thick_Wall_Where_They_Were_Asked_For(string position)
    {
        var s = Base with { Text = "CIAO", TextHeightMm = 8, TextPosition = position };
        Assert.True(s.IsValid());
        var grid = new LithophaneGrid(s);
        using var map = Map(s);
        var inked = Inked(map, s);
        Assert.NotEmpty(inked);
        var (photoTop, photoBottom) = PhotoRows(grid, s);
        switch (position)
        {
            case "below": Assert.All(inked, p => Assert.True(p.Row > photoBottom)); break;
            case "above": Assert.All(inked, p => Assert.True(p.Row < photoTop)); break;
            default: Assert.All(inked, p => Assert.True(p.Row >= photoTop && p.Row <= photoBottom)); break;
        }
        // Centred under the photograph, which faces longitude 0.
        var centre = inked.Average(p => p.Col);
        Assert.Equal(grid.Columns / 2d, centre, grid.Columns * 0.02);
        // The block is about as tall as asked on the surface (capitals only).
        var rows = inked.Select(p => grid.Theta(p.Row) * grid.Radius).ToList();
        Assert.InRange(rows.Max() - rows.Min(), 8 * 0.8, 8 * 1.25);
    }

    [Fact]
    public void Words_Only_Sit_On_The_Equator_Of_A_Plain_Wall()
    {
        var s = Base with { Text = "CIAO", TextPosition = "below" };
        var grid = new LithophaneGrid(s);
        using var map = LithophaneImage.CreateMap(null, s);
        var inked = Inked(map, s);
        Assert.NotEmpty(inked);
        // Centred on the middle of the picture area whatever the position says.
        var middle = inked.Average(p => grid.Theta(p.Row) * 180 / Math.PI);
        Assert.InRange(middle, 89, 91);
        // Everywhere else the wall is the background's.
        for (var row = 1; row < grid.Rows; row++)
            for (var col = 0; col < grid.Columns; col += 7)
                if (!inked.Contains((col, row)) && map[col, row].A == 0)
                    Assert.Equal(s.BackgroundWallMm, s.Thickness(map[col, row]), 6);
    }

    [Fact]
    public void Two_Lines_Are_Taller_Than_One()
    {
        var one = Base with { Text = "CIAO" };
        var two = Base with { Text = "CIAO\nMAMMA" };
        Assert.True(two.IsValid());
        using var a = Map(one); using var b = Map(two);
        int Span(Image<La16> m, LithophaneSettings s) { var r = Inked(m, s).Select(p => p.Row).ToList(); return r.Max() - r.Min(); }
        Assert.True(Span(b, two) > Span(a, one) * 1.8);
    }

    [Fact]
    public void Every_Face_Of_The_Catalogue_Draws_Letters()
    {
        foreach (var font in TextFonts.All)
        {
            using var mask = LithophaneText.Render("Ab", font.Id, 40);
            var lit = 0;
            mask.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height; y++)
                    foreach (var p in rows.GetRowSpan(y)) if (p.PackedValue > 128) lit++;
            });
            Assert.True(lit > 100, font.Id);
            Assert.InRange(mask.Height, 40, 160);
        }
    }

    [Fact]
    public void Ink_Mixes_Toward_The_Thickest_Wall()
    {
        var s = Base with { MinThicknessMm = 0.8, MaxThicknessMm = 2.8, BackgroundThicknessMm = 1.2 };
        // Plain background, fully inked: the photo's thickest wall.
        Assert.Equal(2.8, s.Thickness(LithophaneText.Ink(new La16(255, 0), 1, s)), 2);
        // Plain background, half inked: halfway between background and thickest.
        Assert.Equal((1.2 + 2.8) / 2, s.Thickness(LithophaneText.Ink(new La16(255, 0), 0.5, s)), 1);
        // White photo, half inked: halfway between thinnest and thickest.
        Assert.Equal(1.8, s.Thickness(LithophaneText.Ink(new La16(255, 255), 0.5, s)), 1);
        // No ink: unchanged.
        Assert.Equal(new La16(10, 40), LithophaneText.Ink(new La16(10, 40), 0, s));
    }

    [Theory]
    [InlineData("ok", "comic", null, null)]
    [InlineData("ok", null, 2.0, null)]
    [InlineData("ok", null, 31.0, null)]
    [InlineData("ok", null, null, "left")]
    [InlineData("uno\ndue\ntre", null, null, null)]
    [InlineData("tab\there", null, null, null)]
    [InlineData("emoji 😀", null, null, null)]
    public void Invalid_Text_Is_Refused(string text, string? font, double? height, string? position)
    {
        Assert.False((Base with { Text = text, TextFont = font, TextHeightMm = height, TextPosition = position }).IsValid());
    }

    [Fact]
    public void Text_Longer_Than_The_Limit_Is_Refused()
    {
        Assert.True((Base with { Text = new string('a', LithophaneSettings.TextMaxLength) }).IsValid());
        Assert.False((Base with { Text = new string('a', LithophaneSettings.TextMaxLength + 1) }).IsValid());
    }
}

using System.Text.Json;
using NubArca.Api.Print;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// THE CATALOGUE against the shared table — the same file the browser's
/// catalogue (packages/contracts/src/printLayouts.ts) is held to, produced by
/// a third, independent implementation. A sheet the server draws and the
/// preview a person composed against are the same numbers, or this fails.
/// </summary>
public sealed class PrintLayoutsTests
{
    private static JsonElement Table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NubArca.sln"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "packages", "contracts", "src", "printLayouts.cases.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static void Close(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) < 1e-12, $"{what}: {actual} != {expected}");

    private static void SameRects(
        JsonElement expected, IReadOnlyList<(double X, double Y, double Width, double Height)> actual, string what)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            var e = expected[i];
            Close(e.GetProperty("x").GetDouble(), actual[i].X, $"{what}[{i}].x");
            Close(e.GetProperty("y").GetDouble(), actual[i].Y, $"{what}[{i}].y");
            Close(e.GetProperty("width").GetDouble(), actual[i].Width, $"{what}[{i}].width");
            Close(e.GetProperty("height").GetDouble(), actual[i].Height, $"{what}[{i}].height");
        }
    }

    [Fact]
    public void Every_Sheet_Of_The_Shared_Table_Exactly()
    {
        var cases = Table().GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 20);
        foreach (var c in cases)
        {
            var layout = c.GetProperty("layout").GetString()!;
            var style = c.GetProperty("style").GetString()!;
            var paper = c.GetProperty("paper").GetString()!;
            var portrait = c.GetProperty("portrait").GetBoolean();
            var id = $"{layout}/{style}/{paper}/{(portrait ? "portrait" : "landscape")}";

            var sheet = PrintLayouts.Arrange(layout, style, paper, portrait);
            Assert.Equal(c.GetProperty("sheet").GetProperty("width").GetInt32(), sheet.Width);
            Assert.Equal(c.GetProperty("sheet").GetProperty("height").GetInt32(), sheet.Height);
            SameRects(c.GetProperty("slots"), sheet.Slots, $"{id} slots");
            SameRects(c.GetProperty("bands"), sheet.Bands, $"{id} bands");
            Close(c.GetProperty("slotAspect").GetDouble(),
                PrintLayouts.SlotAspect(layout, style, paper, portrait), $"{id} slotAspect");
        }
    }

    [Fact]
    public void Which_Paper_Makes_What_How_Many_Photographs_And_Which_Style()
    {
        var table = Table();
        foreach (var row in table.GetProperty("allowed").EnumerateArray())
        {
            Assert.Equal(row.GetProperty("allowed").GetBoolean(),
                PrintLayouts.Allowed(row.GetProperty("paper").GetString()!, row.GetProperty("layout").GetString()!));
        }
        foreach (var row in table.GetProperty("styles").EnumerateArray())
        {
            Assert.Equal(row.GetProperty("supported").GetBoolean(),
                PrintLayouts.SupportsStyle(row.GetProperty("layout").GetString()!, row.GetProperty("style").GetString()!));
        }
        foreach (var layout in table.GetProperty("photoCounts").EnumerateObject())
        {
            Assert.Equal(layout.Value.EnumerateArray().Select(n => n.GetInt32()).ToList(),
                PrintLayouts.PhotoCounts(layout.Name));
        }
        Assert.Equal(PrintLayouts.All, table.GetProperty("photoCounts").EnumerateObject().Select(p => p.Name).ToList());
    }
}

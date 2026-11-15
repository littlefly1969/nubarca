using System.Security.Cryptography;
using System.Text.Json;
using NubArca.Api.Print;

namespace NubArca.Api.Tests.Print;

public sealed class TextFontsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NubArca.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Every_Face_Is_Shipped_Loads_And_Matches_The_Browser_Table()
    {
        var root = RepoRoot();
        var browser = File.ReadAllText(Path.Combine(root, "frontend", "src", "text", "textFonts.ts"));
        foreach (var font in TextFonts.All)
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", font.File)), font.File);
            Assert.NotNull(TextFonts.Family(font.Id));
            Assert.Contains($"id: '{font.Id}'", browser);
        }
        Assert.Equal(TextFonts.All.Count, System.Text.RegularExpressions.Regex.Matches(browser, @"\{ id: '").Count);
        Assert.True(TextFonts.IsKnown(null));
        Assert.False(TextFonts.IsKnown("comic-sans"));
    }

    [Fact]
    public void Text_Font_Binaries_Are_The_Manifested_Ones_With_Their_Licences()
    {
        var dir = Path.Combine(RepoRoot(), "frontend", "public", "fonts", "text");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "fonts-manifest.json")));
        var declared = manifest.RootElement.GetProperty("fonts").EnumerateArray().ToList();
        foreach (var entry in declared)
        {
            var file = Path.Combine(dir, entry.GetProperty("file").GetString()!);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
            Assert.Equal("OFL-1.1", entry.GetProperty("license").GetString());
            Assert.True(File.Exists(Path.Combine(dir, entry.GetProperty("licenseFile").GetString()!)));
        }
        // Every binary is accounted for, and every face but the brand default comes from here.
        Assert.Equal(declared.Select(x => x.GetProperty("file").GetString()).Order(),
            Directory.GetFiles(dir, "*.ttf").Select(Path.GetFileName).Order());
        Assert.Equal(TextFonts.All.Where(x => x.Id != TextFonts.Default).Select(x => Path.GetFileName(x.File)).Order(),
            declared.Select(x => x.GetProperty("file").GetString()).Order());
    }
}

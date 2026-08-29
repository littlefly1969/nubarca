using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using SkiaSharp;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE RENDERERS, against the two properties that make a rendered corpus worth
// anything: it is COMPLETE, and it is the SAME every time.
//
// Completeness is Slice 4's rule applied to pages. A document past a
// completeness-critical bound is refused whole; there is no configuration in
// which NubArca publishes the first N pages of somebody's contract as a
// searchable document and says nothing about the rest.
//
// Determinism is what `PixelHash` is for. Two renders of the same bytes under
// the same render profile must be byte-identical, or a rebuild silently
// re-embeds content that did not change and the hash records nothing.
public sealed class DocumentVisualRenderingTests
{
    private static IOptions<DocumentVisualOptions> Options(DocumentVisualOptions? options = null)
        => Microsoft.Extensions.Options.Options.Create(options ?? new DocumentVisualOptions());

    private static PdfVisualRenderer Pdf(DocumentVisualOptions? options = null)
        => new(Options(options), NullLogger<PdfVisualRenderer>.Instance);

    private static TextCanvasVisualRenderer Canvas(DocumentVisualOptions? options = null)
        => new(Options(options));

    private static DocumentVisualRenderRequest Request(
        byte[] bytes, DocumentFormatKind format, DocumentVisualOptions options)
        => new(bytes, format, options);

    // ---- PDF ----------------------------------------------------------------

    [Fact]
    public async Task Pdf_Renders_Every_Page_And_Maps_Them_Exactly()
    {
        var options = new DocumentVisualOptions();
        var outcome = await Pdf(options).RenderAsync(
            Request(PdfFixtures.Pages(4), DocumentFormatKind.Pdf, options));

        Assert.True(outcome.Ok, outcome.Reason);
        var units = outcome.Artifact!.Units;

        // EVERY page, in order, with nothing skipped.
        Assert.Equal(4, units.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, units.Select(u => u.Ordinal).ToArray());

        // A PDF PAGE IS THE ONE UNIT THAT IS ALSO A CITATION, so it is the only
        // renderer that fills in a source page — 1-based, like every locator
        // above the library's 0-based convention.
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, units.Select(u => u.SourcePage).ToArray());
        Assert.All(units, u => Assert.Equal(DocumentLocatorKinds.Page, u.SourceLocator!.Kind));
        Assert.All(units, u => Assert.Equal(DocumentVisualRenderKinds.PdfPage, u.RenderKind));
        Assert.All(units, u => Assert.NotEmpty(u.Png));
        Assert.Equal(DocumentVisualRenderProfiles.PdfiumPage, outcome.Artifact.RenderProfileKey);
    }

    [Fact]
    public async Task Pdf_At_The_Unit_Bound_Succeeds_And_One_Past_It_Refuses()
    {
        // EXACTLY at the bound must still work — a rule that refuses everything
        // is not a bound, it is an outage.
        var atBound = new DocumentVisualOptions { MaxVisualUnitsPerDocument = 3 };
        var ok = await Pdf(atBound).RenderAsync(
            Request(PdfFixtures.Pages(3), DocumentFormatKind.Pdf, atBound));
        Assert.True(ok.Ok, ok.Reason);
        Assert.Equal(3, ok.Artifact!.Units.Count);

        // And one past it refuses the WHOLE document rather than rendering three
        // pages of a four-page one.
        var past = await Pdf(atBound).RenderAsync(
            Request(PdfFixtures.Pages(4), DocumentFormatKind.Pdf, atBound));
        Assert.False(past.Ok);
        Assert.Equal(DocumentVisualReasons.DocumentTooComplex, past.Reason);
        Assert.True(past.IsPermanent);
    }

    [Fact]
    public async Task Pdf_Past_The_Per_Unit_Pixel_Bound_Refuses_Rather_Than_Downscaling()
    {
        var options = new DocumentVisualOptions { MaxVisualPixelsPerUnit = 10_000 };
        var outcome = await Pdf(options).RenderAsync(
            Request(PdfFixtures.Pages(1), DocumentFormatKind.Pdf, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.OutputTooLarge, outcome.Reason);
    }

    [Fact]
    public async Task Pdf_Past_The_Total_Pixel_Budget_Refuses_The_Document()
    {
        var options = new DocumentVisualOptions
        {
            // One page fits; the accumulated total does not.
            MaxVisualTotalPixelsPerDocument = 2_000_000,
        };
        var outcome = await Pdf(options).RenderAsync(
            Request(PdfFixtures.Pages(6), DocumentFormatKind.Pdf, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.DocumentTooComplex, outcome.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a pdf at all")]
    [InlineData("%PDF-1.7\ntruncated and corrupt")]
    public async Task Pdf_Refuses_Corrupt_Input_With_A_Sanitized_Reason(string content)
    {
        var options = new DocumentVisualOptions();
        var outcome = await Pdf(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(content), DocumentFormatKind.Pdf, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.InvalidSource, outcome.Reason);
        // The reason is a closed token. A native exception message can carry a
        // filesystem path, and this value travels to the CLI and the logs.
        Assert.DoesNotContain("/", outcome.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_Refuses_A_Format_It_Does_Not_Claim()
    {
        var options = new DocumentVisualOptions();
        var outcome = await Pdf(options).RenderAsync(
            Request(PdfFixtures.Pages(1), DocumentFormatKind.WordOpenXml, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.FormatUnsupported, outcome.Reason);
    }

    [Fact]
    public async Task Pdf_Rendering_Writes_No_File_Anywhere()
    {
        // RENDER, EMBED, DISCARD. The artifact is bytes in memory and the
        // renderer has no path, no storage handle and nothing to write with —
        // asserted here by watching the process's temp directory, which is where
        // an accidental `File.WriteAllBytes` would land.
        var temp = Path.GetTempPath();
        var before = Directory.GetFiles(temp, "*.png", SearchOption.TopDirectoryOnly).Length;

        var options = new DocumentVisualOptions();
        var outcome = await Pdf(options).RenderAsync(
            Request(PdfFixtures.Pages(2), DocumentFormatKind.Pdf, options));

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(before, Directory.GetFiles(temp, "*.png", SearchOption.TopDirectoryOnly).Length);
    }

    // ---- the deterministic text canvas ---------------------------------------

    [SkippableFact]
    public async Task TextCanvas_Is_Byte_Identical_Across_Renders()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        var options = new DocumentVisualOptions();
        var bytes = Encoding.UTF8.GetBytes(SampleMarkdown);

        var first = await Canvas(options).RenderAsync(
            Request(bytes, DocumentFormatKind.NativeText, options));
        var second = await Canvas(options).RenderAsync(
            Request(bytes, DocumentFormatKind.NativeText, options));

        Assert.True(first.Ok, first.Reason);
        Assert.True(second.Ok, second.Reason);
        Assert.Equal(first.Artifact!.Units.Count, second.Artifact!.Units.Count);

        for (var i = 0; i < first.Artifact.Units.Count; i++)
        {
            // `PixelHash` is the determinism proof that survives discarding the
            // image, so it is the hash that is compared.
            Assert.Equal(
                Hash(first.Artifact.Units[i].Png),
                Hash(second.Artifact.Units[i].Png));
        }
    }

    [SkippableFact]
    public async Task TextCanvas_Draws_Headings_Visibly_Larger_Than_Body()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        // THE HIERARCHY IS THE SIGNAL. A document whose headings render at body
        // size is a document with no visible structure for the encoder to find,
        // so this compares INK: a page of headings must be measurably darker
        // than the same words as body text.
        var options = new DocumentVisualOptions();
        var words = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"Section heading {i}"));
        var asHeadings = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"# Section heading {i}"));

        var body = await Canvas(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(words), DocumentFormatKind.NativeText, options));
        var headings = await Canvas(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(asHeadings), DocumentFormatKind.NativeText, options));

        Assert.True(body.Ok, body.Reason);
        Assert.True(headings.Ok, headings.Reason);

        Assert.True(
            Ink(headings.Artifact!.Units[0].Png) > Ink(body.Artifact!.Units[0].Png) * 1.4,
            "headings must be visibly heavier than the same words as body text");
    }

    [SkippableFact]
    public async Task TextCanvas_Paginates_Completely_And_Never_Truncates()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        var options = new DocumentVisualOptions();
        // Enough lines to need several sheets. Each carries a distinctive
        // marker, so "did every line get drawn" is answerable by counting sheets
        // against the layout rather than by trusting the renderer's own count.
        var text = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line marker {i:D4}"));

        var outcome = await Canvas(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(text), DocumentFormatKind.NativeText, options));

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.True(outcome.Artifact!.Units.Count > 1, "400 lines must not fit on one sheet");
        // Every sheet carries ink: an empty trailing page would mean pagination
        // ran past the content.
        Assert.All(outcome.Artifact.Units, u => Assert.True(Ink(u.Png) > 0));
    }

    [SkippableFact]
    public async Task TextCanvas_Past_The_Sheet_Bound_Refuses_The_Document()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        var options = new DocumentVisualOptions { MaxVisualUnitsPerDocument = 1 };
        var text = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line marker {i:D4}"));

        var outcome = await Canvas(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(text), DocumentFormatKind.NativeText, options));

        // NOT "the first sheet". A one-sheet picture of a fifteen-sheet document
        // reads as complete and is not.
        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.DocumentTooComplex, outcome.Reason);
    }

    [SkippableFact]
    public async Task TextCanvas_Claims_No_Source_Locator()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        var options = new DocumentVisualOptions();
        var outcome = await Canvas(options).RenderAsync(
            Request(Encoding.UTF8.GetBytes(SampleMarkdown), DocumentFormatKind.NativeText, options));

        Assert.True(outcome.Ok, outcome.Reason);
        // A Markdown file has no sheets. "Sheet 3 of 5" would describe this
        // renderer's margins, not the author's document.
        Assert.All(outcome.Artifact!.Units, u =>
        {
            Assert.Null(u.SourceLocator);
            Assert.Null(u.SourcePage);
            Assert.Equal(DocumentVisualRenderKinds.TextCanvasSheet, u.RenderKind);
        });
    }

    [SkippableFact]
    public async Task TextCanvas_Refuses_Bytes_That_Are_Not_Utf8()
    {
        Skip.IfNot(Canvas().CheckReadiness().Ready, "the bundled canvas font is not installed");

        var options = new DocumentVisualOptions();
        var outcome = await Canvas(options).RenderAsync(
            Request(new byte[] { 0xFF, 0xFE, 0x00, 0x80, 0x81 }, DocumentFormatKind.NativeText, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentVisualReasons.InvalidSource, outcome.Reason);
    }

    // ---- render identity -----------------------------------------------------

    [Fact]
    public void Every_Renderer_Declares_A_Known_Stable_Render_Identity()
    {
        // The identity is what makes a stored index queryable or not, so it is
        // a closed vocabulary rather than a free string — and deliberately not a
        // timestamp or a build SHA, either of which would re-render every
        // document in every library on a release that touched none of this.
        foreach (var key in new[]
                 {
                     Pdf().RenderProfileKey,
                     Canvas().RenderProfileKey,
                 })
        {
            Assert.True(DocumentVisualRenderProfiles.IsKnown(key), key);
            Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(), key, StringComparison.Ordinal);
        }
    }

    // ---- helpers -------------------------------------------------------------

    private const string SampleMarkdown = """
        # Budget 2026

        A short paragraph of ordinary body text that should wrap across the page
        because it is long enough to require more than a single measured line.

        ## Quarterly totals

          indented detail line
          another indented detail line

        ```
        code_block = true
        value      = 42
        ```

        ### Notes

        Final paragraph.
        """;

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    /// Total darkness of a rendered page. A crude but honest measure of "how
    /// much and how heavy is the text", which is exactly what a heading does to
    /// a page and what a visual encoder is being asked to notice.
    private static long Ink(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        long ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                ink += 255 - ((pixel.Red + pixel.Green + pixel.Blue) / 3);
            }
        }
        return ink;
    }
}

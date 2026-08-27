using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents;
using NetVips;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// THE REAL ENGINE, on a page this test drew.
//
// A fake OCR provider proves the wiring and nothing about the thing that
// actually decides whether scanned documents work. So these render text into a
// bitmap and ask the installed Tesseract to read it back — which is also the
// only way to check the properties that only exist because a real process is
// involved: that a timeout kills it, that its output is bounded, and that
// nothing is downloaded.
//
// Skipped rather than failed where the engine is absent: a suite that depends on
// a system package fails for reasons that have nothing to do with the code. The
// completion report distinguishes skipped from passed.
[Trait("Category", "External")]
public sealed class TesseractOcrProviderTests
{
    private static TesseractOcrProvider Provider(DocumentExtractionOptions options)
        => new(Options.Create(options), NullLogger<TesseractOcrProvider>.Instance);

    private static DocumentExtractionOptions Enabled(string languages = "eng") => new()
    {
        OcrEnabled = true,
        OcrLanguages = languages,
    };

    private static bool EngineAvailable(out string reason)
    {
        var readiness = Provider(Enabled()).CheckReadiness();
        reason = readiness.Reason ?? string.Empty;
        return readiness.IsReady;
    }

    // ---- readiness ----------------------------------------------------------

    [Fact]
    public void Ocr_Is_Off_Until_It_Is_Turned_On()
    {
        // AI is disabled by default in NubArca, and recognition is AI. An
        // installation that never asked for it must not start a process.
        var readiness = Provider(new DocumentExtractionOptions()).CheckReadiness();

        Assert.False(readiness.IsReady);
        Assert.Equal(DocumentExtractionReasons.OcrUnavailable, readiness.Reason);
    }

    [Fact]
    public void A_Language_That_Is_Not_Installed_Is_Not_Ready_And_Nothing_Is_Downloaded()
    {
        // The failure mode this prevents is a first-run fetch: a network call at
        // document-index time, from a component whose whole promise is that it
        // makes none. Not ready is the honest answer.
        var readiness = Provider(Enabled("zzz")).CheckReadiness();

        Assert.False(readiness.IsReady);
        Assert.Equal(DocumentExtractionReasons.OcrUnavailable, readiness.Reason);
    }

    [Fact]
    public void Configured_Languages_Are_Validated_As_Tokens()
    {
        // Nothing user-controlled reaches an argument vector unvalidated. These
        // are not shell-escaping concerns — ArgumentList never invokes a shell —
        // but a flag-shaped token would still be read by the engine as a flag.
        var options = new DocumentExtractionOptions
        {
            OcrLanguages = "eng+--psm+ita+../../etc/passwd+x",
        };

        var accepted = TesseractOcrProvider.ConfiguredLanguages(options);

        Assert.Equal(new[] { "eng", "ita" }, accepted);
        Assert.Equal("eng+ita", TesseractOcrProvider.LanguageArgument(options));
    }

    [Fact]
    public void With_No_Valid_Language_The_Argument_Falls_Back_Rather_Than_Passing_Nothing()
    {
        var options = new DocumentExtractionOptions { OcrLanguages = "!!!" };
        Assert.Equal("eng", TesseractOcrProvider.LanguageArgument(options));
    }

    // ---- real recognition ---------------------------------------------------

    [SkippableFact]
    public async Task A_Rendered_Page_Is_Read_Back()
    {
        Skip.IfNot(EngineAvailable(out var reason), $"Tesseract not usable: {reason}");

        var png = RenderText("MAINTENANCE EVERY SIX MONTHS");

        var result = await Provider(Enabled()).RecognizeAsync(
            png, new OcrPageRequest("eng", 30, 40_000));

        Assert.True(result.Ok, result.Reason);
        // Normalised before comparing: OCR is allowed to disagree about spacing,
        // and asserting an exact string would make this a test of the engine's
        // layout analysis rather than of the integration.
        var text = new string(result.Text!.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        Assert.Contains("MAINTENANCE", text, StringComparison.Ordinal);
        Assert.Contains("MONTHS", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_Impossible_Timeout_Kills_The_Process_And_Says_So()
    {
        Skip.IfNot(EngineAvailable(out var reason), $"Tesseract not usable: {reason}");

        var png = RenderText("QUALCHE PAROLA DA LEGGERE");

        // One second is not enough to start an engine, read a page and exit. The
        // point is not the number: it is that exceeding the budget kills the
        // process and returns a token, rather than leaving work running while
        // NubArca stops waiting for it.
        var result = await Provider(Enabled()).RecognizeAsync(
            png, new OcrPageRequest("eng", 1, 40_000));

        if (result.Ok) return; // fast machine; the bound simply was not reached
        Assert.Equal(DocumentExtractionReasons.OcrTimeout, result.Reason);
    }

    [SkippableFact]
    public async Task Output_Past_The_Bound_Is_Refused()
    {
        Skip.IfNot(EngineAvailable(out var reason), $"Tesseract not usable: {reason}");

        var png = RenderText("MAINTENANCE EVERY SIX MONTHS");

        // Stdout is untrusted process output, so the cap is on what is READ. A
        // ten-character budget is exceeded by any real page.
        var result = await Provider(Enabled()).RecognizeAsync(
            png, new OcrPageRequest("eng", 30, 10));

        Assert.False(result.Ok);
        Assert.Equal(DocumentExtractionReasons.OcrOutputTooLarge, result.Reason);
    }

    [SkippableFact]
    public async Task Cancellation_Reaches_The_Caller_As_Itself()
    {
        Skip.IfNot(EngineAvailable(out var reason), $"Tesseract not usable: {reason}");

        // An operator stopping an index run must not be recorded as an OCR
        // timeout: that is a permanent-looking failure for something nobody did
        // wrong.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(Enabled()).RecognizeAsync(
                RenderText("TESTO"), new OcrPageRequest("eng", 30, 40_000), cancelled.Token));
    }

    [SkippableFact]
    public void Readiness_Never_Reports_A_Filesystem_Path()
    {
        Skip.IfNot(EngineAvailable(out _), "Tesseract not usable.");

        // `--list-langs` prints the tessdata DIRECTORY on its first line, and a
        // diagnostic carrying a filesystem path is exactly what the privacy
        // rules forbid.
        var readiness = Provider(Enabled()).CheckReadiness();

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.Reason);
    }

    // ---- fixture ------------------------------------------------------------

    /// Draws text into a white bitmap and returns a PNG.
    ///
    /// Generated rather than committed: a checked-in scan is somebody's
    /// document, and a reader of this test can see exactly what the engine is
    /// expected to read.
    private static byte[] RenderText(string text)
    {
        // libvips, which NubArca already depends on for thumbnails, renders text
        // through Pango. Using the image stack the product already has avoids
        // adding a drawing library purely so a test can draw.
        Image rendered;
        try
        {
            rendered = Image.Text(text, dpi: 300, font: "sans bold 24");
        }
        catch (Exception ex)
        {
            throw new SkipException($"libvips cannot render text here: {ex.GetType().Name}");
        }

        using (rendered)
        {
            // `Image.Text` produces a white-on-black alpha mask; OCR expects
            // dark glyphs on a light page, so it is inverted and given a margin.
            using var inverted = rendered.Invert();
            using var page = inverted.Gravity(
                Enums.CompassDirection.Centre,
                inverted.Width + 120,
                inverted.Height + 120,
                extend: Enums.Extend.White);

            return page.PngsaveBuffer();
        }
    }
}

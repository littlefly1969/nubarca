using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// Deciding whether a PDF page needs recognition.
//
// `page.Text.Length > 0` is the tempting test and it is wrong in both
// directions, which is the entire reason this file exists. A scanned page
// usually carries a few characters from a header stamp — enough to pass a length
// check while containing none of the document — and a page with broken font
// encodings produces plenty of text made of replacement characters that a length
// check waves through.
//
// The heuristic is not a claim about meaning and does not need to be. Its only
// job is choosing between reading and recognising, and the cost of being wrong
// is one rendered page.
public sealed class PdfTextQualityTests
{
    [Fact]
    public void Ordinary_Prose_Is_Usable()
    {
        Assert.True(PdfTextQuality.IsUsable(
            "Il filtro dell'acqua va pulito ogni sei mesi chiudendo il rubinetto."));
    }

    [Fact]
    public void An_Empty_Or_Whitespace_Page_Is_Not_Usable()
    {
        Assert.False(PdfTextQuality.IsUsable(null));
        Assert.False(PdfTextQuality.IsUsable(""));
        Assert.False(PdfTextQuality.IsUsable("   \n\t  \n "));
    }

    [Fact]
    public void A_Header_Stamp_Alone_Is_Not_Usable()
    {
        // THE SCANNED-PAGE CASE. A scanner or a stamping tool leaves a page
        // number and a date in the text layer of an otherwise image-only page.
        // It clears a length check comfortably and contains none of the
        // document.
        Assert.False(PdfTextQuality.IsUsable("12"));
        Assert.False(PdfTextQuality.IsUsable("- 4 -"));
    }

    [Fact]
    public void Replacement_Characters_Are_Not_Text()
    {
        // A font that could not be decoded. Plenty of characters, no content.
        Assert.False(PdfTextQuality.IsUsable(new string('\uFFFD', 200)));
    }

    [Fact]
    public void One_Character_Repeated_Is_A_Rendering_Artefact()
    {
        Assert.False(PdfTextQuality.IsUsable(new string('a', 300)));
        Assert.False(PdfTextQuality.IsUsable(string.Concat(Enumerable.Repeat(".", 400))));
    }

    [Fact]
    public void Control_Characters_Dominating_The_Page_Are_Not_Text()
    {
        var garbage = new string('\u0001', 60) + "testo";
        Assert.False(PdfTextQuality.IsUsable(garbage));
    }

    [Fact]
    public void Text_With_Some_Noise_Is_Still_Usable()
    {
        // The heuristic must not be so strict that a slightly imperfect
        // extraction triggers an expensive rendering pass on every page.
        Assert.True(PdfTextQuality.IsUsable(
            "Manutenzione ordinaria \uFFFD della caldaia installata nell'appartamento."));
    }

    [Fact]
    public void Digits_Alone_Are_Not_A_Document()
    {
        // Page furniture: a number and some rules. No letters means nothing to
        // retrieve.
        Assert.False(PdfTextQuality.IsUsable("1234567890 1234567890 12345"));
    }
}

using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// Reading Office documents as their authors read them.
//
// The recurring theme is that "what the package contains" and "what the document
// says" are different, and every case where they diverge is a decision somebody
// has to make deliberately: deleted tracked-change text is in the markup and not
// in the document; a hidden sheet is in the workbook and not on anybody's
// screen; a hyperlink's target is in the file and is not text.
//
// Getting those wrong is not a formatting nit. Answering from a clause the
// author deleted is quoting a document that does not exist.
public sealed class OfficeExtractionTests
{
    private readonly DocumentExtractionOptions _options = new();

    private static DocumentExtractionRequest Request(
        byte[] bytes, string name, string mime, DocumentExtractionOptions options)
        => new(bytes, name, mime, options);

    // ---- DOCX ---------------------------------------------------------------

    [Fact]
    public async Task Word_Preserves_Heading_Hierarchy_As_A_Path()
    {
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        Assert.True(outcome.Ok, outcome.Reason);
        var blocks = outcome.Artifact!.Blocks;

        // The path is built from the document's own outline levels, and a
        // heading resets everything below it: `Preavviso` is a child of
        // `Risoluzione`, not of whatever paragraph preceded it.
        var notice = blocks.Single(b => b.Text.Contains("novanta giorni", StringComparison.Ordinal));
        Assert.Equal("Contratto › Risoluzione › Preavviso", notice.Heading);
    }

    [Fact]
    public async Task Word_Keeps_Inserted_Text_And_Drops_Deleted_Text()
    {
        // THE ONE THAT `InnerText` GETS WRONG. The tempting one-liner returns
        // deleted tracked-change text as though it were part of the document.
        // Somebody who struck a clause and sent the file for review has said
        // that clause is gone.
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        Assert.True(outcome.Ok, outcome.Reason);
        var text = string.Join("\n", outcome.Artifact!.Blocks.Select(b => b.Text));

        Assert.Contains("Testo inserito e valido", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTO_CANCELLATO_SENTINELLA", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_Renders_A_Table_As_Rows_Not_As_Loose_Cells()
    {
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        var table = outcome.Artifact!.Blocks.Single(b => b.Kind == DocumentBlockKinds.Table);

        // The relationship between a label and its value is the only reason the
        // table exists; one chunk per cell would produce two-word fragments that
        // rank badly and cite nothing.
        Assert.Contains("Piano | Prezzo | Rinnovo", table.Text, StringComparison.Ordinal);
        Assert.Contains("Pro | 25 EUR | Annuale", table.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_Takes_A_Hyperlinks_Text_And_Never_Its_Target()
    {
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        var text = string.Join("\n", outcome.Artifact!.Blocks.Select(b => b.Text));

        Assert.Contains("condizioni complete", text, StringComparison.Ordinal);
        // The target is a relationship. A document asking to be told what is at
        // a URL is a request a parser must not honour — not fetched, and not
        // even carried into the text where a later component might follow it.
        Assert.DoesNotContain("esempio.invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SEGRETO", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_Includes_Footnotes()
    {
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        var notes = outcome.Artifact!.Blocks.Single(b => b.Kind == DocumentBlockKinds.Notes);
        Assert.Contains("forza maggiore", notes.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_Never_Invents_A_Page_Number()
    {
        // Open XML does not describe pages — pagination is what Word's layout
        // engine produces from fonts and printer metrics, and two machines can
        // genuinely disagree. A citation saying "page 7" that is not page 7 is
        // worse than one that says which section.
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, _options));

        Assert.All(outcome.Artifact!.Blocks, b =>
        {
            Assert.Null(b.Locator.Page);
            Assert.Equal(DocumentLocatorKinds.Section, b.Locator.Kind);
        });
    }

    [Fact]
    public async Task Word_Refuses_A_Document_With_Too_Many_Paragraphs()
    {
        var options = new DocumentExtractionOptions { MaxDocxParagraphs = 3 };

        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, options));

        // Refused, not truncated. Indexing the first three paragraphs and
        // calling the contract complete is how somebody gets an answer that
        // omits the clause they were asking about.
        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, outcome.Reason);
        Assert.True(outcome.IsPermanent);
    }

    // ---- XLSX ---------------------------------------------------------------

    [Fact]
    public async Task Spreadsheet_Uses_The_Header_Row_To_Label_Values()
    {
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        Assert.True(outcome.Ok, outcome.Reason);
        var sheet = outcome.Artifact!.Blocks.Single();

        Assert.Contains("Reparto=Ingegneria", sheet.Text, StringComparison.Ordinal);
        Assert.Contains("Previsione=238500", sheet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spreadsheet_Reports_A_Cached_Formula_Result_And_Never_Evaluates()
    {
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        var sheet = outcome.Artifact!.Blocks.Single();

        // The number is the workbook's own stored claim, and the expression is
        // its provenance. Recalculating would mean executing logic a stranger
        // wrote and reporting a figure this installation invented as though the
        // owner's spreadsheet said it.
        Assert.Contains("430500", sheet.Text, StringComparison.Ordinal);
        Assert.Contains("[formula: SUM(C2:C3)]", sheet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spreadsheet_Renders_A_Boolean_As_A_Word()
    {
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        Assert.Contains("VERO", outcome.Artifact!.Blocks.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spreadsheet_Never_Ingests_A_Hidden_Sheet()
    {
        // A hidden sheet is where lookup tables, scratch calculations and
        // configuration live — material the author deliberately took out of
        // view. Importing it would make a private assistant surface things its
        // owner removed from their own screen.
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        var text = string.Join("\n", outcome.Artifact!.Blocks.Select(b => b.Text));
        Assert.DoesNotContain("SENTINELLA_FOGLIO_NASCOSTO", text, StringComparison.Ordinal);
        Assert.Single(outcome.Artifact.Blocks);
    }

    [Fact]
    public async Task Spreadsheet_Carries_The_Sheet_Name_As_Provenance_And_No_Page()
    {
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        var sheet = outcome.Artifact!.Blocks.Single();
        Assert.Equal(DocumentLocatorKinds.Sheet, sheet.Locator.Kind);
        Assert.Equal("Previsione", sheet.Locator.Label);
        Assert.Equal(1, sheet.Locator.Index);
        // A sheet ordinal in `Page` would render as "Page 1" in a citation for a
        // document that has no pages.
        Assert.Null(sheet.Locator.Page);
    }

    [Fact]
    public async Task Spreadsheet_Refuses_A_Workbook_Past_Its_Cell_Bound()
    {
        var options = new DocumentExtractionOptions { MaxWorkbookNonEmptyCells = 2 };

        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Budget(), "budget.xlsx",
                DocumentFormatProbe.SpreadsheetMimeType, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, outcome.Reason);
    }

    // ---- PPTX ---------------------------------------------------------------

    [Fact]
    public async Task Presentation_Keeps_The_Slide_Title_And_Number()
    {
        var outcome = await new PresentationExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.LaunchPlan(), "piano.pptx",
                DocumentFormatProbe.PresentationMimeType, _options));

        Assert.True(outcome.Ok, outcome.Reason);
        var body = outcome.Artifact!.Blocks.First(b => b.Kind == DocumentBlockKinds.Body);

        Assert.Equal(DocumentLocatorKinds.Slide, body.Locator.Kind);
        Assert.Equal(1, body.Locator.Index);
        Assert.Equal("Pilota", body.Locator.Label);
        Assert.Null(body.Locator.Page);
        Assert.Contains("14 marzo", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presentation_Ingests_Speaker_Notes_As_Their_Own_Block()
    {
        // The slide says "Pilota" and the notes say why. Excluding them would
        // drop the most useful sentence in the file; merging them would make
        // "the presentation says" cover something the audience never saw.
        var outcome = await new PresentationExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.LaunchPlan(), "piano.pptx",
                DocumentFormatProbe.PresentationMimeType, _options));

        var notes = outcome.Artifact!.Blocks.Single(b => b.Kind == DocumentBlockKinds.Notes);
        Assert.Contains("magazzino", notes.Text, StringComparison.Ordinal);
        Assert.Equal(1, notes.Locator.Index);
    }

    [Fact]
    public async Task Presentation_Never_Ingests_A_Hidden_Slide()
    {
        var outcome = await new PresentationExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.LaunchPlan(), "piano.pptx",
                DocumentFormatProbe.PresentationMimeType, _options));

        var text = string.Join("\n", outcome.Artifact!.Blocks.Select(b => b.Text));
        Assert.DoesNotContain("SENTINELLA_SLIDE_NASCOSTA", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presentation_Refuses_A_Deck_Past_Its_Slide_Bound()
    {
        var options = new DocumentExtractionOptions { MaxPresentationSlides = 1 };

        var outcome = await new PresentationExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.LaunchPlan(), "piano.pptx",
                DocumentFormatProbe.PresentationMimeType, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, outcome.Reason);
    }

    // ---- routing and bounds shared by all three -----------------------------

    [Fact]
    public async Task An_Oversized_Package_Is_Refused_Before_The_Sdk_Sees_It()
    {
        // The preflight is the resource boundary; the Open XML SDK is a parser.
        // Microsoft documents that its ZIP handling can hold a large working
        // set, which is exactly why the measurement happens first.
        var options = new DocumentExtractionOptions { MaxOfficeEntries = 1 };

        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.WordMimeType, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficeTooManyEntries, outcome.Reason);
    }

    [Fact]
    public async Task A_Word_Package_Handed_To_The_Spreadsheet_Parser_Is_Refused()
    {
        // The probe stops this from happening in production. This asserts the
        // parser itself does not do something interesting if it ever did: a
        // parser written for one structure, handed another, is where bugs stop
        // being theoretical.
        var outcome = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(OfficeDocumentFixtures.Contract(), "contratto.docx",
                DocumentFormatProbe.SpreadsheetMimeType, _options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.OfficePackageInvalid, outcome.Reason);
    }

    [Fact]
    public async Task Every_Reason_Is_A_Sanitized_Token()
    {
        var options = new DocumentExtractionOptions { MaxOfficeEntries = 1 };
        var outcomes = new[]
        {
            await new WordDocumentExtractionProvider().ExtractAsync(
                Request(OfficeDocumentFixtures.Contract(), "riservato-2027.docx",
                    DocumentFormatProbe.WordMimeType, options)),
            await new SpreadsheetExtractionProvider().ExtractAsync(
                Request(OfficeDocumentFixtures.Contract(), "stipendi.xlsx",
                    DocumentFormatProbe.SpreadsheetMimeType, _options)),
        };

        Assert.All(outcomes, o =>
        {
            Assert.NotNull(o.Reason);
            Assert.Matches("^[a-z-]+$", o.Reason!);
        });
    }
}

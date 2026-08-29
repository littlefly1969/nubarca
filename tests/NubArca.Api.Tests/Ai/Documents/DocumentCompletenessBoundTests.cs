using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using NubArca.Api.Ai.Documents;
using Xunit;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NubArca.Api.Tests.Ai.Documents;

// THE COMPLETENESS INVARIANT, exercised adversarially, one family at a time:
//
//     a document that exceeds a completeness-critical bound is REFUSED;
//     NubArca never publishes the first N pages, blocks, chunks or characters
//     as a Completed document.
//
// Every bound here used to be a `break` or a `substring`. Both produce the same
// artefact — a document that reads as whole and is not — and that artefact is
// worse than no document at all: an owner asking a question of their contract
// gets a confident answer drawn from the part that happened to fit, with
// nothing anywhere reporting that the rest was dropped. A refused document is
// merely unanswerable, which is visible and recoverable.
//
// So each family is tested at the boundary in both directions. EXACTLY at the
// bound must still succeed — a rule that refuses everything is not a bound, it
// is an outage — and bound+1 must refuse rather than shorten.
public sealed class DocumentCompletenessBoundTests
{
    private static DocumentExtractionRequest Request(
        byte[] bytes, string name, string mime, DocumentExtractionOptions options)
        => new(bytes, name, mime, options);

    // ---- DOCX ---------------------------------------------------------------

    /// A document of `paragraphs` paragraphs, each exactly `size` characters.
    private static byte[] Docx(int paragraphs, int size, string? note = null)
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
                   buffer, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            for (var i = 0; i < paragraphs; i++)
            {
                var paragraph = new Paragraph();
                paragraph.AppendChild(new W.Run(
                    new W.Text(new string('a', size)) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(paragraph);
            }

            if (note is not null)
            {
                var footnotes = main.AddNewPart<FootnotesPart>();
                footnotes.Footnotes = new Footnotes();
                var footnote = new Footnote { Id = 1 };
                var paragraph = new Paragraph();
                paragraph.AppendChild(new W.Run(
                    new W.Text(note) { Space = SpaceProcessingModeValues.Preserve }));
                footnote.AppendChild(paragraph);
                footnotes.Footnotes.AppendChild(footnote);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Word_At_The_Character_Bound_Succeeds_And_One_Past_It_Refuses()
    {
        // Ten paragraphs of one hundred characters is exactly one thousand.
        var atBound = new DocumentExtractionOptions { MaxCharacters = 1_000 };
        var ok = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(Docx(10, 100), "a.docx", DocumentFormatProbe.WordMimeType, atBound));

        Assert.True(ok.Ok, ok.Reason);
        Assert.Equal(1_000, ok.Artifact!.Blocks.Sum(b => b.Text.Length));

        // One character less of budget, and the same document is refused whole.
        var pastBound = new DocumentExtractionOptions { MaxCharacters = 999 };
        var refused = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(Docx(10, 100), "a.docx", DocumentFormatProbe.WordMimeType, pastBound));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
        Assert.True(refused.IsPermanent, "a content verdict is permanent");
        Assert.Null(refused.Artifact);
    }

    [Fact]
    public async Task Word_Past_The_Block_Bound_Refuses_Instead_Of_Returning_The_First_N()
    {
        var options = new DocumentExtractionOptions { MaxChunks = 4 };

        var atBound = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(Docx(4, 10), "a.docx", DocumentFormatProbe.WordMimeType, options));
        Assert.True(atBound.Ok, atBound.Reason);
        Assert.Equal(4, atBound.Artifact!.Blocks.Count);

        var refused = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(Docx(5, 10), "a.docx", DocumentFormatProbe.WordMimeType, options));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
    }

    [Fact]
    public async Task Word_Footnotes_That_Do_Not_Fit_Refuse_Rather_Than_Disappear()
    {
        // The body fits exactly; the footnote does not. A contract's exclusions
        // frequently live in its footnotes, so publishing the body and dropping
        // them is the most misleading outcome available — worse than refusing.
        var options = new DocumentExtractionOptions { MaxCharacters = 100 };
        var outcome = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(
                Docx(1, 100, note: "Esclusione rilevante che non entra nel limite."),
                "a.docx", DocumentFormatProbe.WordMimeType, options));

        Assert.False(outcome.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, outcome.Reason);

        // And the control: the same document with room for the note succeeds
        // AND actually carries it, so the refusal above is about the bound and
        // not about footnotes being broken.
        var roomy = new DocumentExtractionOptions { MaxCharacters = 1_000 };
        var complete = await new WordDocumentExtractionProvider().ExtractAsync(
            Request(
                Docx(1, 100, note: "Esclusione rilevante che non entra nel limite."),
                "a.docx", DocumentFormatProbe.WordMimeType, roomy));

        Assert.True(complete.Ok, complete.Reason);
        Assert.Contains(complete.Artifact!.Blocks, b => b.Kind == DocumentBlockKinds.Notes);
    }

    // ---- XLSX ---------------------------------------------------------------

    private static byte[] Xlsx(int rows, int cellSize)
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(
                   buffer, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();

            for (var r = 1; r <= rows; r++)
            {
                var row = new Row { RowIndex = (uint)r };
                var cell = new Cell
                {
                    CellReference = $"A{r}",
                    DataType = CellValues.String,
                    CellValue = new CellValue(new string('b', cellSize)),
                };
                row.AppendChild(cell);
                sheetData.AppendChild(row);
            }

            worksheetPart.Worksheet = new Worksheet(sheetData);
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Dati",
            });
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Workbook_Rows_Are_Bounded_By_Characters_And_Never_Silently_Dropped()
    {
        // A generous character budget takes the whole workbook…
        var roomy = new DocumentExtractionOptions { MaxCharacters = 100_000 };
        var complete = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(Xlsx(50, 100), "a.xlsx", DocumentFormatProbe.SpreadsheetMimeType, roomy));

        Assert.True(complete.Ok, complete.Reason);
        var rowsRendered = complete.Artifact!.Blocks.Single().Text.Split('\n').Length;
        Assert.Equal(50, rowsRendered);

        // …and a budget the completed workbook would exceed refuses it, rather
        // than emitting the sheet's first rows as the whole sheet.
        var tight = new DocumentExtractionOptions { MaxCharacters = 500 };
        var refused = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(Xlsx(50, 100), "a.xlsx", DocumentFormatProbe.SpreadsheetMimeType, tight));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
        Assert.Null(refused.Artifact);
    }

    [Fact]
    public async Task Workbook_Row_And_Cell_Bounds_Still_Refuse()
    {
        // The pre-existing structural bounds are untouched by the character
        // work above — a regression here would mean the new bound replaced them
        // rather than joining them.
        var rowBound = new DocumentExtractionOptions { MaxWorkbookRowsPerSheet = 3 };
        var refused = await new SpreadsheetExtractionProvider().ExtractAsync(
            Request(Xlsx(10, 5), "a.xlsx", DocumentFormatProbe.SpreadsheetMimeType, rowBound));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
    }

    // ---- PPTX ---------------------------------------------------------------

    private static byte[] Pptx(int slides, int bodySize)
    {
        using var buffer = new MemoryStream();
        using (var document = PresentationDocument.Create(
                   buffer, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = new P.SlideIdList();
            presentationPart.Presentation.AppendChild(slideIdList);

            for (var i = 0; i < slides; i++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                var shapeTree = new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties());

                var shape = new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2, Name = "Corpo" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new DocumentFormat.OpenXml.Drawing.BodyProperties(),
                        new DocumentFormat.OpenXml.Drawing.Paragraph(
                            new DocumentFormat.OpenXml.Drawing.Run(
                                new DocumentFormat.OpenXml.Drawing.Text(new string('c', bodySize))))));
                shapeTree.AppendChild(shape);

                slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));

                slideIdList.AppendChild(new P.SlideId
                {
                    Id = (uint)(256 + i),
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Slide_Text_Past_Its_Bound_Refuses_Instead_Of_Being_Cut()
    {
        var atBound = new DocumentExtractionOptions { MaxSlideTextCharacters = 200 };
        var ok = await new PresentationExtractionProvider().ExtractAsync(
            Request(Pptx(1, 200), "a.pptx", DocumentFormatProbe.PresentationMimeType, atBound));

        Assert.True(ok.Ok, ok.Reason);
        Assert.Equal(200, ok.Artifact!.Blocks.Single().Text.Length);

        // 201 characters against a 200 bound used to become a 200-character
        // slide, published as the whole slide.
        var refused = await new PresentationExtractionProvider().ExtractAsync(
            Request(Pptx(1, 201), "a.pptx", DocumentFormatProbe.PresentationMimeType, atBound));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
        Assert.Null(refused.Artifact);
    }

    [Fact]
    public async Task A_Deck_Of_Individually_Small_Slides_Still_Meets_The_Document_Bound()
    {
        // Every slide is well inside its own ceiling; the DECK is not. Per-slide
        // bounds say nothing about a thousand small slides, which is why the
        // document-wide ceiling had to be enforced here too.
        var options = new DocumentExtractionOptions
        {
            MaxSlideTextCharacters = 1_000,
            MaxCharacters = 250,
        };

        var refused = await new PresentationExtractionProvider().ExtractAsync(
            Request(Pptx(10, 100), "a.pptx", DocumentFormatProbe.PresentationMimeType, options));

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
    }

    // ---- the chunker --------------------------------------------------------

    private static IReadOnlyList<ExtractedDocumentBlock> Blocks(int count, int size)
        => Enumerable.Range(1, count)
            .Select(i => new ExtractedDocumentBlock(
                i, DocumentBlockKinds.Body, new string('d', size), null,
                new DocumentLocator(DocumentLocatorKinds.Slide, i)))
            .ToList();

    [Fact]
    public void Chunker_Returns_An_Outcome_And_Refuses_Past_The_Chunk_Bound()
    {
        var options = new DocumentExtractionOptions { MaxChunks = 3, MaxChunkCharacters = 1_000 };

        var atBound = RichDocumentChunker.Chunk(Blocks(3, 50), options);
        Assert.True(atBound.Ok, atBound.Reason);
        Assert.Equal(3, atBound.Chunks!.Count);

        var refused = RichDocumentChunker.Chunk(Blocks(4, 50), options);

        // The old shape returned three chunks here, and every caller treated
        // that as a complete document.
        Assert.False(refused.Ok);
        Assert.Null(refused.Chunks);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
    }

    [Fact]
    public void Chunker_Refuses_When_SPLITTING_One_Block_Would_Exceed_The_Bound()
    {
        // A single block far larger than the chunk budget splits into many
        // pieces. That path had its own separate cap, and its own separate way
        // of returning a partial list.
        var options = new DocumentExtractionOptions { MaxChunks = 2, MaxChunkCharacters = 200 };

        var refused = RichDocumentChunker.Chunk(Blocks(1, 2_000), options);

        Assert.False(refused.Ok);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, refused.Reason);
    }

    // ---- the source budget --------------------------------------------------

    [Fact]
    public void The_Probe_Budget_Makes_The_Rich_Source_Limits_Reachable()
    {
        // The bug this closes: the pre-probe gate used the native-text ceiling,
        // so a PDF or an Office package larger than 4 MiB was refused before it
        // was ever opened — and the 64 MiB limits an operator can configure
        // could never be exercised by anything.
        var options = new DocumentExtractionOptions();

        Assert.Equal(
            options.EffectiveMaxPdfSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget(DocumentFormatProbe.PdfMimeType, "a.pdf", options));

        foreach (var mime in new[]
        {
            DocumentFormatProbe.WordMimeType,
            DocumentFormatProbe.SpreadsheetMimeType,
            DocumentFormatProbe.PresentationMimeType,
        })
        {
            Assert.Equal(
                options.EffectiveMaxOfficeSourceBytes,
                DocumentFormatProbe.CandidateSourceBudget(mime, "a.docx", options));
        }

        // A generic declared type earns the rich budget only through a
        // supported extension — the same rule IsCandidate applies.
        Assert.Equal(
            options.EffectiveMaxOfficeSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget("application/octet-stream", "a.xlsx", options));
        Assert.Equal(
            options.EffectiveMaxPdfSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget("application/zip", "a.pdf", options));

        // And everything else keeps the text ceiling, which is what stops the
        // budget from becoming a way to read an arbitrary 64 MiB file.
        Assert.Equal(
            options.EffectiveMaxSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget("text/plain", "a.txt", options));
        Assert.Equal(
            options.EffectiveMaxSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget("application/octet-stream", "a.bin", options));
        Assert.Equal(
            options.EffectiveMaxSourceBytes,
            DocumentFormatProbe.CandidateSourceBudget("application/octet-stream", null, options));

        // The budget is genuinely larger than the native ceiling — the whole
        // point — and still bounded.
        Assert.True(options.EffectiveMaxPdfSourceBytes > options.EffectiveMaxSourceBytes);
        Assert.True(
            options.EffectiveMaxOfficeSourceBytes
            <= DocumentExtractionOptions.AbsoluteMaxSourceBytes);
    }
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NubArca.Api.Tests.Ai.Documents;

// Synthetic Office documents, built in memory.
//
// Written rather than checked in as binaries for two reasons that both matter.
// A committed `.docx` is opaque: a test asserting "the heading path is
// Agreement › Termination" gives a reader no way to see that the fixture
// actually contains those headings, so a failure is unreadable. And a real
// document from anywhere carries somebody's content and somebody's licence.
//
// Everything here is invented, in Italian, about boilers and budgets.
internal static class OfficeDocumentFixtures
{
    // ---- DOCX ---------------------------------------------------------------

    /// A contract with a heading hierarchy, a table, a footnote and a
    /// tracked-change edit — one document carrying every DOCX behaviour worth
    /// asserting.
    internal static byte[] Contract()
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
                   buffer, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            AddStyles(main);

            body.AppendChild(Heading("Contratto", 1));
            body.AppendChild(Text("Il presente contratto disciplina la manutenzione."));

            body.AppendChild(Heading("Risoluzione", 2));
            body.AppendChild(Text("La parte che intende recedere deve darne comunicazione."));

            body.AppendChild(Heading("Preavviso", 3));
            body.AppendChild(Text("Il preavviso richiesto è di novanta giorni."));

            // Tracked changes: the inserted clause is part of the document, the
            // deleted one is not.
            var revised = new Paragraph();
            revised.AppendChild(new W.Run(new W.Text("Clausola valida. ") { Space = SpaceProcessingModeValues.Preserve }));
            var inserted = new InsertedRun { Author = "Autore", Date = DateTime.UtcNow };
            inserted.AppendChild(new W.Run(new W.Text("Testo inserito e valido.") { Space = SpaceProcessingModeValues.Preserve }));
            revised.AppendChild(inserted);
            var deleted = new DeletedRun { Author = "Autore", Date = DateTime.UtcNow };
            deleted.AppendChild(new W.Run(new DeletedText("TESTO_CANCELLATO_SENTINELLA") { Space = SpaceProcessingModeValues.Preserve }));
            revised.AppendChild(deleted);
            body.AppendChild(revised);

            body.AppendChild(Heading("Piani", 2));
            body.AppendChild(Table(
                new[] { "Piano", "Prezzo", "Rinnovo" },
                new[] { "Base", "10 EUR", "Mensile" },
                new[] { "Pro", "25 EUR", "Annuale" }));

            // A hyperlink: its display text is content, its target is not.
            var linkParagraph = new Paragraph();
            var relationship = main.AddHyperlinkRelationship(
                new Uri("https://esempio.invalid/SEGRETO"), isExternal: true);
            var hyperlink = new W.Hyperlink { Id = relationship.Id };
            hyperlink.AppendChild(new W.Run(new W.Text("condizioni complete")));
            linkParagraph.AppendChild(hyperlink);
            body.AppendChild(linkParagraph);

            AddFootnote(main, "La penale non si applica in caso di forza maggiore.");
        }

        return buffer.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();
        for (var level = 1; level <= 3; level++)
        {
            styles.AppendChild(new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}",
                StyleName = new StyleName { Val = $"heading {level}" },
                StyleParagraphProperties = new StyleParagraphProperties(
                    new OutlineLevel { Val = level - 1 }),
            });
        }
        part.Styles = styles;
    }

    private static Paragraph Heading(string text, int level)
        => new(
            new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{level}" }),
            new W.Run(new W.Text(text)));

    private static Paragraph Text(string text) => new(new W.Run(new W.Text(text)));

    private static W.Table Table(params string[][] rows)
    {
        var table = new W.Table();
        foreach (var row in rows)
        {
            var tableRow = new W.TableRow();
            foreach (var value in row)
            {
                tableRow.AppendChild(new W.TableCell(new Paragraph(new W.Run(new W.Text(value)))));
            }
            table.AppendChild(tableRow);
        }
        return table;
    }

    private static void AddFootnote(MainDocumentPart main, string text)
    {
        var part = main.AddNewPart<FootnotesPart>();
        part.Footnotes = new Footnotes(
            new Footnote(new Paragraph(new W.Run(new W.Text(text))))
            {
                Id = 1,
                Type = FootnoteEndnoteValues.Normal,
            });
    }

    // ---- XLSX ---------------------------------------------------------------

    /// A workbook with a visible sheet carrying a header row, numbers, a
    /// boolean, an inline string and a cached formula result — plus a HIDDEN
    /// sheet holding a sentinel that must never be ingested.
    internal static byte[] Budget(string hiddenSentinel = "SENTINELLA_FOGLIO_NASCOSTO")
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(
                   buffer, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            var visible = workbookPart.AddNewPart<WorksheetPart>();
            visible.Worksheet = new Worksheet(new SheetData(
                Row(1, ("A", "Reparto"), ("B", "Budget"), ("C", "Previsione")),
                Row(2, ("A", "Ingegneria"), ("B", "250000"), ("C", "238500")),
                Row(3, ("A", "Vendite"), ("B", "180000"), ("C", "192000")),
                FormulaRow(4, "Totale", "430500", "SUM(C2:C3)"),
                BooleanRow(5, "Approvato", true)));
            sheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(visible),
                SheetId = 1,
                Name = "Previsione",
            });

            var hidden = workbookPart.AddNewPart<WorksheetPart>();
            hidden.Worksheet = new Worksheet(new SheetData(
                Row(1, ("A", hiddenSentinel))));
            sheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(hidden),
                SheetId = 2,
                Name = "Interno",
                State = SheetStateValues.Hidden,
            });
        }

        return buffer.ToArray();
    }

    private static Row Row(uint index, params (string Column, string Value)[] cells)
    {
        var row = new Row { RowIndex = index };
        foreach (var (column, value) in cells)
        {
            row.AppendChild(new Cell
            {
                CellReference = column + index,
                DataType = double.TryParse(value, out _) ? CellValues.Number : CellValues.String,
                CellValue = new CellValue(value),
            });
        }
        return row;
    }

    private static Row FormulaRow(uint index, string label, string cached, string formula)
    {
        var row = new Row { RowIndex = index };
        row.AppendChild(new Cell
        {
            CellReference = "A" + index,
            DataType = CellValues.String,
            CellValue = new CellValue(label),
        });
        row.AppendChild(new Cell
        {
            CellReference = "C" + index,
            DataType = CellValues.Number,
            CellFormula = new CellFormula(formula),
            CellValue = new CellValue(cached),
        });
        return row;
    }

    private static Row BooleanRow(uint index, string label, bool value)
    {
        var row = new Row { RowIndex = index };
        row.AppendChild(new Cell
        {
            CellReference = "A" + index,
            DataType = CellValues.String,
            CellValue = new CellValue(label),
        });
        row.AppendChild(new Cell
        {
            CellReference = "B" + index,
            DataType = CellValues.Boolean,
            CellValue = new CellValue(value ? "1" : "0"),
        });
        return row;
    }

    // ---- PPTX ---------------------------------------------------------------

    /// A deck with a visible titled slide carrying speaker notes, and a HIDDEN
    /// slide holding a sentinel.
    internal static byte[] LaunchPlan(string hiddenSentinel = "SENTINELLA_SLIDE_NASCOSTA")
    {
        using var buffer = new MemoryStream();
        using (var document = PresentationDocument.Create(
                   buffer, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new Presentation(new SlideIdList());

            AddSlide(presentationPart, 256U,
                title: "Pilota",
                body: "Il lancio del pilota è previsto per il 14 marzo.",
                notes: "Il rollout è vincolato alla disponibilità del magazzino.",
                hidden: false);

            AddSlide(presentationPart, 257U,
                title: "Riserva",
                body: hiddenSentinel,
                notes: null,
                hidden: true);
        }

        return buffer.ToArray();
    }

    private static void AddSlide(
        PresentationPart presentationPart, uint id,
        string title, string body, string? notes, bool hidden)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties());

        shapeTree.AppendChild(Shape(2, title, PlaceholderValues.Title));
        shapeTree.AppendChild(Shape(3, body, PlaceholderValues.Body));

        slidePart.Slide = new Slide(new CommonSlideData(shapeTree), new ColorMapOverride())
        {
            Show = hidden ? false : null,
        };

        if (notes is not null)
        {
            var notesPart = slidePart.AddNewPart<NotesSlidePart>();
            var notesTree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties());
            notesTree.AppendChild(Shape(2, notes, PlaceholderValues.Body));
            notesPart.NotesSlide = new NotesSlide(new CommonSlideData(notesTree), new ColorMapOverride());
        }

        presentationPart.Presentation!.SlideIdList!.AppendChild(new SlideId
        {
            Id = id,
            RelationshipId = presentationPart.GetIdOfPart(slidePart),
        });
    }

    private static P.Shape Shape(uint id, string text, PlaceholderValues placeholder)
        => new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Shape" + id },
                new P.NonVisualShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties(
                    new PlaceholderShape { Type = placeholder })),
            new P.ShapeProperties(),
            new P.TextBody(
                new D.BodyProperties(),
                new D.ListStyle(),
                new D.Paragraph(new D.Run(new D.Text(text)))));
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text;

namespace NubArca.Api.Ai.Documents;

/// Reading a workbook without ever running one.
///
/// The distinction this whole file is built around: a spreadsheet is a program
/// AND a document, and NubArca reads the document. A formula is extracted as
/// what it says — its expression, and the value the workbook last stored for it
/// — and never evaluated. That is not caution about performance. Recalculating
/// would mean executing logic a stranger wrote, resolving links to workbooks
/// that live somewhere else, and reporting numbers this installation invented as
/// though the owner's spreadsheet said them.
///
/// The cached value is the workbook's own claim about its result, so it is
/// reported as such and never described as freshly computed.
///
/// ROWS, NOT CELLS. One chunk per cell produces thousands of two-word fragments
/// that rank badly and cite nothing; the relationship between a label and its
/// value is the only reason the table exists, and a row is the smallest unit
/// that preserves it.
public sealed class SpreadsheetExtractionProvider : IDocumentExtractionProvider
{
    public DocumentFormatKind Format => DocumentFormatKind.SpreadsheetOpenXml;

    public string ProfileKey => DocumentTextSources.SpreadsheetProfileKey;

    public Task<DocumentExtractionOutcome> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (archive, refusal) = OpenXmlPackageReader.Open(request.Bytes, request.Options);
        archive?.Dispose();
        if (refusal is not null)
        {
            return Task.FromResult(DocumentExtractionOutcome.Rejected(refusal));
        }

        try
        {
            return Task.FromResult(Extract(request, cancellationToken));
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException
                                       or FileFormatException or ArgumentOutOfRangeException)
        {
            return Task.FromResult(
                DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid));
        }
    }

    private static DocumentExtractionOutcome Extract(
        DocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Bytes.ToArray(), writable: false);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);

        var workbookPart = document.WorkbookPart;
        var sheets = workbookPart?.Workbook?.Sheets?.Elements<Sheet>().ToList();
        if (workbookPart is null || sheets is null)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid);
        }

        var options = request.Options;
        var sharedStrings = SharedStrings(workbookPart, options);
        var blocks = new List<ExtractedDocumentBlock>();
        var ordinal = 0;
        var totalCells = 0;
        var visibleIndex = 0;

        if (sheets.Count > options.EffectiveMaxWorkbookSheets)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.DocumentTooComplex);
        }

        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // HIDDEN SHEETS ARE NOT INGESTED.
            //
            // The workbook a person sees is the workbook NubArca answers from.
            // Hidden and very-hidden sheets are where lookup tables, scratch
            // calculations and configuration live — material the author
            // deliberately took out of view — and quietly importing it would
            // make a private assistant surface things its owner removed from
            // their own screen.
            if (sheet.State is not null && sheet.State.Value != SheetStateValues.Visible) continue;

            var id = sheet.Id?.Value;
            if (id is null) continue;
            if (workbookPart.GetPartById(id) is not WorksheetPart worksheetPart) continue;

            visibleIndex++;
            var name = sheet.Name?.Value ?? $"Foglio {visibleIndex}";

            var rows = worksheetPart.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>()
                       ?? Enumerable.Empty<Row>();

            var rendered = new List<string>();
            string[]? header = null;
            var rowCount = 0;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++rowCount > options.EffectiveMaxWorkbookRowsPerSheet)
                {
                    return DocumentExtractionOutcome.Rejected(
                        DocumentExtractionReasons.DocumentTooComplex);
                }

                var cells = new List<(string Reference, string Value)>();
                var columns = 0;
                foreach (var cell in row.Elements<Cell>())
                {
                    if (++columns > options.EffectiveMaxWorkbookColumnsPerSheet)
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }

                    var value = CellText(cell, sharedStrings, options);
                    // A BLANK CELL CONTRIBUTES NOTHING and does not extend the
                    // used range. Excel routinely stores styled-but-empty cells
                    // for thousands of rows, and treating those as content
                    // produces a document of separators.
                    if (value.Length == 0) continue;

                    if (++totalCells > options.EffectiveMaxWorkbookNonEmptyCells)
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }

                    cells.Add((ColumnOf(cell.CellReference?.Value), value));
                }

                if (cells.Count == 0) continue;

                // HEADER INFERENCE IS DELIBERATELY CONSERVATIVE AND
                // DETERMINISTIC. The first non-empty row counts as a header only
                // when every one of its cells is non-numeric text; anything else
                // falls back to cell references. No model, no heuristic scoring
                // — a wrong guess here silently relabels every value in the
                // sheet, and "A12=250000" is merely unhelpful where
                // "Forecast=250000" against the wrong column is untrue.
                if (header is null && rendered.Count == 0 && LooksLikeHeader(cells))
                {
                    header = cells.Select(c => c.Value).ToArray();
                    rendered.Add(string.Join(" | ", header));
                    continue;
                }

                rendered.Add(RenderRow(cells, header, row.RowIndex?.Value));
            }

            if (rendered.Count == 0) continue;

            var text = string.Join("\n", rendered);
            blocks.Add(new ExtractedDocumentBlock(
                ++ordinal,
                DocumentBlockKinds.Table,
                text,
                name,
                // SHEET, never Page. A sheet ordinal in `Page` would render as
                // "Page 3" in a citation for a document that has no pages.
                new DocumentLocator(DocumentLocatorKinds.Sheet, visibleIndex, name)));
        }

        if (blocks.Count == 0)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.Empty);
        }

        return DocumentExtractionOutcome.Extracted(
            new DocumentExtractionArtifact(DocumentTextSources.Spreadsheet, blocks, null));
    }

    // ---- cells --------------------------------------------------------------

    /// One cell's text, by type.
    ///
    /// A formula cell renders as its stored result plus its expression, in that
    /// order, because the result is what somebody asking "what is the forecast"
    /// wants and the expression is the provenance. The expression is bounded:
    /// it is author-controlled and can be enormous.
    private static string CellText(
        Cell cell, IReadOnlyList<string> sharedStrings, DocumentExtractionOptions options)
    {
        var raw = cell.CellValue?.InnerText;
        var type = cell.DataType?.Value;

        // if/else rather than a switch expression: in Open XML SDK 3.x
        // `CellValues` is an enum-like struct whose members are not compile-time
        // constants, so they cannot appear as patterns.
        string value;
        if (type is not null && type.Value == CellValues.SharedString
            && int.TryParse(raw, out var index))
        {
            value = index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : string.Empty;
        }
        else if (type is not null && type.Value == CellValues.InlineString)
        {
            value = cell.InlineString?.Text?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
        }
        else if (type is not null && type.Value == CellValues.Boolean)
        {
            // Stored as "0"/"1"; a person reading their own sheet sees words.
            value = raw == "1" ? "VERO" : "FALSO";
        }
        else if (type is not null && type.Value == CellValues.Error)
        {
            // An error is a value the workbook holds — `#DIV/0!` is information
            // about the spreadsheet, not a parse failure.
            value = raw ?? string.Empty;
        }
        else
        {
            value = raw ?? string.Empty;
        }

        value = value.Trim();

        var formula = cell.CellFormula?.Text;
        if (string.IsNullOrWhiteSpace(formula)) return value;

        var expression = formula.Length > options.EffectiveMaxFormulaCharacters
            ? formula[..options.EffectiveMaxFormulaCharacters]
            : formula;

        // Never "=SUM(...) evaluates to 442500". The workbook stored the number;
        // NubArca did not compute it and must not imply otherwise.
        return value.Length == 0
            ? $"[formula: {expression}]"
            : $"{value} [formula: {expression}]";
    }

    /// The shared string table, read once and bounded.
    ///
    /// It is the single largest part of most workbooks and the easiest place for
    /// a hostile file to hide a very large allocation, so the entry count is
    /// capped by the non-empty cell bound: a table larger than the cells that
    /// could reference it is not a document.
    private static IReadOnlyList<string> SharedStrings(
        WorkbookPart workbookPart, DocumentExtractionOptions options)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null) return Array.Empty<string>();

        var max = options.EffectiveMaxWorkbookNonEmptyCells;
        var strings = new List<string>();
        foreach (var item in table.Elements<SharedStringItem>())
        {
            if (strings.Count >= max) break;
            strings.Add(item.InnerText);
        }

        return strings;
    }

    private static bool LooksLikeHeader(IReadOnlyList<(string Reference, string Value)> cells)
        => cells.Count > 1
           && cells.All(c => c.Value.Length > 0
                             && !double.TryParse(
                                 c.Value,
                                 System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out _));

    private static string RenderRow(
        IReadOnlyList<(string Reference, string Value)> cells, string[]? header, uint? rowIndex)
    {
        var parts = new List<string>(cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            var (reference, value) = cells[i];
            var label = header is not null && i < header.Length && header[i].Length > 0
                ? header[i]
                : reference + (rowIndex?.ToString() ?? string.Empty);

            parts.Add(label.Length == 0 ? value : $"{label}={value}");
        }

        return string.Join(" | ", parts);
    }

    /// The column letters from a cell reference like `C12`.
    private static string ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return string.Empty;
        var end = 0;
        while (end < reference.Length && char.IsLetter(reference[end])) end++;
        return reference[..end];
    }
}

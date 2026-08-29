using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace NubArca.Api.Ai.Documents;

/// Reading a Word document as its author would read it.
///
/// The guiding question throughout is "what does this document SAY", not "what
/// does this package contain". They differ in ways that matter: a paragraph
/// somebody deleted with track-changes on is still in the markup and is not part
/// of the document; a header repeated on ninety pages is one statement, not
/// ninety; a hyperlink's target is not text at all.
///
/// NO PAGE NUMBERS. Open XML does not describe pages — pagination is what Word's
/// layout engine produces from fonts, printer metrics and the page setup, and
/// two machines can genuinely disagree. Any number invented here would be wrong
/// for somebody, and a citation saying "page 7" that is not page 7 is worse than
/// a citation that says which section. The locator is the heading path, which is
/// stable, meaningful and actually derivable from the file.
public sealed class WordDocumentExtractionProvider : IDocumentExtractionProvider
{
    public DocumentFormatKind Format => DocumentFormatKind.WordOpenXml;

    public string ProfileKey => DocumentTextSources.WordProfileKey;

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
        catch (OpenXmlPackageException)
        {
            // Malformed markup that survived the structural probe. A verdict
            // about the content: the same bytes will be just as malformed next
            // week.
            return Task.FromResult(
                DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(
                DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid));
        }
        catch (FileFormatException)
        {
            return Task.FromResult(
                DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid));
        }
    }

    private static DocumentExtractionOutcome Extract(
        DocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Bytes.ToArray(), writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid);
        }

        var options = request.Options;
        var styles = HeadingStyles(document);
        var blocks = new List<ExtractedDocumentBlock>();
        var path = new List<string>();
        var ordinal = 0;
        var characters = 0;
        var paragraphs = 0;
        var section = 0;

        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                {
                    if (++paragraphs > options.EffectiveMaxDocxParagraphs)
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }

                    var text = ParagraphText(paragraph);
                    if (text.Length == 0) continue;

                    var level = HeadingLevel(paragraph, styles);
                    if (level is { } depth)
                    {
                        // A HEADING RESETS THE PATH BELOW IT. `Agreement ›
                        // Termination › Notice period` followed by a level-2
                        // heading drops `Notice period`, because the new section
                        // is a sibling of `Termination` and not a child of the
                        // paragraph that happened to come before it.
                        while (path.Count >= depth) path.RemoveAt(path.Count - 1);
                        path.Add(text);
                        section++;
                    }

                    // THE BOUND IS CHECKED WHERE CONTENT IS ADDED, not at the
                    // top of the loop. Breaking out on a full budget publishes
                    // the paragraphs read so far as the whole document; checking
                    // here refuses only when this block genuinely pushes the
                    // COMPLETE document past the ceiling, so a document that
                    // lands exactly on it still succeeds.
                    if (Exceeds(blocks.Count, characters, text.Length, options))
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }

                    var heading = path.Count == 0 ? null : string.Join(" › ", path);
                    characters += text.Length;
                    blocks.Add(new ExtractedDocumentBlock(
                        ++ordinal,
                        level is null ? DocumentBlockKinds.Body : DocumentBlockKinds.Heading,
                        text,
                        heading,
                        new DocumentLocator(
                            DocumentLocatorKinds.Section,
                            section == 0 ? null : section,
                            heading)));
                    break;
                }

                case Table table:
                {
                    var rendered = TableText(table, options, out var overflowed);
                    if (overflowed)
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }
                    if (rendered.Length == 0) continue;

                    if (Exceeds(blocks.Count, characters, rendered.Length, options))
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.DocumentTooComplex);
                    }

                    var heading = path.Count == 0 ? null : string.Join(" › ", path);
                    characters += rendered.Length;
                    blocks.Add(new ExtractedDocumentBlock(
                        ++ordinal,
                        DocumentBlockKinds.Table,
                        rendered,
                        heading,
                        new DocumentLocator(
                            DocumentLocatorKinds.Section,
                            section == 0 ? null : section,
                            heading)));
                    break;
                }
            }
        }

        // FOOTNOTES AND ENDNOTES, ONCE, AT THE END. They are part of what the
        // document says — a contract's exclusions frequently live there — and
        // they are not part of the flow, so appending them keeps the body's
        // reading order honest.
        if (!AppendNotes(document, blocks, ref ordinal, ref characters, options))
        {
            // NOTES THAT DO NOT FIT ARE A REFUSAL, NOT A SILENT OMISSION. A
            // contract's exclusions frequently live in its footnotes, so
            // dropping them and publishing the body as complete is the most
            // misleading outcome available.
            return DocumentExtractionOutcome.Rejected(
                DocumentExtractionReasons.DocumentTooComplex);
        }

        if (blocks.Count == 0 || characters < options.EffectiveMinimumCharacters)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.Empty);
        }

        return DocumentExtractionOutcome.Extracted(
            new DocumentExtractionArtifact(DocumentTextSources.Word, blocks, null));
    }

    // ---- text ---------------------------------------------------------------

    /// A paragraph's visible text.
    ///
    /// Deliberately NOT `paragraph.InnerText`, which is the tempting one-liner
    /// and is wrong: it returns deleted tracked-change text as though it were
    /// part of the document. Somebody who deleted a clause and sent the file for
    /// review has said that clause is gone, and answering a question from it
    /// would be quoting a document that does not exist.
    ///
    /// Inserted text IS included. It is the current reading of the document,
    /// which is what the author is showing the reader.
    private static string ParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        AppendRuns(paragraph, builder);
        return Normalize(builder.ToString());
    }

    private static void AppendRuns(OpenXmlElement element, StringBuilder builder)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                // Deleted content, and everything nested inside it, is skipped
                // whole. Descending would find the DeletedText and put it back.
                case DeletedRun:
                    continue;

                case Run run:
                    foreach (var piece in run.ChildElements)
                    {
                        switch (piece)
                        {
                            case Text text:
                                builder.Append(text.Text);
                                break;
                            case TabChar:
                                builder.Append('\t');
                                break;
                            case Break:
                                builder.Append('\n');
                                break;
                            // DeletedText inside a live run: same rule.
                            case DeletedText:
                                break;
                        }
                    }
                    break;

                // A hyperlink contributes its DISPLAY text. The target is a
                // relationship, and it is never resolved: a document asking to
                // be told what is at a URL is a request a parser must not
                // honour.
                case Hyperlink hyperlink:
                    AppendRuns(hyperlink, builder);
                    break;

                // Inserted runs and other containers: descend.
                case InsertedRun:
                case SimpleField:
                case BookmarkStart:
                case ProofError:
                    AppendRuns(child, builder);
                    break;

                default:
                    if (child.HasChildren) AppendRuns(child, builder);
                    break;
            }
        }
    }

    /// A table as rows, not as a bag of cells.
    ///
    /// One chunk per cell would produce thousands of two-word fragments with no
    /// context, each of which ranks terribly and cites nothing useful. A row
    /// keeps the relationship between a label and its value, which is the only
    /// reason the table exists.
    private static string TableText(
        Table table, DocumentExtractionOptions options, out bool overflowed)
    {
        overflowed = false;
        var builder = new StringBuilder();
        var cells = 0;

        foreach (var row in table.Elements<TableRow>())
        {
            var values = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                if (++cells > options.EffectiveMaxDocxTableCells)
                {
                    overflowed = true;
                    return string.Empty;
                }

                var cellBuilder = new StringBuilder();
                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    var text = ParagraphText(paragraph);
                    if (text.Length == 0) continue;
                    if (cellBuilder.Length > 0) cellBuilder.Append(' ');
                    cellBuilder.Append(text);
                }
                values.Add(cellBuilder.ToString());
            }

            if (values.All(string.IsNullOrWhiteSpace)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(string.Join(" | ", values));
        }

        return builder.ToString();
    }

    /// Appends footnotes and endnotes. Returns false when they would push the
    /// COMPLETE document past a bound — the caller refuses rather than publish a
    /// body whose notes quietly went missing.
    private static bool AppendNotes(
        WordprocessingDocument document, List<ExtractedDocumentBlock> blocks,
        ref int ordinal, ref int characters, DocumentExtractionOptions options)
    {
        var texts = new List<string>();

        foreach (var note in document.MainDocumentPart?.FootnotesPart?.Footnotes
                     ?.Elements<Footnote>() ?? Enumerable.Empty<Footnote>())
        {
            // Separator and continuation pseudo-notes are markup, not content.
            if (note.Type is not null && note.Type.Value != FootnoteEndnoteValues.Normal) continue;
            foreach (var paragraph in note.Elements<Paragraph>())
            {
                var text = ParagraphText(paragraph);
                if (text.Length > 0) texts.Add(text);
            }
        }

        foreach (var note in document.MainDocumentPart?.EndnotesPart?.Endnotes
                     ?.Elements<Endnote>() ?? Enumerable.Empty<Endnote>())
        {
            if (note.Type is not null && note.Type.Value != FootnoteEndnoteValues.Normal) continue;
            foreach (var paragraph in note.Elements<Paragraph>())
            {
                var text = ParagraphText(paragraph);
                if (text.Length > 0) texts.Add(text);
            }
        }

        if (texts.Count == 0) return true;

        var joined = string.Join("\n", texts);
        if (Exceeds(blocks.Count, characters, joined.Length, options)) return false;

        characters += joined.Length;
        blocks.Add(new ExtractedDocumentBlock(
            ++ordinal,
            DocumentBlockKinds.Notes,
            joined,
            "Note",
            new DocumentLocator(DocumentLocatorKinds.Section, null, "Note")));
        return true;
    }

    /// Would adding one more block of `length` characters carry the COMPLETE
    /// document past a completeness-critical bound?
    ///
    /// One predicate, used at every site that adds content, so the character and
    /// block ceilings cannot drift apart between paragraphs, tables and notes.
    private static bool Exceeds(
        int blockCount, int characters, int length, DocumentExtractionOptions options)
        => blockCount + 1 > options.EffectiveMaxChunks
           || (long)characters + length > options.EffectiveMaxCharacters;

    // ---- headings -----------------------------------------------------------

    /// Style id → heading depth, read from the document's own style definitions.
    ///
    /// The style ID is not reliable on its own: a document authored in Italian
    /// may name its styles `Titolo1`, and one round-tripped through another
    /// editor may use arbitrary ids. What IS reliable is the outline level the
    /// style declares, so that is what is read, with the English convention kept
    /// as a fallback for documents that declare nothing.
    private static Dictionary<string, int> HeadingStyles(WordprocessingDocument document)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = document.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null) return map;

        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            if (id is null) continue;

            var outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (outline is { } level && level < 9)
            {
                map[id] = level + 1;
            }
        }

        return map;
    }

    private static int? HeadingLevel(Paragraph paragraph, Dictionary<string, int> styles)
    {
        var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (id is null) return null;
        if (styles.TryGetValue(id, out var level)) return level;

        // No declared outline level: fall back to the convention.
        if (id.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id[7..], out var n) && n is >= 1 and <= 9)
        {
            return n;
        }

        return null;
    }

    private static string Normalize(string text)
    {
        var trimmed = text.Replace(' ', ' ').Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed;
    }
}

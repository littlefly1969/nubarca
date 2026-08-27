using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using System.Text;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace NubArca.Api.Ai.Documents;

/// A presentation, slide by slide.
///
/// The slide is the unit of meaning and the unit of citation: "slide 7" is
/// something a person can turn to, and a chunk spanning two slides describes a
/// place that does not exist. So a slide boundary is never crossed — a large
/// slide may become several chunks, and a small one stays whole.
///
/// SPEAKER NOTES ARE INGESTED. They are part of the owner's private
/// presentation and frequently carry the actual explanation: the slide says
/// "Q3 Launch" and the notes say why the date moved. Excluding them would drop
/// the most useful sentence in the file. They are marked as notes so a citation
/// can say where the answer came from, since a person looking at slide 7 will
/// not see them on it.
///
/// HIDDEN SLIDES ARE NOT. A hidden slide is material the author removed from
/// what they present — a backup figure, an old number, a rehearsal note — and
/// answering from it would surface something they took out of view.
public sealed class PresentationExtractionProvider : IDocumentExtractionProvider
{
    public DocumentFormatKind Format => DocumentFormatKind.PresentationOpenXml;

    public string ProfileKey => DocumentTextSources.PresentationProfileKey;

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
        using var document = PresentationDocument.Open(stream, isEditable: false);

        var presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<SlideId>().ToList();
        if (presentationPart is null || slideIds is null)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid);
        }

        var options = request.Options;
        if (slideIds.Count > options.EffectiveMaxPresentationSlides)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.DocumentTooComplex);
        }

        var blocks = new List<ExtractedDocumentBlock>();
        var ordinal = 0;
        var number = 0;

        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = slideId.RelationshipId?.Value;
            if (id is null) continue;
            if (presentationPart.GetPartById(id) is not SlidePart slidePart) continue;

            number++;

            // `Show` absent means shown. Only an explicit false hides a slide,
            // so the default must not be read as hidden.
            if (slidePart.Slide?.Show is { } show && !show.Value) continue;

            var title = SlideTitle(slidePart);
            var body = SlideText(slidePart, options);
            var notes = NotesText(slidePart, options);

            var locator = new DocumentLocator(DocumentLocatorKinds.Slide, number, title);

            if (body.Length > 0)
            {
                blocks.Add(new ExtractedDocumentBlock(
                    ++ordinal, DocumentBlockKinds.Body, body, title, locator));
            }

            if (notes.Length > 0)
            {
                // A SEPARATE BLOCK, deliberately. Notes and slide body answer
                // different questions and a citation should be able to say which
                // one it came from; merging them would make "the presentation
                // says" cover something the audience never saw.
                blocks.Add(new ExtractedDocumentBlock(
                    ++ordinal, DocumentBlockKinds.Notes, notes, title, locator));
            }
        }

        if (blocks.Count == 0)
        {
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.Empty);
        }

        return DocumentExtractionOutcome.Extracted(
            new DocumentExtractionArtifact(DocumentTextSources.Presentation, blocks, null));
    }

    // ---- one slide ----------------------------------------------------------

    /// The slide's title, from the shape the layout designates as the title.
    ///
    /// Read from the placeholder type rather than "the first text on the slide",
    /// which is a guess that goes wrong on any slide whose title is positioned
    /// below something else.
    private static string? SlideTitle(SlidePart slidePart)
    {
        var shapes = slidePart.Slide?.CommonSlideData?.ShapeTree?.Elements<P.Shape>();
        if (shapes is null) return null;

        foreach (var shape in shapes)
        {
            var placeholder = shape.NonVisualShapeProperties
                ?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            if (placeholder?.Type is null) continue;

            var type = placeholder.Type.Value;
            if (type != PlaceholderValues.Title && type != PlaceholderValues.CenteredTitle) continue;

            var text = ShapeText(shape);
            if (text.Length > 0) return text;
        }

        return null;
    }

    private static string SlideText(SlidePart slidePart, DocumentExtractionOptions options)
    {
        var builder = new StringBuilder();
        var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        if (tree is null) return string.Empty;

        foreach (var element in tree.ChildElements)
        {
            if (builder.Length >= options.EffectiveMaxSlideTextCharacters) break;

            switch (element)
            {
                case P.Shape shape:
                {
                    var text = ShapeText(shape);
                    // ALT TEXT counts as content: a description an author wrote
                    // for an image is text they wrote about their own document.
                    var alt = shape.NonVisualShapeProperties
                        ?.NonVisualDrawingProperties?.Description?.Value;
                    Append(builder, text);
                    Append(builder, alt);
                    break;
                }

                case P.Picture picture:
                {
                    // The image itself is NOT read. No OCR of embedded media in
                    // this slice; its alt text is the author's own words.
                    var alt = picture.NonVisualPictureProperties
                        ?.NonVisualDrawingProperties?.Description?.Value;
                    Append(builder, alt);
                    break;
                }

                case P.GraphicFrame frame:
                {
                    // Tables, and chart TITLES only. A chart's data series are
                    // numbers in a cached workbook; rendering them as text would
                    // be reporting figures nobody wrote in the presentation.
                    foreach (var table in frame.Descendants<D.Table>())
                    {
                        Append(builder, TableText(table));
                    }
                    break;
                }
            }
        }

        var result = builder.ToString().Trim();
        return result.Length > options.EffectiveMaxSlideTextCharacters
            ? result[..options.EffectiveMaxSlideTextCharacters]
            : result;
    }

    private static string NotesText(SlidePart slidePart, DocumentExtractionOptions options)
    {
        var notes = slidePart.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (notes is null) return string.Empty;

        var builder = new StringBuilder();
        foreach (var shape in notes.Elements<P.Shape>())
        {
            // The slide-image placeholder inside a notes page carries a copy of
            // the slide number, not content.
            var placeholder = shape.NonVisualShapeProperties
                ?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            if (placeholder?.Type is not null
                && (placeholder.Type.Value == PlaceholderValues.SlideImage
                    || placeholder.Type.Value == PlaceholderValues.SlideNumber))
            {
                continue;
            }

            Append(builder, ShapeText(shape));
            if (builder.Length >= options.EffectiveMaxSlideTextCharacters) break;
        }

        var result = builder.ToString().Trim();
        return result.Length > options.EffectiveMaxSlideTextCharacters
            ? result[..options.EffectiveMaxSlideTextCharacters]
            : result;
    }

    /// A shape's paragraphs, one per line, in document order — which is bullet
    /// order, and is the order the author chose.
    private static string ShapeText(P.Shape shape)
    {
        var body = shape.TextBody;
        if (body is null) return string.Empty;

        var builder = new StringBuilder();
        foreach (var paragraph in body.Elements<D.Paragraph>())
        {
            var line = new StringBuilder();
            foreach (var run in paragraph.Elements<D.Run>())
            {
                line.Append(run.Text?.Text);
            }
            foreach (var br in paragraph.Elements<D.Break>())
            {
                line.Append('\n');
            }

            var text = line.ToString().Trim();
            if (text.Length == 0) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string TableText(D.Table table)
    {
        var builder = new StringBuilder();
        foreach (var row in table.Elements<D.TableRow>())
        {
            var values = new List<string>();
            foreach (var cell in row.Elements<D.TableCell>())
            {
                var cellText = new StringBuilder();
                foreach (var paragraph in cell.TextBody?.Elements<D.Paragraph>()
                                          ?? Enumerable.Empty<D.Paragraph>())
                {
                    foreach (var run in paragraph.Elements<D.Run>())
                    {
                        cellText.Append(run.Text?.Text);
                    }
                }
                values.Add(cellText.ToString().Trim());
            }

            if (values.All(string.IsNullOrWhiteSpace)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(string.Join(" | ", values));
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(text.Trim());
    }
}

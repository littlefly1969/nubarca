using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace NubArca.Api.Ai.Documents;

/// A PDF, page by page, with recognition only where reading fails.
///
/// Two different jobs live behind one profile, and keeping them one profile is
/// deliberate: "the PDF pipeline" is the interpretation, and whether a given
/// document happened to need OCR is a property of that document rather than of
/// the reading. A text-native PDF completing without the engine ever starting is
/// the normal case, not a different lineage.
///
/// NEVER ACROSS A PAGE. A chunk spanning two pages cites a place that does not
/// exist, and the page is the only location a PDF actually has.
///
/// AN INCOMPLETE DOCUMENT IS NOT PUBLISHED AS COMPLETE. If a page needs
/// recognition and recognition is unavailable, the whole extraction is
/// unavailable — retryable, not a verdict — rather than a document quietly
/// missing its scanned pages while looking finished. A genuinely blank page is a
/// different thing and contributes nothing without being an error.
public sealed class PdfExtractionProvider : IDocumentExtractionProvider
{
    private readonly PdfPageRenderer _renderer;
    private readonly IDocumentOcrProvider _ocr;
    private readonly ILogger<PdfExtractionProvider> _log;

    public PdfExtractionProvider(
        PdfPageRenderer renderer, IDocumentOcrProvider ocr, ILogger<PdfExtractionProvider> log)
    {
        _renderer = renderer;
        _ocr = ocr;
        _log = log;
    }

    public DocumentFormatKind Format => DocumentFormatKind.Pdf;

    public string ProfileKey => DocumentTextSources.PdfProfileKey;

    public async Task<DocumentExtractionOutcome> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = request.Options;

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(request.Bytes.ToArray());
        }
        catch (PdfDocumentEncryptedException)
        {
            // Password-protected. Nothing to extract without a credential
            // NubArca does not have and must never ask for.
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.EncryptedDocument);
        }
        catch (Exception)
        {
            // PdfPig is pre-1.0 and has open issues on malformed input, so its
            // exceptions are treated as fallible rather than authoritative — and
            // sanitized, because the message can carry a path.
            return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.OfficePackageInvalid);
        }

        using (document)
        {
            if (document.NumberOfPages > options.EffectiveMaxPdfPages)
            {
                return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.PdfTooManyPages);
            }

            var blocks = new List<ExtractedDocumentBlock>();
            var ordinal = 0;
            var characters = 0;
            var ocrPages = 0;

            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (characters >= options.EffectiveMaxCharacters) break;

                string native;
                try
                {
                    var page = document.GetPage(number);
                    // Content-order extraction: PdfPig recommends it for
                    // indexing, and reading order is what a chunk should follow.
                    native = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                }
                catch (Exception)
                {
                    native = string.Empty;
                }

                var text = native;

                if (!PdfTextQuality.IsUsable(native))
                {
                    // A page whose text is missing or garbage. Recognition is
                    // the only way to read it — but a blank page is not one of
                    // these, and asking the engine to read nothing would waste
                    // a rendering per empty page.
                    if (++ocrPages > options.EffectiveMaxOcrPages)
                    {
                        return DocumentExtractionOutcome.Rejected(
                            DocumentExtractionReasons.PdfTooManyPages);
                    }

                    var recognized = await RecognizeAsync(
                        request, number, options, cancellationToken);

                    if (recognized.Reason is { } reason)
                    {
                        // UNAVAILABLE, NOT REJECTED. The document is readable in
                        // principle; this installation could not read it right
                        // now. Publishing what was extracted would claim the
                        // scanned pages were empty.
                        return DocumentExtractionOutcome.Unavailable(reason);
                    }

                    text = recognized.Text ?? string.Empty;
                }

                text = text.Trim();
                if (text.Length == 0) continue;

                if (characters + text.Length > options.EffectiveMaxCharacters)
                {
                    text = text[..Math.Max(0, options.EffectiveMaxCharacters - characters)];
                }
                if (text.Length == 0) break;

                characters += text.Length;
                blocks.Add(new ExtractedDocumentBlock(
                    ++ordinal,
                    DocumentBlockKinds.Body,
                    text,
                    null,
                    // PAGE IN BOTH FIELDS, and this is the only format where
                    // that is right: `Page` means a real PDF page and the
                    // locator index is the same number.
                    new DocumentLocator(DocumentLocatorKinds.Page, number, null, number)));
            }

            if (blocks.Count == 0 || characters < options.EffectiveMinimumCharacters)
            {
                return DocumentExtractionOutcome.Rejected(DocumentExtractionReasons.Empty);
            }

            _log.LogInformation(
                "document extraction: format=pdf pages={Pages} ocr_pages={OcrPages} chars={Characters}",
                document.NumberOfPages, ocrPages, characters);

            return DocumentExtractionOutcome.Extracted(
                new DocumentExtractionArtifact(DocumentTextSources.Pdf, blocks, null));
        }
    }

    private async Task<(string? Text, string? Reason)> RecognizeAsync(
        DocumentExtractionRequest request, int pageNumber,
        DocumentExtractionOptions options, CancellationToken cancellationToken)
    {
        var readiness = _ocr.CheckReadiness();
        if (!readiness.IsReady) return (null, readiness.Reason);

        var (png, renderReason) = await _renderer.RenderAsync(
            request.Bytes, pageNumber - 1, cancellationToken);
        if (renderReason is not null) return (null, renderReason);

        var result = await _ocr.RecognizeAsync(
            png!,
            new OcrPageRequest(
                TesseractOcrProvider.LanguageArgument(options),
                options.EffectiveOcrPageTimeoutSeconds,
                options.EffectiveMaxOcrCharactersPerPage),
            cancellationToken);

        return result.Ok ? (result.Text, null) : (null, result.Reason);
    }
}

/// IS THIS PAGE'S TEXT WORTH HAVING?
///
/// `page.Text.Length > 0` is the tempting test and it is wrong in both
/// directions. A scanned page often carries a few stray characters from a
/// header stamp, which passes a length check while containing none of the
/// document; and a page of broken font encodings produces plenty of text made
/// entirely of replacement characters and control codes.
///
/// So the decision is a bounded, deterministic heuristic over what the
/// characters ARE. It is not a claim about meaning and it does not need to be:
/// its only job is choosing between reading and recognising, and the cost of
/// being wrong is one rendered page.
public static class PdfTextQuality
{
    /// Below this, a page has not produced enough to judge — and not enough to
    /// be worth retrieving either.
    public const int MinimumCharacters = 16;

    /// The share of characters that must be ordinary text. Broken encodings
    /// produce long runs of control and replacement characters that clear a
    /// length check easily.
    public const double MinimumLegibleRatio = 0.6;

    /// One character repeated past this share is a rendering artefact, not
    /// prose — the classic symptom of a font that could not be decoded.
    public const double MaximumRepeatRatio = 0.5;

    public static bool IsUsable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length < MinimumCharacters) return false;

        var legible = 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in trimmed)
        {
            if (c == '�') continue;
            if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c)
                || char.IsSymbol(c))
            {
                legible++;
            }

            if (char.IsWhiteSpace(c)) continue;
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        if ((double)legible / trimmed.Length < MinimumLegibleRatio) return false;

        if (counts.Count > 0)
        {
            var nonSpace = counts.Values.Sum();
            var dominant = counts.Values.Max();
            if (nonSpace > 0 && (double)dominant / nonSpace > MaximumRepeatRatio) return false;
        }

        // A page whose "text" has no letters at all is page furniture — a number
        // and some rules — rather than content.
        return trimmed.Any(char.IsLetter);
    }
}

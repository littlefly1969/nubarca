namespace NubArca.Api.Ai.Documents;

/// The native-text reading, behind the seam.
///
/// This provider adds nothing. It is the Slice-3 decoder, unchanged, wearing the
/// interface every richer parser will wear — and moving it here first is the
/// whole point of doing it before any of them exist. If the seam were introduced
/// alongside a new format, a regression in the native path and a defect in the
/// new parser would arrive in the same commit and look identical from the test
/// output. Done in this order, the native suite is a control: it passes before
/// and after, or the seam is wrong.
///
/// It produces exactly one block. A plain text file has no interior geography —
/// no pages, no sheets, no slides — so its locator is `text` with no index, and
/// the heading structure a Markdown document carries is recovered later by the
/// chunker, which already knows how to read it. Inventing section blocks here
/// would duplicate that with a second, worse implementation.
public sealed class NativeTextExtractionProvider : IDocumentExtractionProvider
{
    public DocumentFormatKind Format => DocumentFormatKind.NativeText;

    public string ProfileKey => DocumentTextSources.NativeProfileKey;

    public Task<DocumentExtractionOutcome> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var extraction = NativeTextExtractor.Extract(
            request.CanonicalMimeType, request.Bytes.Span, request.Options);

        if (!extraction.Ok)
        {
            // EVERY native refusal is a content verdict. Unsupported type,
            // binary bytes, malformed UTF-8, too large, empty — each is a
            // statement about these bytes that will be just as true next week,
            // so it is recorded against the blob rather than retried forever.
            // Nothing in this path depends on a binary, a model or a network,
            // so there is no environment failure it can produce.
            return Task.FromResult(DocumentExtractionOutcome.Rejected(extraction.Reason!));
        }

        var block = new ExtractedDocumentBlock(
            Ordinal: 1,
            Kind: DocumentBlockKinds.Body,
            Text: extraction.Text!,
            Heading: null,
            Locator: DocumentLocator.None);

        return Task.FromResult(DocumentExtractionOutcome.Extracted(
            new DocumentExtractionArtifact(
                DocumentTextSources.Native,
                new[] { block },
                extraction.Language)));
    }
}

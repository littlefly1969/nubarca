namespace NubArca.Api.Ai.Documents;

/// Whether OCR can run here, right now.
public sealed record OcrReadiness(bool IsReady, string? Reason)
{
    public static readonly OcrReadiness Ready = new(true, null);
    public static OcrReadiness NotReady(string reason) => new(false, reason);
}

/// What one page's recognition produced, or the sanitized reason it did not.
public sealed record OcrPageResult(string? Text, string? Reason)
{
    public static OcrPageResult Recognized(string text) => new(text, null);
    public static OcrPageResult Failed(string reason) => new(null, reason);

    public bool Ok => Text is not null;
}

/// One page, described only as much as recognition needs.
public sealed record OcrPageRequest(string Language, int TimeoutSeconds, int MaxCharacters);

/// LOCAL RECOGNITION, behind a seam.
///
/// The seam is here so the baseline can be replaced. Tesseract is a mature,
/// local, open-source engine and an appropriate BASELINE; it is not a claim
/// about NubArca's final OCR quality. A stronger local document model can enter
/// through this interface later as another profile without touching owner
/// authorization, `DocumentText`, RAG trust or Assistant policy — which is the
/// only reason the boundary is worth drawing now.
///
/// What the provider is given is a page IMAGE and a language. What it is not
/// given is the entire point: no owner id, no storage key, no filesystem path,
/// no database, no Assistant runtime, no URL. A component that cannot identify a
/// person cannot leak one, and one that has no URL cannot be talked into
/// fetching anything.
public interface IDocumentOcrProvider
{
    string Provider { get; }

    OcrReadiness CheckReadiness();

    Task<OcrPageResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes,
        OcrPageRequest request,
        CancellationToken cancellationToken = default);
}

namespace NubArca.Api.Ai.DocumentVisual;

/// What kind of surface a rendered unit is.
///
/// Closed, and read only by diagnostics and the provenance rule — retrieval
/// does not branch on it. Its job is to make "can this unit's ordinal be quoted
/// as a source location" answerable without joining back to the file to find
/// out what it was.
public static class DocumentVisualRenderKinds
{
    /// A real page of a real PDF. The only kind whose ordinal is also a
    /// document coordinate.
    public const string PdfPage = "pdf-page";

    /// A sheet of NubArca's own deterministic text canvas. It has no
    /// counterpart in the source document — a Markdown file has no pages — so
    /// its ordinal is a rendering artefact and nothing else.
    public const string TextCanvasSheet = "text-canvas-sheet";

    /// A page produced by laying an Office document out through a layout engine.
    /// Looks exactly like a PDF page and is NOT one: the pagination is the
    /// engine's, so the same DOCX under a different LibreOffice build puts its
    /// table somewhere else. Never a citation.
    public const string OfficeRenderedPage = "office-rendered-page";
}

/// How `DocumentVisualEmbedding.EmbeddingBytes` is laid out. Closed, because
/// the value decides how the bytes are decoded and a wrong decode is a wrong
/// number rather than an error.
public static class DocumentVisualEmbeddingLayouts
{
    /// Exactly one vector. Cosine against the query vector.
    public const string Dense = "dense";

    /// A sequence of vectors. MaxSim against the query's own sequence.
    public const string LateInteraction = "late-interaction";

    public static bool IsKnown(string? layout)
        => layout is Dense or LateInteraction;
}

/// RENDER IDENTITY — the token that says how a set of pixels was produced.
///
/// It changes when the renderer engine changes, when its pagination or layout
/// implementation changes, or when the bundled fonts change, because each of
/// those makes the same document produce different pixels and therefore
/// different vectors. It is deliberately NOT a timestamp and not a build SHA: a
/// value that changes on every release would re-render every document in every
/// library for a release that touched none of this, and a value derived from
/// "when" cannot tell an operator what actually differs.
///
/// A stored index whose RenderProfileKey is not the active one for its format
/// is unreachable — the same instant-invalidation the blob id gives for content.
public static class DocumentVisualRenderProfiles
{
    /// PDFium page rasterisation, at the pixel bound this slice configures.
    public const string PdfiumPage = "pdfium-page-render-v1";

    /// NubArca's own text canvas: a fixed font, fixed margins, deterministic
    /// wrapping and fixed sheet dimensions. Bump when any of those change.
    public const string TextCanvas = "nubarca-text-canvas-v1";

    /// LibreOffice headless → PDF → PDFium, inside the isolated renderer worker.
    /// Bumping this is what a LibreOffice upgrade costs.
    public const string LibreOfficePdf = "libreoffice-office-pdf-v1";

    public static bool IsKnown(string? key)
        => key is PdfiumPage or TextCanvas or LibreOfficePdf;
}

/// Sanitized reasons the visual path produced nothing.
///
/// Split the way extraction splits them, and for the same reason: a CONTENT
/// verdict is permanent and may be recorded against these bytes, while an
/// ENVIRONMENT state is about this installation right now and must never be
/// written as a verdict. A renderer worker that is not deployed marking every
/// DOCX in every library permanently unrenderable is a configuration mistake
/// turning into data loss.
///
/// Nothing here ever overwrites a text extraction's state. The two derivatives
/// fail independently: a document whose visual pass fails is still fully
/// answerable from its text.
public static class DocumentVisualReasons
{
    // ---- permanent: a verdict about the content -----------------------------

    /// No renderer claims this format. A photo library is mostly photos.
    public const string FormatUnsupported = "visual-format-unsupported";

    /// Past a completeness-critical structural bound — too many pages, too many
    /// total pixels. Refused whole, never rendered up to the bound.
    public const string DocumentTooComplex = "visual-document-too-complex";

    /// A single rendered unit exceeded its byte or pixel ceiling.
    public const string OutputTooLarge = "visual-output-too-large";

    /// The bytes are not the document they claimed to be.
    public const string InvalidSource = "visual-invalid-source";

    // ---- retryable: a statement about this installation ---------------------

    /// The renderer is not deployed, not built, or not reachable.
    public const string RendererUnavailable = "visual-renderer-unavailable";

    /// The renderer exceeded its time bound and was killed.
    public const string RenderTimeout = "visual-render-timeout";

    /// The renderer process failed for a reason that is not about the document.
    public const string RenderProcessFailed = "visual-render-process-failed";

    /// No visual embedding profile is configured, enabled, or has its model on
    /// disk. Never a content verdict — see the AI substrate rules.
    public const string ModelUnavailable = "visual-model-unavailable";

    public const string ModelTimeout = "visual-model-timeout";

    /// The visual capability is switched off. The ordinary state of a fresh
    /// installation, and not a failure of anything.
    public const string Disabled = "visual-disabled";

    /// This owner's corpus is past the ceiling the exact-search fallback can
    /// rank without a pgvector accelerator. Visual retrieval reports itself
    /// unavailable; nothing is truncated, and text retrieval is unaffected.
    public const string CorpusTooLarge = "visual-corpus-too-large";

    /// The model produced more vectors, or a different dimension, than the
    /// declared layout allows. The profile is refused; vectors are never
    /// truncated to fit a column.
    public const string ModelOutputUnsupported = "visual-model-output-unsupported";

    /// Whether this reason is a permanent verdict about the document's content.
    /// The indexer uses it to decide between recording a skip and leaving the
    /// document for the next pass.
    public static bool IsPermanent(string? reason) => reason is
        FormatUnsupported or DocumentTooComplex or OutputTooLarge or InvalidSource;
}

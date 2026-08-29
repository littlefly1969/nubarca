namespace NubArca.Api.Domain.Ai;

/// ONE RENDERED VISUAL UNIT of a document — a page, a slide, a canvas sheet.
///
/// What it is NOT is the point: it holds no image. Rendering a private document
/// produces pixels that go straight into an encoder and are dropped; persisting
/// them would turn "search my documents by how they look" into a second copy of
/// everybody's paperwork sitting in a cache directory, with its own backup,
/// deletion and share-boundary problems, in exchange for nothing retrieval
/// needs. `PixelHash` is kept so a rebuild can prove determinism without the
/// bytes it hashes.
///
/// A unit is also NOT a citation. `Ordinal` is where the RENDERER put this
/// content, which for a DOCX or an XLSX is an implementation detail of
/// LibreOffice's pagination — the same document repaginated by a different
/// build lands its table on a different page. The authority for "where in the
/// document did this come from" stays Slice 4's typed text provenance, which is
/// derived from the document's own structure. `SourceLocator*` below is carried
/// only where the format makes it exact (a PDF page is a PDF page), and is
/// deliberately null where it would be invented.
public class DocumentVisualUnit
{
    public Guid Id { get; set; }

    public Guid DocumentVisualIndexId { get; set; }

    /// Position in the rendered sequence, 0-based and dense. Rendering order,
    /// not document geography.
    public int Ordinal { get; set; }

    /// What kind of surface this is (see DocumentVisualRenderKinds): a real PDF
    /// page, a text canvas sheet, an office page rendered through a layout
    /// engine. Retrieval does not branch on it; diagnostics and the provenance
    /// rule do.
    public string RenderKind { get; set; } = string.Empty;

    /// The document's OWN coordinates for this unit, in the same typed shape as
    /// `DocumentChunk.LocatorKind/Index/Label` — and populated only when the
    /// renderer can state them exactly. PDF can (page N is page N). Office
    /// rendered through a layout engine cannot, so these stay null rather than
    /// carrying a page number that means "wherever LibreOffice broke it".
    public string? SourceLocatorKind { get; set; }
    public int? SourceLocatorIndex { get; set; }
    public string? SourceLocatorLabel { get; set; }

    /// A REAL PDF PAGE, 1-based, and nothing else — the same rule
    /// `DocumentChunk.Page` follows, for the same reason. Null for every format
    /// that has no pages of its own.
    public int? SourcePage { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// SHA-256 of the rendered pixels, hex. The determinism check for the text
    /// canvas and the only trace of an image that is not kept: two renders of
    /// the same bytes under the same render profile must hash identically, and a
    /// test can assert that without a golden PNG in the repository.
    ///
    /// Not a content hash of the SOURCE, and never exposed.
    public string PixelHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

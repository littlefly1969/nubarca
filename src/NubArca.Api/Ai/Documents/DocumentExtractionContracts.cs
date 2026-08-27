namespace NubArca.Api.Ai.Documents;

/// WHERE a block sits inside its own document, in that document's own units.
///
/// Typed rather than a formatted string because two readers need it and a string
/// serves only one: the citation builder, which turns it into something a person
/// recognises, and a future visual derivative that has to point at the same
/// page/slide/sheet without parsing "Slide 7 — Launch plan" back into structure.
///
/// `Page` is separate from `Index` and stays a real PDF page. A single field
/// meaning "page-like thing" is a field with no meaning: a later reader cannot
/// interpret it without joining back to discover the format, and the one that
/// forgets renders "Page 4" for a spreadsheet.
public sealed record DocumentLocator(
    string Kind,
    int? Index = null,
    string? Label = null,
    int? Page = null)
{
    /// Native text has no interior geography worth recording. A plain file is
    /// one stream of characters, and inventing a section number for it would put
    /// a made-up value where a null is the honest answer.
    public static readonly DocumentLocator None = new(DocumentLocatorKinds.Text);
}

/// The closed vocabulary of locator kinds.
public static class DocumentLocatorKinds
{
    public const string Text = "text";
    public const string Page = "page";
    public const string Section = "section";
    public const string Sheet = "sheet";
    public const string Slide = "slide";
}

/// What KIND of content a block is, where the distinction survives into
/// retrieval. Speaker notes are the case that earns this: they are part of the
/// owner's presentation and often the most useful explanation of a slide, and
/// they are not what the slide displays.
public static class DocumentBlockKinds
{
    public const string Body = "body";
    public const string Heading = "heading";
    public const string Table = "table";
    public const string Notes = "notes";
}

/// One ordered piece of a document, as its parser saw it.
///
/// Blocks rather than one mega-string, because everything downstream needs the
/// structure that flattening destroys: a chunker that must not cross a slide
/// boundary, a citation that says which sheet, a future derivative that renders
/// one page. Reconstructing any of that from a joined string is guesswork.
public sealed record ExtractedDocumentBlock(
    int Ordinal,
    string Kind,
    string Text,
    string? Heading,
    DocumentLocator Locator);

/// Everything one parser produced from one document.
public sealed record DocumentExtractionArtifact(
    string Source,
    IReadOnlyList<ExtractedDocumentBlock> Blocks,
    string? Language)
{
    public static DocumentExtractionArtifact Empty(string source)
        => new(source, Array.Empty<ExtractedDocumentBlock>(), null);
}

/// A parser's answer: blocks, or a sanitized reason there are none.
///
/// The reason's CLASS matters as much as its value, which is why refusals are
/// not one shape. A content verdict is permanent — the same bytes earn the same
/// answer next week, so it is recorded against the blob and not attempted again.
/// An environment failure is temporary and must never be written as a verdict: a
/// missing OCR binary marking somebody's documents permanently unreadable is a
/// configuration mistake becoming data loss.
public sealed record DocumentExtractionOutcome(
    DocumentExtractionArtifact? Artifact,
    string? Reason,
    bool IsPermanent)
{
    public static DocumentExtractionOutcome Extracted(DocumentExtractionArtifact artifact)
        => new(artifact, null, false);

    /// A verdict about the CONTENT. Recorded against these bytes.
    public static DocumentExtractionOutcome Rejected(string reason)
        => new(null, reason, true);

    /// A statement about this INSTALLATION, right now. Never recorded as a
    /// verdict; the next pass tries again.
    public static DocumentExtractionOutcome Unavailable(string reason)
        => new(null, reason, false);

    public bool Ok => Artifact is not null;
}

/// What a parser is given.
///
/// Notice what is absent. No owner id: parsing does not need to know whose
/// document this is, and a component that cannot identify a person cannot leak
/// one. No storage key and no path: the bytes arrive as bytes, so there is
/// nothing for a parser bug to turn into a filesystem read. No database, no HTTP
/// client, no model.
///
/// `FileName` is present because some formats genuinely need the extension to
/// disambiguate, and because a citation names the document — but it is passed as
/// data, never used to open anything.
public sealed record DocumentExtractionRequest(
    ReadOnlyMemory<byte> Bytes,
    string? FileName,
    string CanonicalMimeType,
    DocumentExtractionOptions Options);

/// One way of reading one family of document.
///
/// The seam exists so that adding PDF, Word, Excel and PowerPoint does not mean
/// adding four more branches to the indexer, and so that a later, better parser
/// — a stronger local OCR model, a different PDF library — enters here without
/// touching owner authorization, `DocumentText`, RAG trust or Assistant policy.
///
/// What an implementation may NOT do is the interesting half:
///
///  - it does not write `DocumentText` or `DocumentChunk`;
///  - it does not call embeddings, retrieval or the Assistant;
///  - it does not make authorization decisions — it is handed bytes only after
///    live owner eligibility and the source-size bound have both passed;
///  - it does not reach the network, for any reason, including an external
///    relationship a document asks it to follow;
///  - it does not execute anything the document contains.
///
/// Parsing means reading visible document information. It never means executing
/// document behaviour.
public interface IDocumentExtractionProvider
{
    DocumentFormatKind Format { get; }

    /// The extraction profile key this provider's output is recorded under. Two
    /// providers must not share one: changing how spreadsheets are read should
    /// not force every Word document to be extracted again.
    string ProfileKey { get; }

    Task<DocumentExtractionOutcome> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken = default);
}

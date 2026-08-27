namespace NubArca.Api.Domain.Ai;

// Owner/file-scoped chunk of an extracted DocumentText, for semantic search.
// Keyed uniquely by (document text, profile, ordinal). `Text` is internal-only.
// Phase 0A defines the shape only; nothing chunks anything yet.
public class DocumentChunk
{
    public Guid Id { get; set; }

    public Guid DocumentTextId { get; set; }

    // Explicit owner scope, denormalized for owner-scoped queries/isolation.
    public Guid OwnerUserId { get; set; }

    // The extraction profile that produced this chunking.
    public Guid ProfileId { get; set; }

    // Position of the chunk within the document.
    public int Ordinal { get; set; }

    // The section this chunk came from, as a heading trail
    // ("Manutenzione › Pulizia filtro"). Two jobs: ranking weights it well above
    // body text, because a private document's section titles are usually what it
    // is about; and it is the only part of a chunk that is safe to show as a
    // CITATION next to the filename, since a heading is a label rather than
    // content.
    public string? Heading { get; set; }

    // Chunk text. INTERNAL ONLY — never serialized to a normal DTO.
    public string? Text { get; set; }

    public string? TextHash { get; set; }

    public int? StartOffset { get; set; }
    public int? EndOffset { get; set; }

    // A REAL PDF PAGE, and nothing else.
    //
    // The tempting shortcut is to let this mean "page-like thing" and store a
    // slide number, a sheet ordinal or a Word pseudo-page in it. That would make
    // `Page` a field whose meaning depends on a format recorded elsewhere, which
    // is the same as having no meaning: a later reader — a citation builder, a
    // visual derivative, a diagnostic query — cannot interpret it without
    // joining back to find out what kind of document it came from, and the one
    // that forgets produces a confident nonsense like "Page 4" for a
    // spreadsheet. Word is the sharpest case: Open XML markup does not define
    // the pages Word renders, so any number here would be invented. Null is the
    // honest value for every format that has no pages.
    public int? Page { get; set; }

    // WHERE IN ITS OWN DOCUMENT this chunk is, in that document's own units.
    //
    // Typed rather than a formatted string, because two different readers need
    // it: the citation builder, which turns it into something a person
    // recognises, and a future visual derivative that has to point at the same
    // page/slide/sheet without parsing a citation back into structure. A string
    // like "Slide 7 — Launch plan" serves the first and defeats the second.
    //
    //   format        Kind        Index              Label
    //   native text   text/null   null               null
    //   PDF           page        1-based page       null
    //   DOCX          section     section ordinal    heading path
    //   XLSX          sheet       sheet ordinal      sheet name
    //   PPTX          slide       1-based slide      slide title
    public string? LocatorKind { get; set; }
    public int? LocatorIndex { get; set; }
    public string? LocatorLabel { get; set; }

    public int? TokenCount { get; set; }

    public DateTime CreatedAt { get; set; }
}

using NubArca.Api.Rag.Chunking;

namespace NubArca.Api.Ai.Documents;

/// The version of NubArca's reading of a PRIVATE document.
///
/// Its own number, not `RagIndexFormat`. The two happen to share a chunker
/// today, and that is not a reason to share a version: bumping the system one
/// re-chunks and re-embeds the whole repository corpus, and a private corpus
/// belongs to people who should not pay for a change that did not affect how
/// their documents are read. Coupling them would make every future decision
/// "and do we also want to re-run inference over everyone's library".
///
/// Bump it when chunk boundaries or headings would come out differently for
/// identical text. The next extraction pass then re-chunks and re-embeds only
/// what it re-chunked.
public static class OwnerDocumentChunkFormat
{
    /// 1 — Slice 3's chunking: the markdown/prose splitter, section-aware, with
    /// a heading trail.
    public const int Current = 1;
}

/// One chunk of a private document, before it has an identity.
public sealed record OwnerDocumentChunkDraft(
    int Ordinal, string Heading, string Text, int StartOffset, int EndOffset);

/// Splits a person's document into retrievable passages, deterministically.
///
/// Deliberately the SAME splitter the system corpus uses. A second
/// implementation would be a second set of heading rules, a second set of
/// boundary bugs and a second thing to keep in step, and the shape of the
/// problem is identical: prose with sections, split so a passage stands on its
/// own and six of them fit a context budget. What is NOT shared is the version
/// above and the storage — those are the parts where private and system
/// genuinely differ.
///
/// No language parsers. A `.json` or `.csv` in somebody's library is chunked as
/// text, because the alternative is a parser per format, and a parser is a
/// memory-safety surface pointed at user-supplied bytes.
public static class OwnerDocumentChunker
{
    public static IReadOnlyList<OwnerDocumentChunkDraft> Chunk(
        string text, DocumentExtractionOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<OwnerDocumentChunkDraft>();

        var drafts = MarkdownRagChunker.Chunk(text);
        var result = new List<OwnerDocumentChunkDraft>(Math.Min(drafts.Count, options.EffectiveMaxChunks));

        // Offsets are recovered by SEARCHING FORWARD from where the previous
        // chunk ended, never by assuming the chunker concatenates back to the
        // input. It does not: headings travel into a chunk's text, so a naive
        // running total would drift and every offset after the first heading
        // would point at the wrong place. A chunk whose text cannot be located
        // simply reports no offsets, which is honest — they are diagnostic, and
        // a wrong offset is worse than an absent one.
        var cursor = 0;

        foreach (var draft in drafts)
        {
            if (result.Count >= options.EffectiveMaxChunks) break;

            var body = draft.Text;
            if (body.Length > options.EffectiveMaxChunkCharacters)
            {
                body = body[..options.EffectiveMaxChunkCharacters];
            }
            if (string.IsNullOrWhiteSpace(body)) continue;

            var start = text.IndexOf(body, cursor, StringComparison.Ordinal);
            var end = start >= 0 ? start + body.Length : -1;
            if (start >= 0) cursor = end;

            // Ordinal is RE-DERIVED from position in this list rather than
            // copied from the draft. Skipping a blank chunk must not leave a
            // hole: the ordinal is part of the chunk's identity
            // (DocumentTextId, ProfileId, Ordinal), so a gap would make the same
            // document chunk to different keys depending on what was skipped.
            result.Add(new OwnerDocumentChunkDraft(
                Ordinal: result.Count + 1,
                Heading: draft.Heading,
                Text: body,
                StartOffset: start,
                EndOffset: end));
        }

        return result;
    }
}

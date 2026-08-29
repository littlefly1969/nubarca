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
    /// a heading trail. A draft longer than the chunk budget was CUT to the
    /// budget and its tail discarded.
    ///
    /// 2 — the same splitter, with oversized drafts SPLIT rather than cut. The
    /// tail of a long paragraph is text the owner wrote, and dropping it
    /// published part of their document as the whole of it — the completeness
    /// invariant the rest of Slice 4 enforces.
    ///
    /// Bumping this needs no schema migration. `ChunkFormatVersion` is compared
    /// on every indexing pass, and a mismatch fails the idempotent early exit,
    /// so an existing document is re-chunked and re-embedded by the next
    /// ordinary pass with nothing to run by hand.
    public const int Current = 2;
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
        var result = new List<OwnerDocumentChunkDraft>(drafts.Count);

        // Offsets are recovered by SEARCHING FORWARD from where the previous
        // chunk ended, never by assuming the chunker concatenates back to the
        // input. It does not: headings travel into a chunk's text, so a naive
        // running total would drift and every offset after the first heading
        // would point at the wrong place. A chunk whose text cannot be located
        // simply reports no offsets, which is honest — they are diagnostic, and
        // a wrong offset is worse than an absent one.
        var cursor = 0;

        // THE CHUNK-COUNT BOUND IS NOT ENFORCED HERE ANY MORE. It used to stop
        // at the ceiling and return what fit, which is a partial document
        // wearing a complete document's clothes. The ceiling is now decided in
        // ONE place — OwnerDocumentIndexer.PlanChunks — where exceeding it
        // refuses the document instead, for native text and structured formats
        // alike.
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Text)) continue;

            // AN OVERSIZED DRAFT IS SPLIT, NEVER CUT. It used to be truncated to
            // the budget and its tail dropped on the floor — the markdown
            // splitter's own maximum is above the default chunk budget, so this
            // fired on ordinary prose and quietly removed the end of long
            // paragraphs from somebody's document.
            //
            // Every piece keeps the draft's HEADING, because the heading is what
            // makes a passage citable and the second half of a paragraph is in
            // the same section as the first.
            foreach (var body in SplitToBudget(draft.Text, options.EffectiveMaxChunkCharacters))
            {
                if (string.IsNullOrWhiteSpace(body)) continue;

                var start = text.IndexOf(body, cursor, StringComparison.Ordinal);
                var end = start >= 0 ? start + body.Length : -1;
                if (start >= 0) cursor = end;

                // Ordinal is RE-DERIVED from position in this list rather than
                // copied from the draft. Skipping a blank chunk must not leave a
                // hole: the ordinal is part of the chunk's identity
                // (DocumentTextId, ProfileId, Ordinal), so a gap would make the
                // same document chunk to different keys depending on what was
                // skipped.
                result.Add(new OwnerDocumentChunkDraft(
                    Ordinal: result.Count + 1,
                    Heading: draft.Heading,
                    Text: body,
                    StartOffset: start,
                    EndOffset: end));
            }
        }

        return result;
    }

    /// Cuts one oversized draft into pieces that each fit the budget, LOSING
    /// NOTHING: concatenating what this yields reproduces the input exactly.
    ///
    /// That exactness is the whole point, and it is why nothing here trims. A
    /// boundary's own newline or space travels with the piece BEFORE it, so the
    /// next piece starts on real content and the join is still lossless — a
    /// trim would look tidier and would silently delete characters at every
    /// boundary, which is a smaller version of the bug being fixed.
    ///
    /// The break is chosen from the text rather than imposed on it: a line
    /// ending first, then a sentence ending, then a word boundary, and only a
    /// hard cut when a single run offers none of them. Deterministic at every
    /// step, because the same document must chunk to the same pieces — the
    /// ordinal-by-ordinal reuse that keeps a one-paragraph edit costing one
    /// embedding depends on it.
    private static IEnumerable<string> SplitToBudget(string text, int max)
    {
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= max)
            {
                yield return text[start..];
                yield break;
            }

            var window = text.AsSpan(start, max);
            var length = PreferredBreak(window, max);
            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// How many characters of `window` to take, preferring a boundary a reader
    /// would recognise.
    ///
    /// A candidate is only accepted past a quarter of the budget. Below that the
    /// "boundary" produces a sliver of a chunk and pushes the real content into
    /// the next one, which retrieves worse than an honest hard cut.
    private static int PreferredBreak(ReadOnlySpan<char> window, int max)
    {
        var floor = max / 4;

        var newline = window.LastIndexOf('\n');
        if (newline > floor) return newline + 1;

        var sentence = LastSentenceEnd(window);
        if (sentence > floor) return sentence;

        var space = window.LastIndexOf(' ');
        if (space > floor) return space + 1;

        // A single unbroken run — a URL, a base64 blob, a language that does not
        // space its words. Cut it, because the alternative is an unbounded chunk.
        return max;
    }

    /// The end of the last sentence in the window, or -1. Counted as the
    /// position AFTER the terminator and its following space, so the piece keeps
    /// its own punctuation.
    private static int LastSentenceEnd(ReadOnlySpan<char> window)
    {
        for (var i = window.Length - 2; i > 0; i--)
        {
            if (window[i] is not ('.' or '!' or '?' or ';')) continue;
            if (!char.IsWhiteSpace(window[i + 1])) continue;
            return i + 2;
        }

        return -1;
    }
}

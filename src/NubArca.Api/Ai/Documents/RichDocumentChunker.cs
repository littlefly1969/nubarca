namespace NubArca.Api.Ai.Documents;

/// A chunk of a rich document, carrying where it came from.
public sealed record RichChunkDraft(
    int Ordinal,
    string Heading,
    string Text,
    int StartOffset,
    int EndOffset,
    DocumentLocator Locator);

/// Splits STRUCTURED documents without losing the structure.
///
/// The native chunker takes a string and finds headings in it, which is right
/// for a Markdown file and wrong for everything here: by the time a presentation
/// is a string, the slide boundaries are gone, and a chunker that finds its own
/// boundaries will happily produce a passage that starts on slide 6 and ends on
/// slide 7. That passage cites a place that does not exist.
///
/// So the block boundary is a hard boundary. A block never merges with its
/// neighbour — not even two tiny ones — because merging is what destroys the
/// locator: two slides in one chunk have no slide number, and picking either
/// one's is a lie about half the text. A block LARGER than the chunk budget is
/// split, and every piece keeps the block's locator, which stays true.
///
/// DETERMINISM IS THE CONTRACT. The same blocks produce the same chunks, byte
/// for byte, so `TextHash` only changes when the document does — and the
/// ordinal-by-ordinal reuse that makes a one-paragraph edit cost one embedding
/// keeps working.
public static class RichDocumentChunker
{
    public static IReadOnlyList<RichChunkDraft> Chunk(
        IReadOnlyList<ExtractedDocumentBlock> blocks, DocumentExtractionOptions options)
    {
        var drafts = new List<RichChunkDraft>();
        if (blocks.Count == 0) return drafts;

        var max = options.EffectiveMaxChunkCharacters;
        var limit = options.EffectiveMaxChunks;
        var ordinal = 0;
        var offset = 0;

        for (var i = 0; i < blocks.Count; i++)
        {
            if (drafts.Count >= limit) break;

            var block = blocks[i];
            var text = block.Text ?? string.Empty;

            // The canonical text joins blocks with a blank line, and the offsets
            // recorded here have to point into THAT string — they are what a
            // future derivative uses to find this passage in the document.
            if (i > 0) offset += 2;

            if (text.Length == 0) continue;

            var heading = block.Heading ?? string.Empty;

            if (text.Length <= max)
            {
                drafts.Add(new RichChunkDraft(
                    ++ordinal, heading, text, offset, offset + text.Length, block.Locator));
                offset += text.Length;
                continue;
            }

            // A BLOCK TOO BIG FOR ONE CHUNK is split, and every piece keeps the
            // block's locator. Splitting on line boundaries where possible keeps
            // a spreadsheet row and a bullet whole; a single line longer than the
            // budget is cut hard, because the alternative is an unbounded chunk.
            var start = 0;
            while (start < text.Length && drafts.Count < limit)
            {
                var length = Math.Min(max, text.Length - start);
                if (start + length < text.Length)
                {
                    var window = text.AsSpan(start, length);
                    var breakAt = window.LastIndexOf('\n');
                    if (breakAt > max / 4) length = breakAt + 1;
                }

                var piece = text.Substring(start, length).Trim();
                if (piece.Length > 0)
                {
                    drafts.Add(new RichChunkDraft(
                        ++ordinal, heading, piece,
                        offset + start, offset + start + length, block.Locator));
                }

                start += length;
            }

            offset += text.Length;
        }

        return drafts;
    }
}

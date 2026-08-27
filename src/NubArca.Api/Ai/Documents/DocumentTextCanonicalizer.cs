using System.Text;

namespace NubArca.Api.Ai.Documents;

/// ONE definition of how blocks become the document's text.
///
/// Every parser could join its own blocks, and then `DocumentText.Text` would
/// mean something slightly different per format — and the things computed from
/// it would quietly differ too. `TextHash` is the idempotence key that decides
/// whether a document is re-chunked; the offsets are what a citation points at;
/// the chunker's boundaries depend on where the newlines are. A parser that
/// used a single newline where another used two would change all three for its
/// format alone, and the symptom would be documents re-chunking for no reason.
///
/// So the separator rule lives here, once. Determinism is the contract: the same
/// blocks produce the same string, byte for byte, on every machine and every
/// run, because a hash that changes without the document changing is a hash that
/// causes work rather than preventing it.
public static class DocumentTextCanonicalizer
{
    /// Blocks are separated by a blank line.
    ///
    /// Two newlines rather than one because a blank line is a paragraph break in
    /// every format being joined here, and because the chunker already reads it
    /// that way — a single newline would run a slide title into its body and a
    /// spreadsheet row into the next.
    private const string BlockSeparator = "\n\n";

    public static string Canonicalize(IReadOnlyList<ExtractedDocumentBlock> blocks)
    {
        if (blocks.Count == 0) return string.Empty;
        if (blocks.Count == 1) return blocks[0].Text;

        var builder = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) builder.Append(BlockSeparator);
            builder.Append(blocks[i].Text);
        }

        return builder.ToString();
    }
}

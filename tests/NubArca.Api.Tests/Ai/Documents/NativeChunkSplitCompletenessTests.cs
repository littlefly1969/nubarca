using NubArca.Api.Ai.Documents;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// THE LAST SILENT TRUNCATION IN SLICE 4.
//
// The native chunker cut a draft longer than the chunk budget down to the
// budget and dropped the rest. It was not a theoretical path: the markdown
// splitter's own maximum (1800) sits ABOVE the default chunk budget (1600), so
// an ordinary long paragraph lost its ending, and the document was still
// recorded Completed. Every other family in this slice refuses rather than
// publishes part of a document; this one quietly deleted text and published the
// remainder as the whole.
//
// The fix splits instead of cutting, so the completeness question moves to
// where it belongs: OwnerDocumentIndexer.PlanChunks, which refuses a document
// whose LOSSLESS split needs more chunks than the bound allows.
public sealed class NativeChunkSplitCompletenessTests
{
    /// A paragraph of `words` words, deterministic and prose-shaped, so the
    /// splitter has real sentence and word boundaries to find.
    private static string Paragraph(int sentences)
        => string.Join(" ", Enumerable.Range(1, sentences).Select(i =>
            $"La manutenzione ordinaria numero {i} prevede il controllo del "
            + $"circuito idraulico e la verifica della pressione di esercizio."));

    // ---- 1. no text is lost -------------------------------------------------

    [Fact]
    public void A_Draft_Just_Over_The_Budget_Is_Split_And_Nothing_Is_Lost()
    {
        // Deliberately just over: the old code took the first `max` characters
        // and discarded a tail small enough that nobody would notice it missing
        // until they asked a question whose answer lived there.
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 400 };
        var body = Paragraph(6);
        Assert.True(body.Length > options.EffectiveMaxChunkCharacters);
        Assert.True(body.Length < options.EffectiveMaxChunkCharacters * 2,
            "the fixture must be JUST over the budget, which is the case that used to look harmless");

        var text = $"# Manuale\n\n{body}";
        var chunks = OwnerDocumentChunker.Chunk(text, options);

        Assert.True(chunks.Count > 1, "an oversized draft must be split, not cut");

        // EVERY character survives, exactly once and in order. This body is one
        // markdown draft, so the pieces concatenate back to it VERBATIM — the
        // strongest form of the claim, and the one the old behaviour could not
        // pass because it threw the tail away.
        var rejoined = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains(body, rejoined, StringComparison.Ordinal);
        Assert.EndsWith(body[^60..], rejoined, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Draft_Many_Times_The_Budget_Loses_Nothing_Either()
    {
        // Long enough that the MARKDOWN splitter produces several drafts of its
        // own before this chunker ever sees them, so exact concatenation is not
        // the right assertion — the markdown layer normalises whitespace between
        // its drafts, which is the "modulo whitespace normalisation" clause.
        //
        // What must hold regardless is that no sentence disappears and none is
        // duplicated. Counting each numbered sentence is a sharper test than a
        // substring check anyway: truncation drops the tail, and a bad split
        // would emit an overlap.
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 300 };
        var chunks = OwnerDocumentChunker.Chunk($"# Manuale\n\n{Paragraph(40)}", options);

        var rejoined = string.Concat(chunks.Select(c => c.Text));
        for (var i = 1; i <= 40; i++)
        {
            var marker = $"numero {i} ";
            var occurrences = CountOccurrences(rejoined, marker);
            Assert.True(occurrences == 1,
                $"sentence {i} appears {occurrences} times; it must appear exactly once");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    // ---- 2. every chunk respects the maximum --------------------------------

    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(512)]
    [InlineData(1_600)]
    public void Every_Emitted_Chunk_Fits_The_Configured_Maximum(int max)
    {
        var options = new DocumentExtractionOptions { MaxChunkCharacters = max };
        var text = $"# Manuale\n\n{Paragraph(30)}\n\n## Sezione\n\n{Paragraph(30)}";

        var chunks = OwnerDocumentChunker.Chunk(text, options);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
            Assert.True(
                c.Text.Length <= options.EffectiveMaxChunkCharacters,
                $"a chunk of {c.Text.Length} exceeds the {options.EffectiveMaxChunkCharacters} budget"));
    }

    [Fact]
    public void A_Run_With_No_Boundary_At_All_Is_Still_Bounded()
    {
        // A URL, a base64 blob, a language that does not space its words. There
        // is no boundary to prefer, so the cut is hard — but it is still a cut
        // into pieces rather than a cut that throws the rest away.
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 200 };
        var unbroken = new string('x', 1_000);

        var chunks = OwnerDocumentChunker.Chunk($"# Blob\n\n{unbroken}", options);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200));
        Assert.Contains(unbroken, string.Concat(chunks.Select(c => c.Text)), StringComparison.Ordinal);
    }

    // ---- 3. ordering and headings ------------------------------------------

    [Fact]
    public void Order_Is_Preserved_And_Every_Piece_Keeps_Its_Heading()
    {
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 300 };
        var text = $"# Manuale\n\n## Pulizia\n\n{Paragraph(20)}";

        var chunks = OwnerDocumentChunker.Chunk(text, options);

        Assert.True(chunks.Count > 1);

        // Contiguous ordinals from 1: the ordinal is part of a chunk's identity
        // (DocumentTextId, ProfileId, Ordinal), so a hole would make the same
        // document chunk to different keys.
        Assert.Equal(
            Enumerable.Range(1, chunks.Count).ToArray(),
            chunks.Select(c => c.Ordinal).ToArray());

        // The heading travels with every piece. The second half of a paragraph
        // is in the same section as the first, and a citation has to say so.
        var headings = chunks.Select(c => c.Heading).Distinct().ToArray();
        Assert.Single(headings);
        Assert.Contains("Pulizia", headings[0], StringComparison.Ordinal);

        // Reading order survives the split.
        var rejoined = string.Concat(chunks.Select(c => c.Text));
        Assert.True(
            rejoined.IndexOf("numero 1 ", StringComparison.Ordinal)
            < rejoined.IndexOf("numero 20 ", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunking_Is_Deterministic()
    {
        // The reuse that makes a one-paragraph edit cost one embedding depends
        // on identical input producing identical pieces.
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 250 };
        var text = $"# Manuale\n\n{Paragraph(25)}";

        var first = OwnerDocumentChunker.Chunk(text, options);
        var second = OwnerDocumentChunker.Chunk(text, options);

        Assert.Equal(
            first.Select(c => (c.Ordinal, c.Heading, c.Text)).ToArray(),
            second.Select(c => (c.Ordinal, c.Heading, c.Text)).ToArray());
    }

    [Fact]
    public void A_Sensible_Boundary_Is_Preferred_Over_A_Hard_Cut()
    {
        // Prose that offers sentence endings should not be cut mid-word. This
        // is a quality property rather than a completeness one — the split is
        // lossless either way — but a chunk that ends mid-word retrieves badly
        // and reads as broken in a citation.
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 400 };
        var chunks = OwnerDocumentChunker.Chunk($"# Manuale\n\n{Paragraph(12)}", options);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            var last = chunk.Text[^1];
            Assert.True(
                char.IsWhiteSpace(last) || last is '.' or '!' or '?' or ';',
                $"a chunk ended mid-token on '{last}'");
        }
    }

    // ---- the format version -------------------------------------------------

    [Fact]
    public void The_Chunk_Format_Version_Records_That_Boundaries_Changed()
    {
        // Splitting instead of cutting moves chunk boundaries, so every existing
        // native document's chunks describe text that no longer matches how it
        // would be chunked today. The version is what makes the next ordinary
        // indexing pass notice and re-chunk — no schema migration, no backfill
        // to run by hand.
        Assert.Equal(2, OwnerDocumentChunkFormat.Current);
    }
}

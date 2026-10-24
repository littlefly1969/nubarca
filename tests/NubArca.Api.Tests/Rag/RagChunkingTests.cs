using NubArca.Api.Rag.Chunking;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Chunking, which decides what a retrieved passage IS.
//
// Worth testing on its own because both failure modes are quiet. Chunks that
// are too big dilute the sentence that matched and spend a context budget on
// text nobody asked about; chunks that are too small say nothing on their own.
// Neither throws.
public sealed class RagChunkingTests
{
    // ---- markdown ------------------------------------------------------------

    [Fact]
    public void Markdown_Chunks_Carry_Their_Heading_Trail()
    {
        var drafts = MarkdownRagChunker.Chunk("""
            # Volti

            Introduzione alla funzione dei volti.

            ## Gruppi suggeriti

            ### Assegna nome

            Apri il gruppo e scegli Assegna nome per dargli un'identità.
            """);

        // `Volti › Gruppi suggeriti › Assegna nome` is a citation somebody can
        // act on; a file name alone is not.
        Assert.Contains(drafts, d => d.Heading == "Volti › Gruppi suggeriti › Assegna nome");
        Assert.All(drafts, d => Assert.NotEmpty(d.Text));
        Assert.Equal(drafts.Select((_, i) => i + 1), drafts.Select(d => d.Ordinal));
    }

    [Fact]
    public void Markdown_Does_Not_Treat_A_Comment_Inside_A_Fence_As_A_Heading()
    {
        var drafts = MarkdownRagChunker.Chunk("""
            # Deploy

            Esegui il comando indicato di seguito per aggiornare la produzione.

            ```bash
            # Questo NON è un titolo: è un commento shell dentro un blocco di codice
            docker compose up -d
            ```

            Il blocco sopra deve restare intero.
            """);

        Assert.All(drafts, d => Assert.Equal("Deploy", d.Heading));
        var fence = Assert.Single(drafts, d => d.Text.Contains("docker compose up -d", StringComparison.Ordinal));
        // A split inside a fence produces two chunks that are each syntactically
        // nonsense, so the fence is kept whole.
        Assert.Contains("```bash", fence.Text, StringComparison.Ordinal);
        Assert.Equal(2, fence.Text.Split("```").Length - 1);
    }

    [Fact]
    public void Markdown_Chunks_Are_Section_Sized()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat(
            "Questa frase descrive il comportamento della libreria multimediale.", 20));
        var document = "# Titolo\n\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 20));

        var drafts = MarkdownRagChunker.Chunk(document);

        Assert.True(drafts.Count > 1, "a long document must not become one chunk");
        Assert.All(drafts, d => Assert.True(
            d.Text.Length <= RagChunkSizes.HardCharacters,
            $"chunk of {d.Text.Length} characters exceeds the hard bound"));
    }

    [Fact]
    public void Chunking_Is_Deterministic()
    {
        const string document = "# Caldaia\n\nPulire il filtro ogni sei mesi.\n\n## Garanzia\n\nDue anni.";
        var first = MarkdownRagChunker.Chunk(document);
        var second = MarkdownRagChunker.Chunk(document);

        Assert.Equal(
            first.Select(d => (d.Ordinal, d.Heading, d.Text)),
            second.Select(d => (d.Ordinal, d.Heading, d.Text)));
    }

    [Fact]
    public void Empty_Input_Produces_No_Chunks()
    {
        Assert.Empty(MarkdownRagChunker.Chunk(string.Empty));
        Assert.Empty(MarkdownRagChunker.Chunk("   \n\n  "));
    }
}

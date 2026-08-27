using NubArca.Api.Ai.Documents;
using NubArca.Api.Rag.Chunking;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// Chunking a person's own document.
//
// The interesting properties are DETERMINISM and BOUNDEDNESS, not cleverness.
// A chunk's identity is (document, profile, ordinal), so an ordinal that moved
// because a blank section was skipped would silently re-key half a document and
// throw away its embeddings — which is why "no holes in the ordinals" is a test
// and not an implementation detail.
public sealed class OwnerDocumentChunkingTests
{
    private static readonly DocumentExtractionOptions Options = new();

    private const string Manual = """
        # Manuale della caldaia

        Questo manuale descrive la manutenzione ordinaria della caldaia
        installata nell'appartamento, comprese le operazioni che il proprietario
        può eseguire senza chiamare un tecnico specializzato.

        ## Pulizia del filtro

        Il filtro dell'acqua va pulito ogni sei mesi. Chiudere il rubinetto di
        ingresso, svitare il corpo del filtro e sciacquare la cartuccia sotto
        acqua corrente fino a rimuovere ogni residuo visibile.

        ## Controllo della pressione

        La pressione dell'impianto deve restare fra 1,2 e 1,5 bar a freddo.
        Se scende sotto 1 bar occorre reintegrare l'acqua dal rubinetto di
        caricamento fino a riportarla nell'intervallo corretto.
        """;

    [Fact]
    public void ChunkOrdinals_AreDeterministic_AndContiguous()
    {
        var first = OwnerDocumentChunker.Chunk(Manual, Options);
        var second = OwnerDocumentChunker.Chunk(Manual, Options);

        Assert.NotEmpty(first);
        Assert.Equal(
            first.Select(c => (c.Ordinal, c.Heading, c.Text)),
            second.Select(c => (c.Ordinal, c.Heading, c.Text)));

        // 1..n with no holes. A gap would re-key every chunk after it on the
        // next pass, dropping their embeddings for no reason.
        Assert.Equal(
            Enumerable.Range(1, first.Count).ToArray(),
            first.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public void Sections_BecomeHeadings()
    {
        var chunks = OwnerDocumentChunker.Chunk(Manual, Options);

        // A private document has no editorial metadata, so its section titles
        // are most of what ranking has to work with — and the only part of a
        // chunk safe to show as a citation.
        Assert.Contains(chunks, c => c.Heading.Contains("Pulizia del filtro", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("ogni sei mesi", StringComparison.Ordinal));
    }

    [Fact]
    public void DocumentChunk_IsBounded()
    {
        var options = new DocumentExtractionOptions { MaxChunkCharacters = 300 };
        var chunks = OwnerDocumentChunker.Chunk(Manual + "\n\n" + new string('x', 9000), options);

        Assert.All(chunks, c => Assert.True(
            c.Text.Length <= options.EffectiveMaxChunkCharacters,
            $"chunk {c.Ordinal} is {c.Text.Length} characters"));
    }

    [Fact]
    public void ChunkCount_IsBounded()
    {
        var options = new DocumentExtractionOptions { MaxChunks = 2 };
        var many = string.Join("\n\n", Enumerable.Range(1, 40).Select(i =>
            $"## Sezione {i}\n\nUn paragrafo di testo sufficientemente lungo da diventare "
            + $"un chunk autonomo, numero {i}, con abbastanza parole da non essere unito."));

        var chunks = OwnerDocumentChunker.Chunk(many, options);

        Assert.Equal(2, chunks.Count);
        // Still contiguous after the cap: the bound truncates, it does not
        // renumber what survives.
        Assert.Equal(new[] { 1, 2 }, chunks.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public void EmptyText_ProducesNoChunks()
    {
        Assert.Empty(OwnerDocumentChunker.Chunk("", Options));
        Assert.Empty(OwnerDocumentChunker.Chunk("   \n\n  ", Options));
    }

    [Fact]
    public void Offsets_Are_Absent_Rather_Than_Wrong()
    {
        // The chunker lifts headings into a chunk's text, so a chunk does not
        // always appear verbatim in the source. Offsets are diagnostic, and an
        // offset pointing at the wrong place is worse than no offset — so a
        // chunk that cannot be located reports -1 rather than a plausible guess.
        var chunks = OwnerDocumentChunker.Chunk(Manual, Options);

        Assert.All(chunks, c =>
        {
            if (c.StartOffset < 0)
            {
                Assert.Equal(-1, c.EndOffset);
                return;
            }
            Assert.True(c.EndOffset > c.StartOffset);
            Assert.True(c.EndOffset <= Manual.Length);
            Assert.Equal(c.Text, Manual.Substring(c.StartOffset, c.EndOffset - c.StartOffset));
        });
    }

    [Fact]
    public void The_Private_Chunk_Version_Is_Independent_Of_The_System_One()
    {
        // Two versions that must be able to move independently. Bumping
        // RagIndexFormat re-chunks and re-embeds the repository corpus; people's
        // own documents must not pay for a change that did not affect how they
        // are read, and vice versa.
        //
        // The assertion is about the SOURCE, not the values: they may coincide
        // at any moment, and that must not be what anything relies on.
        var source = File.ReadAllText(Path.Combine(
            NubArca.Api.Tests.Rag.RagTestHarness.RepositoryRoot(),
            "src/NubArca.Api/Ai/Documents/OwnerDocumentChunking.cs"));

        Assert.Contains("OwnerDocumentChunkFormat", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RagIndexFormat.Current", source, StringComparison.Ordinal);
        Assert.True(OwnerDocumentChunkFormat.Current > 0);
        Assert.True(RagIndexFormat.Current > 0);
    }
}

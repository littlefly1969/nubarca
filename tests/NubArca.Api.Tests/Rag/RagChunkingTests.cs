using NubArca.Api.Rag;
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

    // ---- source code ---------------------------------------------------------

    [Fact]
    public void Code_Chunks_Start_At_Declarations_And_Name_Their_Symbols()
    {
        var drafts = RagChunkers.Chunk(CSharpFixture, RagCodeLanguages.CSharp);

        Assert.True(drafts.Count > 1, "a multi-member file must not become one chunk");
        var symbols = drafts.SelectMany(d => d.Symbols).ToList();
        Assert.Contains("ExampleService", symbols);
        Assert.Contains("Describe", symbols);
        Assert.Contains("Rebuild", symbols);

        // The heading is a citation: a type, a member and a line range.
        Assert.All(drafts, d => Assert.Matches(@"L\d+–L\d+", d.Heading));
    }

    [Fact]
    public void A_Declaration_Keeps_The_Comment_Written_Above_It()
    {
        var drafts = RagChunkers.Chunk(CSharpFixture, RagCodeLanguages.CSharp);

        // In this codebase the comment above a member is frequently the best
        // description of what it is for. Indexing the explanation away from the
        // thing it explains would mean a question phrased the way the comment is
        // written retrieves a chunk that does not contain the code.
        var rebuild = Assert.Single(drafts, d => d.Text.Contains(
            "public void Rebuild(", StringComparison.Ordinal));
        Assert.Contains("Rebuilds the cached view", rebuild.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Whole_File_Chunks_Are_Never_Produced()
    {
        var long_ = string.Join("\n", Enumerable.Range(0, 400).Select(i =>
            $"    public string Property{i} {{ get; set; }} = \"a value long enough to matter {i}\";"));
        var file = "namespace NubArca.Api.Tests.Fixtures;\n\npublic sealed class Big\n{\n" + long_ + "\n}\n";

        var drafts = RagChunkers.Chunk(file, RagCodeLanguages.CSharp);

        Assert.True(drafts.Count > 3);
        Assert.All(drafts, d => Assert.True(
            d.Text.Length <= RagChunkSizes.MaximumCharacters + 400,
            $"chunk of {d.Text.Length} characters is effectively the whole file"));
    }

    [Theory]
    [InlineData(RagCodeLanguages.TypeScript, "export function facesTabs(): string[] {\n  return [];\n}", "facesTabs")]
    [InlineData(RagCodeLanguages.Sql, "CREATE TABLE IF NOT EXISTS face_previews (id uuid);", "face_previews")]
    [InlineData(RagCodeLanguages.Shell, "deploy_release() {\n  echo hi\n}", "deploy_release")]
    [InlineData(RagCodeLanguages.Yaml, "services:\n  api:\n    image: nubarca", "services")]
    public void Each_Language_Recognises_Its_Own_Declarations(
        string language, string source, string expectedSymbol)
    {
        var padding = string.Join("\n", Enumerable.Repeat("# padding line that adds body", 10));
        var drafts = RagChunkers.Chunk($"{source}\n{padding}", language);

        Assert.Contains(expectedSymbol, drafts.SelectMany(d => d.Symbols));
    }

    [Fact]
    public void Chunking_Is_Deterministic()
    {
        var first = RagChunkers.Chunk(CSharpFixture, RagCodeLanguages.CSharp);
        var second = RagChunkers.Chunk(CSharpFixture, RagCodeLanguages.CSharp);

        Assert.Equal(
            first.Select(d => (d.Ordinal, d.Heading, d.Text)),
            second.Select(d => (d.Ordinal, d.Heading, d.Text)));
    }

    [Fact]
    public void Empty_Input_Produces_No_Chunks()
    {
        Assert.Empty(RagChunkers.Chunk(string.Empty, RagCodeLanguages.CSharp));
        Assert.Empty(RagChunkers.Chunk("   \n\n  ", RagCodeLanguages.Markdown));
    }

    private const string CSharpFixture = """
        namespace NubArca.Api.Tests.Fixtures;

        using System;

        /// A service that exists so the chunker has something with structure to
        /// divide: a type declaration, several members, and comments above them
        /// that belong with the code they describe rather than with whatever
        /// happened to precede it in the file.
        public sealed class ExampleService
        {
            private readonly string _name;

            public ExampleService(string name)
            {
                _name = name;
                // Enough body that this region clears the minimum chunk size and
                // the next declaration actually opens a new chunk.
                Console.WriteLine($"constructed {_name} with a deliberately long line of body text");
            }

            /// Describes the service in a sentence, which is the sort of thing a
            /// person asks about by paraphrasing rather than by identifier.
            public string Describe()
            {
                return $"{_name} is an example service used by the chunking tests, "
                    + "with a body long enough to be a chunk of its own rather than a fragment.";
            }

            /// Rebuilds the cached view of the service, which is the operation an
            /// operator asks about when the cache looks stale after a deploy.
            public void Rebuild(int generation)
            {
                Console.WriteLine($"rebuilding {_name} at generation {generation}");
                Console.WriteLine("this second line exists so the region has enough body to stand alone");
            }
        }
        """;
}

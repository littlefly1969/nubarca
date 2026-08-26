using NubArca.Api.Rag;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Text;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Golden retrieval, against the REAL shipped Product Help sources.
//
// Not a synthetic fixture: the thing worth protecting is that the documents this
// release actually ships answer the questions people actually ask. A fixture
// would keep passing while the manifest drifted, which is the failure these
// tests exist to catch.
public sealed class ProductHelpRetrievalTests
{
    /// The question that motivated the whole retrieval rewrite. Before it, the
    /// top hits were docs/OPERATIONS.md and docs/multimodal-photo-search.md,
    /// which share accidental words with it and answer none of it.
    private const string ItalianFacesQuestion = "come faccio a utilizzare la funzione dei volti?";

    // The REAL generic retriever over the REAL shipped corpus. Slice 2 moved
    // Product Help behind a domain-general retriever; what these tests assert
    // did not change, and neither did any of the numbers behind it.
    private static readonly Lazy<RagRetriever> Shipped = new(() =>
        RagTestHarness.ForProductHelp(RagTestHarness.ShippedProductHelp()));

    private static IReadOnlyList<RagEvidence> Ask(
        string question, int maxEvidence = 6, int maxCharacters = 12000)
        => Result(question, maxEvidence, maxCharacters).Evidence;

    private static RagRetrievalResult Result(
        string question, int maxEvidence = 6, int maxCharacters = 12000)
        => Shipped.Value
            .RetrieveAsync(new RagQuery(RagDomainKey.ProductHelp, question, maxEvidence, maxCharacters))
            .GetAwaiter().GetResult();

    [Fact]
    public void The_Italian_Faces_Question_Retrieves_The_Faces_User_Guidance()
    {
        var evidence = Ask(ItalianFacesQuestion);

        Assert.NotEmpty(evidence);
        // TOP evidence, not merely present: what leads the context is what the
        // model answers from.
        Assert.StartsWith("docs/help/faces", evidence[0].Path, StringComparison.Ordinal);
        Assert.Equal(ProductHelpVocabulary.SourceKind.UserGuide, evidence[0].SourceKind);

        // …and never the documents that used to win on accidental overlap.
        foreach (var loser in new[] { "docs/OPERATIONS.md", "docs/multimodal-photo-search.md" })
        {
            Assert.NotEqual(loser, evidence[0].Path);
        }

        // The workflow the question is really asking about is in the context.
        var text = string.Join("\n", evidence.Select(e => e.Text));
        Assert.Contains("Gruppi suggeriti", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FacesQuestion_RanksUserGuideAboveTechnicalReference()
    {
        var evidence = Ask(ItalianFacesQuestion);
        var guide = evidence.Select((e, i) => (e, i))
            .First(x => x.e.SourceKind == ProductHelpVocabulary.SourceKind.UserGuide).i;
        var technical = evidence.Select((e, i) => (e, i))
            .FirstOrDefault(x => x.e.SourceKind == ProductHelpVocabulary.SourceKind.TechnicalReference,
                (null!, int.MaxValue)).i;

        Assert.True(guide < technical,
            "a how-to question must reach the user guide before a technical reference");
    }

    [Fact]
    public void HowToIntent_PrefersUserGuideOverOperations()
    {
        // Same topic, asked as a how-to. The operations runbook mentions faces
        // too; it is not what "how do I" wants.
        var evidence = Ask("how do I use the faces feature?");
        Assert.NotEmpty(evidence);
        Assert.StartsWith("docs/help/faces", evidence[0].Path, StringComparison.Ordinal);
        Assert.Equal(ProductHelpVocabulary.Intent.HowTo, evidence[0].Intent);
    }

    [Fact]
    public void FaceAliasExpansion_WorksAcrossItalianAndEnglish()
    {
        // Four ways to name the same feature, two languages, no shared token
        // between some of the pairs. All four must land on the same guidance.
        foreach (var question in new[]
                 {
                     "come funziona il riconoscimento facciale?",
                     "come assegno un nome a una persona nelle foto?",
                     "how do I assign a name to a face?",
                     "where do I find suggested face groups?",
                 })
        {
            var evidence = Ask(question);
            Assert.True(evidence.Count > 0, $"no evidence for: {question}");
            Assert.True(
                evidence[0].Path.StartsWith("docs/help/faces", StringComparison.Ordinal),
                $"'{question}' led with {evidence[0].Path} instead of the faces guide");
        }
    }

    [Fact]
    public void ItalianStopwords_DoNotCreateEnglishFalseHits()
    {
        // Italian `come` ("how") is also an English verb. Before the shared
        // stopword set, every English sentence containing "come" scored against
        // an Italian question — which is how a question about faces reached a
        // paragraph about how requests come in.
        var terms = RagText.ContentTokens("come faccio a utilizzare la funzione dei volti?");

        Assert.DoesNotContain("come", terms);
        Assert.DoesNotContain("faccio", terms);
        Assert.Contains("volti", terms);
        Assert.Contains("funzione", terms);

        // And the same word is dropped from English text, so it can match
        // nothing from either side.
        Assert.DoesNotContain("come", RagText.ContentTokens("requests come in over HTTP"));
    }

    [Fact]
    public void WeakLexicalOverlap_IsRejected()
    {
        // A real question about something NubArca is not. It shares ordinary
        // words with the corpus and nothing else, and `Score > 0` would have
        // bought an outbound provider call and an improvised answer.
        foreach (var question in new[]
                 {
                     "quanto costa un abbonamento mensile premium?",
                     "what is the weather forecast for tomorrow morning?",
                     "puoi prenotarmi un tavolo al ristorante?",
                 })
        {
            var result = Result(question);
            Assert.Equal(RagRetrievalOutcome.None, result.Outcome);
            Assert.Empty(result.Evidence);
        }
    }

    [Fact]
    public void SectionMetadata_IsPreservedInEvidence()
    {
        var evidence = Ask(ItalianFacesQuestion);
        // Every chunk knows where it came from, so a citation can name a section
        // rather than only a file.
        Assert.All(evidence, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.Equal(RagDomainKey.ProductHelp, e.Domain);
        });
        Assert.Contains(evidence, e => !string.IsNullOrWhiteSpace(e.Section));
    }

    [Fact]
    public void Evidence_IsCenteredOnRelevantSection()
    {
        // A budget too small for the whole chunk. The predecessor cut from
        // character zero, so the sentence that matched was frequently not in
        // what got sent — which is the worst possible way to spend a context
        // budget: the excerpt looks substantial and answers nothing.
        //
        // A built fixture rather than the shipped corpus, because the property
        // under test is the excerpting, and it needs a chunk whose match is
        // known to be at the far end.
        var filler = string.Join("\n\n", Enumerable.Repeat(
            "Questa sezione descrive il comportamento generale della libreria multimediale.", 12));
        var retriever = RagTestHarness.ForProductHelp(new ProductHelpCorpus(
            RagDomainKey.ProductHelp.Value, "r", new[]
            {
                new ProductHelpDocument(
                    "docs/help/faces.md#1", "docs/help/faces.md", "Volti", "Ignorati",
                    $"{filler}\n\nCon Ripristina i volti ignorati tornano fra i volti non assegnati.",
                    Feature: "faces",
                    Intent: ProductHelpVocabulary.Intent.HowTo,
                    Audience: ProductHelpVocabulary.Audience.User,
                    Language: ProductHelpVocabulary.Language.Italian,
                    SourceKind: ProductHelpVocabulary.SourceKind.UserGuide,
                    Aliases: new[] { "volti", "ignorati", "ripristina" },
                    Priority: 100),
            }));

        var evidence = retriever.RetrieveAsync(new RagQuery(
                RagDomainKey.ProductHelp, "come ripristina i volti ignorati?", 1, 320))
            .GetAwaiter().GetResult().Evidence;

        Assert.Single(evidence);
        Assert.True(evidence[0].Text.Length <= 320);
        Assert.Contains("Ripristina i volti ignorati", evidence[0].Text, StringComparison.Ordinal);
        // …and it is a WINDOW, marked as one, rather than the head of the chunk:
        // the match is 900 characters in, and a cut from zero would have missed
        // it entirely.
        Assert.StartsWith("… ", evidence[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Ignored_Faces_Workflow_Is_Reachable_In_The_Shipped_Corpus()
    {
        // One of the workflows the Faces guidance has to cover, asked the way
        // the interface names it.
        var evidence = Ask("volti ignorati", maxEvidence: 3);

        Assert.NotEmpty(evidence);
        Assert.StartsWith("docs/help/faces", evidence[0].Path, StringComparison.Ordinal);
        var text = string.Join("\n", evidence.Select(e => e.Text));
        Assert.Contains("Ripristina", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chunks_Are_Section_Sized_Rather_Than_Four_Thousand_Characters()
    {
        var corpus = RagTestHarness.ShippedProductHelp("r");
        Assert.NotEmpty(corpus.Documents);

        // The old builder accumulated paragraphs to 4,000 characters, which
        // produced excerpts spanning three unrelated topics.
        Assert.All(corpus.Documents, d => Assert.True(
            d.Text.Length <= 3000, $"{d.Id} is {d.Text.Length} characters"));

        var median = corpus.Documents.Select(d => d.Text.Length).Order().ToList()[corpus.Documents.Count / 2];
        Assert.InRange(median, 200, 1800);
    }

    [Fact]
    public void Retrieval_Respects_Its_Evidence_And_Character_Budgets()
    {
        var few = Ask(ItalianFacesQuestion, maxEvidence: 2, maxCharacters: 20000);
        Assert.True(few.Count <= 2);

        var tight = Ask(ItalianFacesQuestion, maxEvidence: 10, maxCharacters: 500);
        Assert.True(tight.Sum(e => e.Text.Length) <= 500);
    }

    [Fact]
    public void Retrieval_Is_Deterministic()
    {
        var first = Ask(ItalianFacesQuestion).Select(e => e.Id).ToList();
        var second = Ask(ItalianFacesQuestion).Select(e => e.Id).ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_Retriever_Refuses_A_Query_For_Another_Domain()
    {
        // A private domain will exist later. A feature holding a public
        // retriever must not silently receive public evidence when it asked for
        // something else — it must fail.
        var result = Shipped.Value
            .RetrieveAsync(new RagQuery(new RagDomainKey("private-library"), ItalianFacesQuestion, 6, 12000))
            .GetAwaiter().GetResult();

        Assert.Equal(RagRetrievalOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.Evidence);
    }

    internal static string RepositoryRoot() => RagTestHarness.RepositoryRoot();
}

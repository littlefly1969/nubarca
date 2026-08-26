using NubArca.Api.Rag;
using NubArca.Api.Rag.ProductHelp;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The Faces Product Help source, held against the interface it describes.
//
// A user guide that names a tab the product no longer has is worse than no
// guide: the assistant answers confidently and sends someone looking for a
// button that is not there. The labels are asserted against the SAME Italian
// locale file the page renders from, so a rename breaks the build rather than
// the answer.
//
// Deliberately a label check rather than a TypeScript parser. Parsing the page
// to extract behaviour would be a fragile second implementation of the
// frontend; what has to stay true is the vocabulary a person reads on screen.
public sealed class ProductHelpFacesSourceTests
{
    private static readonly string Root = ProductHelpRetrievalTests.RepositoryRoot();

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string ItalianLocale => Read("frontend/src/i18n/it.ts");
    private static string ItalianGuide => Read("docs/help/faces.md");
    private static string EnglishGuide => Read("docs/help/faces.en.md");

    /// The Faces tab labels, by the locale key the page renders.
    private static readonly string[] TabKeys =
    {
        "people.heading",
        "people.tabSuggested",
        "people.tabPeople",
        "people.tabUnassigned",
        "people.tabPhotoReview",
        "people.tabReview",
        "people.tabVideoFaces",
        "people.tabIgnored",
        "people.tabSettings",
    };

    [Theory]
    [MemberData(nameof(TabKeyCases))]
    public void Every_Faces_Tab_Label_Is_Named_By_The_Guidance(string key)
    {
        var label = LocaleValue(key);
        Assert.False(string.IsNullOrWhiteSpace(label), $"{key} is missing from the Italian locale");

        // Both guides use the Italian labels, because both describe the same
        // Italian interface — the English one translates the explanation, not
        // the buttons.
        Assert.Contains(label, ItalianGuide, StringComparison.Ordinal);
        Assert.Contains(label, EnglishGuide, StringComparison.Ordinal);
    }

    public static TheoryData<string> TabKeyCases()
    {
        var data = new TheoryData<string>();
        foreach (var key in TabKeys) data.Add(key);
        return data;
    }

    [Theory]
    // The route the guidance sends people to, and the actions inside a group.
    [InlineData("/people")]
    [InlineData("?tab=")]
    [InlineData("Rivedi gruppo")]
    [InlineData("Assegna nome")]
    [InlineData("Ignora gruppo")]
    [InlineData("Ripristina")]
    public void The_Guidance_Uses_The_Vocabulary_The_Interface_Uses(string phrase)
    {
        Assert.Contains(phrase, ItalianGuide, StringComparison.Ordinal);
        Assert.Contains(phrase, EnglishGuide, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("people.reviewGroup", "Rivedi gruppo")]
    [InlineData("people.assignNamePlaceholder", "Assegna nome")]
    [InlineData("people.ignoreGroup", "Ignora gruppo")]
    [InlineData("face.restore", "Ripristina")]
    [InlineData("people.photoReviewUnavailable", "Il riconoscimento dei volti non è attivo")]
    [InlineData("person.searchUnavailable", "Ricerca volti non disponibile in questo ambiente")]
    public void The_Quoted_Interface_Strings_Are_Still_What_The_Interface_Says(
        string key, string expected)
        => Assert.Contains(expected, LocaleValue(key), StringComparison.Ordinal);

    [Theory]
    // Every workflow the guidance is required to cover, as a phrase that would
    // disappear if the section were dropped.
    [InlineData("Gruppi suggeriti")]
    [InlineData("Volti non assegnati")]
    [InlineData("Foto da rivedere")]
    [InlineData("Da revisionare")]
    [InlineData("Volti nei video")]
    [InlineData("Ignorati")]
    [InlineData("Impostazioni Face AI")]
    public void The_Guidance_Covers_Each_Faces_Workflow(string section)
        => Assert.Contains(section, ItalianGuide, StringComparison.Ordinal);

    [Fact]
    public void The_Guidance_Says_Recognition_Can_Be_Unavailable()
    {
        // Required by the source's brief: someone whose installation has face
        // recognition switched off must be told that, not left looking for a
        // page that renders empty.
        Assert.Contains("non è attivo", ItalianGuide, StringComparison.Ordinal);
        Assert.Contains("Il riconoscimento dei volti è una funzione opzionale",
            ItalianGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Guidance_Describes_The_Product_And_Carries_No_Library_Data()
    {
        // A Product Help source is documentation, not a fixture. Nothing here
        // may be a real name, a real path or a real identifier.
        foreach (var forbidden in new[]
                 {
                     "OwnerUserId", "StorageKey", "sha256", "/storage/objects",
                     "PayloadJson", "TokenHash", "BlobId",
                 })
        {
            Assert.DoesNotContain(forbidden, ItalianGuide, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, EnglishGuide, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Both_Language_Variants_Are_Approved_And_Classified_As_User_Guidance()
    {
        foreach (var path in new[] { "docs/help/faces.md", "docs/help/faces.en.md" })
        {
            var source = ProductHelpSources.Find(path);
            Assert.NotNull(source);
            Assert.Equal("faces", source!.Feature);
            Assert.Equal(ProductHelpVocabulary.SourceKind.UserGuide, source.SourceKind);
            Assert.Equal(ProductHelpVocabulary.Intent.HowTo, source.Intent);
            Assert.Equal(ProductHelpVocabulary.Audience.User, source.Audience);
        }
        Assert.Equal(
            ProductHelpVocabulary.Language.Italian,
            ProductHelpSources.Find("docs/help/faces.md")!.Language);
        Assert.Equal(
            ProductHelpVocabulary.Language.English,
            ProductHelpSources.Find("docs/help/faces.en.md")!.Language);
    }

    [Fact]
    public void The_Faces_Guidance_Actually_Becomes_Retrievable_Chunks()
    {
        var corpus = ProductHelpCorpusBuilder.Build(Root, "r");
        var chunks = corpus.Documents
            .Where(d => d.Path.StartsWith("docs/help/faces", StringComparison.Ordinal))
            .ToList();

        Assert.True(chunks.Count >= 10, $"only {chunks.Count} faces chunks were produced");
        // Section-aware: a chunk knows the heading it came from, so a citation
        // can name one.
        Assert.All(chunks, c => Assert.Equal(RagDomainKey.ProductHelp.Value, corpus.Domain));
        Assert.True(
            chunks.Count(c => !string.IsNullOrWhiteSpace(c.Section)) >= chunks.Count - 2,
            "nearly every faces chunk should carry its section heading");
    }

    /// The value of one key in the Italian locale, read from the file the page
    /// actually imports.
    private static string LocaleValue(string key)
    {
        foreach (var line in ItalianLocale.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith($"'{key}':", StringComparison.Ordinal)) continue;
            var value = trimmed[($"'{key}':".Length)..].Trim().TrimEnd(',').Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                // The locale escapes some characters as \uXXXX.
                return System.Text.RegularExpressions.Regex.Unescape(value[1..^1]);
            }
            return value;
        }
        return string.Empty;
    }
}

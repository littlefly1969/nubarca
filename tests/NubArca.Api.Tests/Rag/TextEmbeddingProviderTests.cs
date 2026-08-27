using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The local text-embedding seam.
//
// The contract is tested with the deterministic provider, which needs no
// weights and runs in microseconds. The ONNX provider's model-dependent
// behaviour is validated by `rag validate-model` against real weights — the
// tests here cover what CAN be asserted without them: readiness reasons, the
// query/passage preprocessing seam, and the truncation rule.
public sealed class TextEmbeddingProviderTests
{
    private static readonly AiProfile DeterministicProfile = new()
    {
        Id = Guid.NewGuid(),
        Key = DeterministicTextEmbeddingProvider.ProfileKey,
        Capability = AiCapabilities.TextEmbedding,
        Modality = AiModalities.Text,
        Dimension = DeterministicTextEmbeddingProvider.Dimension,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    // ---- provider contract ---------------------------------------------------

    [Fact]
    public async Task Same_Text_Is_Deterministic_And_Different_Text_Is_Not()
    {
        var provider = new DeterministicTextEmbeddingProvider();

        var a = await provider.EmbedAsync(DeterministicProfile, "come uso i volti?", TextEmbeddingInputKind.Query);
        var again = await provider.EmbedAsync(DeterministicProfile, "come uso i volti?", TextEmbeddingInputKind.Query);
        var other = await provider.EmbedAsync(
            DeterministicProfile, "dove trovo gli album condivisi?", TextEmbeddingInputKind.Query);

        Assert.Equal(a.Vector, again.Vector);
        Assert.NotEqual(a.Vector, other.Vector);
    }

    [Fact]
    public async Task Vectors_Have_The_Profile_Dimension_And_Are_Finite_And_Normalized()
    {
        var provider = new DeterministicTextEmbeddingProvider();
        var result = await provider.EmbedAsync(
            DeterministicProfile, "Apri Volti e scegli Assegna nome.", TextEmbeddingInputKind.Passage);

        Assert.Equal(DeterministicProfile.Dimension, result.Dimension);
        Assert.Equal(DeterministicProfile.Dimension, result.Vector.Length);
        Assert.All(result.Vector, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(1.0, Math.Sqrt(result.Vector.Sum(v => (double)v * v)), 3);
        Assert.Equal(AiDistanceMetrics.Cosine, result.DistanceMetric);
    }

    [Fact]
    public async Task The_Input_Kind_Changes_The_Vector()
    {
        // A provider that ignored the kind would let a bug where every passage
        // is embedded as a query pass every test — and that bug is invisible in
        // anything short of a retrieval-quality measurement.
        var provider = new DeterministicTextEmbeddingProvider();
        const string text = "Apri Volti per assegnare un nome a un gruppo suggerito.";

        var query = await provider.EmbedAsync(DeterministicProfile, text, TextEmbeddingInputKind.Query);
        var passage = await provider.EmbedAsync(DeterministicProfile, text, TextEmbeddingInputKind.Passage);

        Assert.NotEqual(query.Vector, passage.Vector);
    }

    [Fact]
    public async Task Text_With_No_Content_Words_Still_Produces_A_Usable_Vector()
    {
        // An all-zero vector has no cosine direction and is rejected downstream,
        // so a query of nothing but stopwords must not produce one.
        var provider = new DeterministicTextEmbeddingProvider();
        var result = await provider.EmbedAsync(DeterministicProfile, "the a of", TextEmbeddingInputKind.Query);

        Assert.True(result.Vector.Sum(v => Math.Abs(v)) > 0);
    }

    [Fact]
    public void The_Deterministic_Provider_Is_Not_The_Onnx_One()
    {
        // Two providers, and which one runs is decided by the configured
        // PROFILE's model provider — never by registration order.
        Assert.Equal(AiProviders.Deterministic, new DeterministicTextEmbeddingProvider().Provider);
        Assert.Equal(AiProviders.Onnx, Onnx().Provider);
    }

    // ---- model catalogue and preprocessing -----------------------------------

    [Fact]
    public void The_Query_And_Passage_Prefixes_Belong_To_The_Model_Not_To_Rag()
    {
        var config = RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.MultilingualE5SmallKey];

        // These two strings are not decoration: E5 was trained with them, and
        // embedding a passage as a query measurably degrades retrieval while
        // producing a vector that looks entirely normal.
        Assert.Equal("query: ", config.QueryPrefix);
        Assert.Equal("passage: ", config.PassagePrefix);
        Assert.Equal(
            config.QueryPrefix,
            RagTextEmbeddingModels.PrefixFor(config, TextEmbeddingInputKind.Query));
        Assert.Equal(
            config.PassagePrefix,
            RagTextEmbeddingModels.PrefixFor(config, TextEmbeddingInputKind.Passage));
    }

    [Fact]
    public void A_Profile_Resolves_To_Its_Catalogue_Config_By_Hash_Or_By_Key()
    {
        var byKey = RagTextEmbeddingModels.ResolveConfig(
            null, RagTextEmbeddingModels.MultilingualE5SmallProfileKey);
        var byHash = RagTextEmbeddingModels.ResolveConfig(
            RagTextEmbeddingModels.MultilingualE5SmallKey, "some-other-profile");

        Assert.NotNull(byKey);
        Assert.Same(byKey, byHash);
        Assert.Equal(RagTextEmbeddingModels.MultilingualE5SmallDimension, byKey!.Dimension);

        // An unknown profile is an availability answer, never a guess at which
        // model was meant.
        Assert.Null(RagTextEmbeddingModels.ResolveConfig(null, "not-a-profile"));
    }

    [Fact]
    public void Truncation_Keeps_The_Closing_Separator_Token()
    {
        // A plain Take(max) drops the separator the model was trained to expect.
        // The resulting vector is not obviously wrong; it is quietly displaced.
        var ids = Enumerable.Range(1, 20).Select(i => (long)i).ToArray();
        ids[^1] = 2; // the separator
        var mask = Enumerable.Repeat(1L, 20).ToArray();

        var (kept, keptMask) = OnnxTextEmbeddingProvider.Truncate(ids, mask, maxTokens: 8);

        Assert.Equal(8, kept.Length);
        Assert.Equal(8, keptMask.Length);
        Assert.Equal(2, kept[^1]);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, kept[..7]);
        Assert.All(keptMask, m => Assert.Equal(1, m));
    }

    [Fact]
    public void A_Short_Sequence_Is_Left_Alone()
    {
        var ids = new long[] { 1, 5, 9, 2 };
        var mask = new long[] { 1, 1, 1, 1 };

        var (kept, keptMask) = OnnxTextEmbeddingProvider.Truncate(ids, mask, maxTokens: 512);

        Assert.Equal(ids, kept);
        Assert.Equal(mask, keptMask);
    }

    // ---- availability, not failure -------------------------------------------

    [Fact]
    public void A_Missing_Model_Directory_Is_An_Availability_Reason()
    {
        var readiness = Onnx().CheckReadiness(new AiProfile
        {
            Key = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = RagTextEmbeddingModels.MultilingualE5SmallDimension,
            Enabled = true,
        });

        Assert.False(readiness.IsReady);
        Assert.Equal(RagFailureReasons.EmbeddingModelUnavailable, readiness.Reason);
        // Sanitized: a reason code an operator maps to their own configuration,
        // never a filesystem layout.
        Assert.DoesNotContain('/', readiness.Reason!);
    }

    [Fact]
    public void A_Wrong_Capability_Or_Dimension_Is_Refused_Rather_Than_Coerced()
    {
        var provider = Onnx();

        var wrongCapability = provider.CheckReadiness(new AiProfile
        {
            Key = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Text,
            Dimension = 384,
        });
        Assert.Equal(RagFailureReasons.EmbeddingProfileUnavailable, wrongCapability.Reason);

        var wrongDimension = provider.CheckReadiness(new AiProfile
        {
            Key = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = 1152,
        });
        Assert.Equal(RagFailureReasons.EmbeddingDimensionUnsupported, wrongDimension.Reason);
    }

    [Fact]
    public void There_Is_No_Hosted_Embedding_Path_And_Nothing_Downloads_Weights()
    {
        // Asserted on the SOURCE, because the property is an absence: a provider
        // that quietly fell back to an HTTP endpoint would satisfy every
        // behavioural test in this file.
        var source = File.ReadAllText(Path.Combine(
            RagTestHarness.RepositoryRoot(),
            "src/NubArca.Api/Ai/TextEmbeddings/OnnxTextEmbeddingProvider.cs"));

        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", source, StringComparison.Ordinal);

        var directory = Path.Combine(
            RagTestHarness.RepositoryRoot(), "src/NubArca.Api/Ai/TextEmbeddings");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("v1/embeddings", text, StringComparison.Ordinal);
            Assert.DoesNotContain("openrouter", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_Model_Weights_Are_Committed()
    {
        var root = RagTestHarness.RepositoryRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "models")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.onnx", SearchOption.TopDirectoryOnly));
    }

    private static OnnxTextEmbeddingProvider Onnx()
        => new(
            Options.Create(new AiOptions()),
            new UnusableSessionFactory());

    /// The factory is never reached in these tests — readiness fails on the
    /// missing model directory first — but a provider that DID reach it would
    /// fail loudly rather than silently succeed against a real session.
    private sealed class UnusableSessionFactory : IOnnxInferenceSessionFactory
    {
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec)
            => OnnxSessionReadiness.NotReady("test-factory");

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
            => throw new OnnxSessionUnavailableException("test-factory");

        public void EnsureNativeProviderInitialized() { }

        public OnnxNativeCoreState NativeCoreState => default;

        public void Dispose() { }
    }
}

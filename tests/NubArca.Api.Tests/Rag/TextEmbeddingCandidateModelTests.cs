using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// What the benchmark candidates (Qwen3-Embedding-0.6B, Granite R2) need from the
// local provider beyond what E5 needed: last-token pooling, Matryoshka
// truncation, position ids, refusal of KV-cache graphs, a guaranteed trailing
// EOS, and a per-profile evidence floor.
//
// No weights are involved. The provider's work after tokenization is a static
// function of a session, so a fake session that records its inputs and returns
// a known hidden-state tensor proves the tensor plumbing; a hand-written
// WordLevel tokenizer.json proves the EOS path through the real tokenizer
// binding. What this cannot prove is that an exported graph matches the
// reference model: that is the export script's parity check, run where the
// weights are.
public sealed class TextEmbeddingCandidateModelTests
{
    private static TextEmbeddingModelConfig Qwen1024 =>
        RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.Qwen3Embedding06B1024Key];

    private static TextEmbeddingModelConfig Qwen768 =>
        RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.Qwen3Embedding06B768Key];

    // ---- last-token pooling ---------------------------------------------------

    [Fact]
    public void Last_Token_Pooling_Reads_The_Last_Attended_Token_Under_Right_Padding()
    {
        var config = Small(TextEmbeddingPooling.Last, dimension: 3);
        var output = Hidden(tokens: 5, dimension: 3);

        var pooled = OnnxTextEmbeddingProvider.Pool(output, new long[] { 1, 1, 1, 0, 0 }, config);

        // Token 2, not token 4: position T-1 is padding here.
        Assert.Equal(Row(2, 3), pooled);
    }

    [Fact]
    public void Last_Token_Pooling_Reads_The_Final_Position_Under_Left_Padding()
    {
        var config = Small(TextEmbeddingPooling.Last, dimension: 3);
        var output = Hidden(tokens: 5, dimension: 3);

        var pooled = OnnxTextEmbeddingProvider.Pool(output, new long[] { 0, 0, 1, 1, 1 }, config);

        Assert.Equal(Row(4, 3), pooled);
    }

    [Fact]
    public void Last_Token_Pooling_Of_A_Fully_Masked_Sequence_Fails_Rather_Than_Guessing()
    {
        var config = Small(TextEmbeddingPooling.Last, dimension: 3);
        var failure = Assert.Throws<TextEmbeddingUnavailableException>(() =>
            OnnxTextEmbeddingProvider.Pool(Hidden(3, 3), new long[] { 0, 0, 0 }, config));
        Assert.Equal(RagFailureReasons.EmbeddingFailed, failure.ReasonCode);
    }

    [Fact]
    public void Mean_And_Cls_Pooling_Are_Unchanged()
    {
        var output = Hidden(tokens: 3, dimension: 2);

        Assert.Equal(Row(0, 2), OnnxTextEmbeddingProvider.Pool(
            output, new long[] { 1, 1, 1 }, Small(TextEmbeddingPooling.Cls, 2)));

        // Mean of rows 0 and 1 only: row 2 is padding.
        var mean = OnnxTextEmbeddingProvider.Pool(
            output, new long[] { 1, 1, 0 }, Small(TextEmbeddingPooling.Mean, 2));
        Assert.Equal(new[] { (Row(0, 2)[0] + Row(1, 2)[0]) / 2, (Row(0, 2)[1] + Row(1, 2)[1]) / 2 }, mean);
    }

    // ---- Matryoshka -----------------------------------------------------------

    [Fact]
    public void The_Graph_Is_Checked_Against_The_Native_Dimension_Not_The_Stored_One()
    {
        // A 768 profile over a 1024-hidden graph is the point of Matryoshka; a
        // graph that is not 1024 wide is the wrong model.
        var mask = new long[] { 1, 1 };
        Assert.Equal(1024, OnnxTextEmbeddingProvider.Pool(Hidden(2, 1024), mask, Qwen768).Length);

        var failure = Assert.Throws<TextEmbeddingUnavailableException>(() =>
            OnnxTextEmbeddingProvider.Pool(Hidden(2, 768), mask, Qwen768));
        Assert.Equal(RagFailureReasons.EmbeddingDimensionUnsupported, failure.ReasonCode);
    }

    [Fact]
    public void Matryoshka_Truncates_First_And_Normalizes_After()
    {
        var raw = Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(i + 1)).ToArray();

        var full = OnnxTextEmbeddingProvider.Finalize(raw.ToArray(), Qwen1024);
        var cut = OnnxTextEmbeddingProvider.Finalize(raw.ToArray(), Qwen768);

        Assert.Equal(1024, full.Length);
        Assert.Equal(768, cut.Length);
        Assert.Equal(1.0, Norm(full), 5);
        // Normalizing AFTER the cut: the truncated vector is a unit vector,
        // which the first 768 components of the normalized 1024 vector are not.
        Assert.Equal(1.0, Norm(cut), 5);
        Assert.True(Norm(full[..768]) < 0.999);

        // Same direction as the raw prefix.
        var prefixNorm = Math.Sqrt(raw[..768].Sum(v => (double)v * v));
        for (var i = 0; i < 768; i += 97)
        {
            Assert.Equal(raw[i] / prefixNorm, cut[i], 5);
        }
    }

    [Fact]
    public void A_Vector_Already_At_The_Stored_Dimension_Is_Not_Cut()
    {
        var e5 = RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.MultilingualE5SmallKey];
        var raw = Enumerable.Repeat(2f, 384).ToArray();

        var result = OnnxTextEmbeddingProvider.Finalize(raw, e5);

        Assert.Equal(384, result.Length);
        Assert.Equal(1.0, Norm(result), 5);
    }

    // ---- graph inputs ---------------------------------------------------------

    [Fact]
    public void Position_Ids_Are_Fed_Only_When_The_Graph_Declares_Them()
    {
        var decoder = new FakeSession(
            new[] { "input_ids", "attention_mask", "position_ids" }, Hidden(4, 1024));
        OnnxTextEmbeddingProvider.Infer(decoder, new long[] { 5, 6, 7, 3 }, new long[] { 1, 1, 1, 1 }, Qwen1024);

        Assert.Equal(new[] { "input_ids", "attention_mask", "position_ids" }, decoder.FedNames);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, decoder.Fed("position_ids"));

        var encoder = new FakeSession(new[] { "input_ids", "attention_mask" }, Hidden(4, 1024));
        OnnxTextEmbeddingProvider.Infer(encoder, new long[] { 5, 6, 7, 3 }, new long[] { 1, 1, 1, 1 }, Qwen1024);

        Assert.Equal(new[] { "input_ids", "attention_mask" }, encoder.FedNames);
    }

    [Fact]
    public void Position_Ids_Count_Attended_Tokens_Under_Padding()
    {
        Assert.Equal(new long[] { 1, 1, 0, 1, 2 }, OnnxTextEmbeddingProvider.PositionIds(new long[] { 0, 0, 1, 1, 1 }));
        Assert.Equal(new long[] { 0, 1, 2, 1, 1 }, OnnxTextEmbeddingProvider.PositionIds(new long[] { 1, 1, 1, 0, 0 }));
    }

    [Fact]
    public void Token_Type_Ids_Are_Still_Fed_When_Declared()
    {
        var bert = new FakeSession(
            new[] { "input_ids", "attention_mask", "token_type_ids" }, Hidden(3, 384));
        var e5 = RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.MultilingualE5SmallKey];

        OnnxTextEmbeddingProvider.Infer(bert, new long[] { 0, 9, 2 }, new long[] { 1, 1, 1 }, e5);

        Assert.Equal(new[] { "input_ids", "attention_mask", "token_type_ids" }, bert.FedNames);
        Assert.Equal(new long[] { 0, 0, 0 }, bert.Fed("token_type_ids"));
    }

    [Fact]
    public void A_Graph_With_A_Kv_Cache_Is_Refused_Before_Inference()
    {
        var generation = new FakeSession(
            new[] { "input_ids", "attention_mask", "position_ids", "past_key_values.0.key", "past_key_values.0.value" },
            Hidden(2, 1024));

        var failure = Assert.Throws<TextEmbeddingUnavailableException>(() =>
            OnnxTextEmbeddingProvider.Infer(generation, new long[] { 5, 3 }, new long[] { 1, 1 }, Qwen1024));

        // An install problem (wrong export), never a content failure.
        Assert.Equal(RagFailureReasons.EmbeddingModelUnavailable, failure.ReasonCode);
        Assert.Equal(0, generation.Runs);
        Assert.True(OnnxTextEmbeddingProvider.HasKvCacheInputs(new[] { "past.0.key" }));
        Assert.False(OnnxTextEmbeddingProvider.HasKvCacheInputs(new[] { "input_ids", "position_ids" }));
    }

    [Fact]
    public void Infer_Pools_Truncates_And_Normalizes_The_Last_Token()
    {
        var session = new FakeSession(new[] { "input_ids", "attention_mask" }, Hidden(3, 1024));

        var vector = OnnxTextEmbeddingProvider.Infer(session, new long[] { 5, 6, 3 }, new long[] { 1, 1, 1 }, Qwen768);

        var expected = OnnxTextEmbeddingProvider.Finalize(Row(2, 1024), Qwen768);
        Assert.Equal(expected, vector);
    }

    // ---- EOS ------------------------------------------------------------------

    [Fact]
    public void Eos_Is_Appended_When_Missing_And_Not_Duplicated_When_Present()
    {
        var (appended, appendedMask) = OnnxTextEmbeddingProvider.EnsureTrailingEos(
            new long[] { 5, 6, 7 }, new long[] { 1, 1, 1 }, eosId: 3);
        Assert.Equal(new long[] { 5, 6, 7, 3 }, appended);
        Assert.Equal(new long[] { 1, 1, 1, 1 }, appendedMask);

        var (kept, _) = OnnxTextEmbeddingProvider.EnsureTrailingEos(
            new long[] { 5, 6, 3 }, new long[] { 1, 1, 1 }, eosId: 3);
        Assert.Equal(new long[] { 5, 6, 3 }, kept);

        // Padding a tokenizer added is dropped before the check, so EOS ends the
        // ATTENDED sequence rather than following a pad.
        var (unpadded, unpaddedMask) = OnnxTextEmbeddingProvider.EnsureTrailingEos(
            new long[] { 0, 5, 6, 0 }, new long[] { 0, 1, 1, 0 }, eosId: 3);
        Assert.Equal(new long[] { 5, 6, 3 }, unpadded);
        Assert.Equal(new long[] { 1, 1, 1 }, unpaddedMask);
    }

    [Fact]
    public void Truncation_Keeps_Eos_As_The_Pooled_Token()
    {
        var ids = Enumerable.Range(10, 40).Select(i => (long)i).ToArray();
        var (withEos, mask) = OnnxTextEmbeddingProvider.EnsureTrailingEos(
            ids, Enumerable.Repeat(1L, ids.Length).ToArray(), eosId: 3);

        var (kept, keptMask) = OnnxTextEmbeddingProvider.Truncate(withEos, mask, maxTokens: 8);

        Assert.Equal(8, kept.Length);
        Assert.Equal(3, kept[^1]);
        Assert.All(keptMask, m => Assert.Equal(1, m));
    }

    [Fact]
    public async Task The_Provider_Feeds_A_Trailing_Eos_Through_The_Real_Tokenizer()
    {
        using var install = new ModelInstall(tokenizerHasEos: true);
        var session = new FakeSession(new[] { "input_ids", "attention_mask", "position_ids" }, null);
        using var provider = install.Provider(session);

        var result = await provider.EmbedAsync(install.Profile, "ciao mondo", TextEmbeddingInputKind.Passage);

        // WordLevel ids: ciao=1, mondo=2, <|endoftext|>=3. Passages carry no
        // instruction for Qwen3, and the tokenizer added no EOS of its own.
        Assert.Equal(new long[] { 1, 2, 3 }, session.Fed("input_ids"));
        Assert.Equal(new long[] { 0, 1, 2 }, session.Fed("position_ids"));
        Assert.Equal(768, result.Dimension);
        Assert.Equal(768, result.Vector.Length);
        Assert.Equal(1.0, Norm(result.Vector), 4);
    }

    [Fact]
    public async Task A_Tokenizer_That_Does_Not_Know_The_Eos_Token_Is_An_Install_Problem()
    {
        using var install = new ModelInstall(tokenizerHasEos: false);
        var session = new FakeSession(new[] { "input_ids", "attention_mask" }, null);
        using var provider = install.Provider(session);

        var failure = await Assert.ThrowsAsync<TextEmbeddingUnavailableException>(() =>
            provider.EmbedAsync(install.Profile, "ciao mondo", TextEmbeddingInputKind.Passage));

        Assert.Equal(RagFailureReasons.EmbeddingModelUnavailable, failure.ReasonCode);
        Assert.Equal(0, session.Runs);
    }

    // ---- readiness -------------------------------------------------------------

    [Fact]
    public void A_Matryoshka_Profile_Passes_The_Dimension_Check_Only_At_Its_Own_Dimension()
    {
        var provider = new OnnxTextEmbeddingProvider(Options.Create(new AiOptions()), new FakeFactory(null));

        // Right dimension: gets as far as the (absent) model directory.
        var right = provider.CheckReadiness(Profile(RagTextEmbeddingModels.Qwen3Embedding06B768ProfileKey, 768));
        Assert.Equal(RagFailureReasons.EmbeddingModelUnavailable, right.Reason);

        // The native width under the 768 profile is a different vector space.
        var wrong = provider.CheckReadiness(Profile(RagTextEmbeddingModels.Qwen3Embedding06B768ProfileKey, 1024));
        Assert.Equal(RagFailureReasons.EmbeddingDimensionUnsupported, wrong.Reason);
    }

    // ---- catalog ----------------------------------------------------------------

    [Fact]
    public void The_Default_Model_Entry_Is_Unchanged()
    {
        var e5 = RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.MultilingualE5SmallKey];

        Assert.Equal(384, e5.Dimension);
        Assert.Equal(384, e5.GraphDimension);
        Assert.Equal(TextEmbeddingPooling.Mean, e5.Pooling);
        Assert.Equal(512, e5.MaxTokens);
        Assert.Null(e5.EosToken);
        // No own threshold: E5 keeps reading `Rag:MinimumVectorScore`, so an
        // operator's override of it still applies.
        Assert.Null(e5.MinimumVectorScore);
    }

    [Fact]
    public void Qwen3_Entries_State_The_Model_Card_Facts()
    {
        foreach (var config in new[] { Qwen1024, Qwen768 })
        {
            Assert.Equal(RagTextEmbeddingModels.Qwen3Embedding06BModelSubdir, config.ModelSubdir);
            Assert.Equal(
                "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:",
                config.QueryPrefix);
            Assert.Equal("", config.PassagePrefix);
            Assert.Equal(TextEmbeddingPooling.Last, config.Pooling);
            Assert.Equal("<|endoftext|>", config.EosToken);
            Assert.Equal(1024, config.GraphDimension);
            Assert.True(config.Normalize);
        }
        Assert.Equal(1024, Qwen1024.Dimension);
        Assert.Equal(768, Qwen768.Dimension);
    }

    [Fact]
    public void Every_Catalog_Entry_Is_Internally_Consistent()
    {
        foreach (var config in RagTextEmbeddingModels.Catalog.Values)
        {
            Assert.Contains(config.Pooling, new[]
            {
                TextEmbeddingPooling.Mean, TextEmbeddingPooling.Cls, TextEmbeddingPooling.Last,
            });
            Assert.InRange(config.Dimension, 1, config.GraphDimension);
            // A truncated vector must be renormalized, or cosine silently
            // becomes a scaled dot product.
            if (config.Dimension < config.GraphDimension) Assert.True(config.Normalize, config.Key);
            // Last-token pooling without a guaranteed EOS pools whatever token
            // truncation stopped on.
            if (config.Pooling == TextEmbeddingPooling.Last) Assert.False(string.IsNullOrEmpty(config.EosToken));
        }
    }

    [Fact]
    public void Every_Candidate_Is_Pinned_And_Carries_An_Uncalibrated_Gate()
    {
        var candidates = RagTextEmbeddingModels.Catalog.Values
            .Where(c => c.Key != RagTextEmbeddingModels.MultilingualE5SmallKey)
            .ToList();

        Assert.Equal(4, candidates.Count);
        foreach (var config in candidates)
        {
            Assert.False(string.IsNullOrEmpty(config.SourceRepository));
            Assert.Matches("^[0-9a-f]{40}$", config.SourceRevision!);
            Assert.Equal(RagTextEmbeddingModels.UncalibratedMinimumVectorScore, config.MinimumVectorScore);
        }
    }

    [Fact]
    public void Every_Profile_Key_Resolves_To_Its_Catalog_Entry()
    {
        foreach (var (profileKey, catalogKey) in RagTextEmbeddingModels.ProfileToCatalogKey)
        {
            Assert.Same(RagTextEmbeddingModels.Catalog[catalogKey], RagTextEmbeddingModels.ResolveConfig(null, profileKey));
        }
        Assert.Equal(
            RagTextEmbeddingModels.Qwen3Embedding06B768Key,
            RagTextEmbeddingModels.ProfileToCatalogKey[RagTextEmbeddingModels.Qwen3Embedding06B768ProfileKey]);
    }

    // ---- per-profile evidence floor ---------------------------------------------

    [Fact]
    public void The_Evidence_Floor_Is_Per_Profile_With_The_Configured_Fallback()
    {
        // E5 and anything not in the catalog read the configured value.
        Assert.Equal(0.8, RagTextEmbeddingModels.MinimumVectorScoreFor(
            RagTextEmbeddingModels.MultilingualE5SmallProfileKey, 0.8));
        Assert.Equal(0.7, RagTextEmbeddingModels.MinimumVectorScoreFor(
            RagTextEmbeddingModels.MultilingualE5SmallProfileKey, 0.7));
        Assert.Equal(0.8, RagTextEmbeddingModels.MinimumVectorScoreFor(
            DeterministicTextEmbeddingProvider.ProfileKey, 0.8));
        Assert.Equal(0.8, RagTextEmbeddingModels.MinimumVectorScoreFor(null, 0.8));

        // An uncalibrated candidate keeps semantic-only evidence off, whatever
        // the configured fallback says.
        Assert.Equal(1.0, RagTextEmbeddingModels.MinimumVectorScoreFor(
            RagTextEmbeddingModels.Qwen3Embedding06B1024ProfileKey, 0.5));
        Assert.Equal(1.0, RagTextEmbeddingModels.MinimumVectorScoreFor(
            RagTextEmbeddingModels.GraniteEmbedding97MProfileKey, 0.5));
    }

    // ---- seeding ------------------------------------------------------------------

    [Fact]
    public async Task Seeding_Adds_The_Candidates_As_Non_Default_Profiles()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var registry = new AiProfileRegistry(db, TimeProvider.System);

        var first = await registry.SeedRagTextEmbeddingProfilesAsync();
        var again = await registry.SeedRagTextEmbeddingProfilesAsync();

        // Deterministic + E5 + four candidates; idempotent.
        Assert.Equal(6, first.ProfilesCreated);
        Assert.Equal(0, again.ProfilesCreated);

        var expected = new Dictionary<string, (int Dimension, string Config)>
        {
            [RagTextEmbeddingModels.Qwen3Embedding06B1024ProfileKey] = (1024, RagTextEmbeddingModels.Qwen3Embedding06B1024Key),
            [RagTextEmbeddingModels.Qwen3Embedding06B768ProfileKey] = (768, RagTextEmbeddingModels.Qwen3Embedding06B768Key),
            [RagTextEmbeddingModels.GraniteEmbedding311MProfileKey] = (768, RagTextEmbeddingModels.GraniteEmbedding311MKey),
            [RagTextEmbeddingModels.GraniteEmbedding97MProfileKey] = (384, RagTextEmbeddingModels.GraniteEmbedding97MKey),
        };
        foreach (var (key, (dimension, configHash)) in expected)
        {
            var profile = await db.AiProfiles.SingleAsync(p => p.Key == key);
            var model = await db.AiModels.SingleAsync(m => m.Id == profile.AiModelId);
            Assert.False(profile.IsDefault);
            Assert.Equal(dimension, profile.Dimension);
            Assert.Equal(configHash, profile.ConfigHash);
            Assert.Equal(AiCapabilities.TextEmbedding, profile.Capability);
            Assert.Equal(AiProviders.Onnx, model.Provider);
        }

        // Seeding never makes any text-embedding profile the default.
        Assert.DoesNotContain(
            await db.AiProfiles.Where(p => p.Capability == AiCapabilities.TextEmbedding).ToListAsync(),
            p => p.IsDefault);
    }

    // ---- helpers --------------------------------------------------------------------

    private static TextEmbeddingModelConfig Small(string pooling, int dimension) => new(
        Key: "test", ModelSubdir: "test", ModelFile: "model.onnx", TokenizerFile: "tokenizer.json",
        Dimension: dimension, QueryPrefix: "", PassagePrefix: "", MaxTokens: 16,
        Pooling: pooling, Normalize: true);

    /// A [1, T, D] hidden state whose row t is distinguishable from every other.
    private static OnnxOutputTensor Hidden(int tokens, int dimension)
    {
        var data = new float[tokens * dimension];
        for (var t = 0; t < tokens; t++) Array.Copy(Row(t, dimension), 0, data, t * dimension, dimension);
        return new OnnxOutputTensor("last_hidden_state", data, new[] { 1, tokens, dimension });
    }

    private static float[] Row(int t, int dimension)
        => Enumerable.Range(0, dimension).Select(d => (float)(t + 1) * 10 + d * 0.01f).ToArray();

    private static double Norm(IEnumerable<float> vector) => Math.Sqrt(vector.Sum(v => (double)v * v));

    private static AiProfile Profile(string key, int dimension) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Capability = AiCapabilities.TextEmbedding,
        Modality = AiModalities.Text,
        Dimension = dimension,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    /// Records what it was fed; returns a fixed tensor, or one shaped to the
    /// fed sequence when constructed without one.
    private sealed class FakeSession(IReadOnlyList<string> inputNames, OnnxOutputTensor? output) : IOnnxSession
    {
        private readonly Dictionary<string, long[]> _fed = new(StringComparer.Ordinal);

        public IReadOnlyList<string> InputNames { get; } = inputNames;
        public List<string> FedNames { get; } = new();
        public int Runs { get; private set; }

        public long[] Fed(string name) => _fed[name];

        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
        {
            Runs++;
            FedNames.Clear();
            foreach (var input in inputs)
            {
                FedNames.Add(input.Name);
                _fed[input.Name] = input.AsTensor<long>().ToArray();
            }
            return new[] { output ?? Hidden(_fed["input_ids"].Length, 1024) };
        }

        public void Dispose() { }
    }

    private sealed class FakeFactory(IOnnxSession? session) : IOnnxInferenceSessionFactory
    {
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
            => session is null ? throw new OnnxSessionUnavailableException("test-factory") : new Lease(session);

        public void EnsureNativeProviderInitialized() { }

        public OnnxNativeCoreState NativeCoreState => default;

        public void Dispose() { }

        private sealed class Lease(IOnnxSession session) : IOnnxSessionLease
        {
            public IOnnxSession Session { get; } = session;
            public void Dispose() { }
        }
    }

    /// A model directory with an empty model.onnx (never opened: the session
    /// is fake) and a tiny WordLevel tokenizer.json, laid out exactly as the
    /// provider resolves the Qwen3 768 entry.
    private sealed class ModelInstall : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "nubarca-te-" + Guid.NewGuid().ToString("N"));

        public ModelInstall(bool tokenizerHasEos)
        {
            var dir = Path.Combine(_root, RagTextEmbeddingModels.Qwen3Embedding06BModelSubdir);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "model.onnx"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(dir, "tokenizer.json"), Tokenizer(tokenizerHasEos));
        }

        public AiProfile Profile { get; } = TextEmbeddingCandidateModelTests.Profile(
            RagTextEmbeddingModels.Qwen3Embedding06B768ProfileKey, 768);

        public OnnxTextEmbeddingProvider Provider(IOnnxSession session)
        {
            var options = new AiOptions();
            options.Onnx.ModelDir = _root;
            return new OnnxTextEmbeddingProvider(Options.Create(options), new FakeFactory(session));
        }

        private static string Tokenizer(bool withEos)
        {
            var added = withEos
                ? """[{"id":3,"content":"<|endoftext|>","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true}]"""
                : "[]";
            var vocab = withEos
                ? """{"[UNK]":0,"ciao":1,"mondo":2,"<|endoftext|>":3}"""
                : """{"[UNK]":0,"ciao":1,"mondo":2}""";
            return $$$"""
                {"version":"1.0","truncation":null,"padding":null,"added_tokens":{{{added}}},
                 "normalizer":null,"pre_tokenizer":{"type":"Whitespace"},"post_processor":null,"decoder":null,
                 "model":{"type":"WordLevel","vocab":{{{vocab}}},"unk_token":"[UNK]"}}
                """;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}

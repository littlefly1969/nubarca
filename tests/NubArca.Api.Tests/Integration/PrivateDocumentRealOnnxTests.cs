using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using NubArca.Api.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Integration;

// The private path against the REAL model.
//
// Everything else about owner-private retrieval is measured with a deterministic
// provider that hashes text into a vector. That is the right default for a fast
// suite — it is reproducible and costs nothing — and it cannot answer the one
// question a semantic feature exists for: does embedding a QUESTION and a
// PASSAGE with `multilingual-e5-small` actually put them near each other.
//
// So this runs the whole private pipeline on real bytes through real inference:
// extract, chunk, embed as passages, embed the question as a query, rank by
// exact cosine, fuse, gate. And it does it with two owners, because a real model
// producing real neighbours is exactly the condition under which a
// filter-after-search bug would surface — a semantically closer document
// belonging to somebody else is no longer hypothetical.
//
// GATED ON `Ai__Onnx__ModelDir`, with NO fallback path. A default would be an
// installation-specific literal in tracked source, which the identity contract
// refuses and which would make this test silently skip or silently fail
// depending on whose machine it ran on. Unset means skipped, and the completion
// report has to say so rather than claim a lane it did not run.
[Trait("Category", "External")]
public sealed class PrivateDocumentRealOnnxTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly Guid _ownerA = Guid.NewGuid();
    private readonly Guid _ownerB = Guid.NewGuid();

    private static string? ModelDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("Ai__Onnx__ModelDir");
            if (string.IsNullOrWhiteSpace(dir)) return null;

            // The catalogue key is the subdirectory name, exactly as the
            // provider resolves it.
            var model = Path.Combine(dir, RagTextEmbeddingModels.MultilingualE5SmallKey);
            return File.Exists(Path.Combine(model, "model.onnx"))
                   && File.Exists(Path.Combine(model, "tokenizer.json"))
                ? dir
                : null;
        }
    }

    public PrivateDocumentRealOnnxTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();

        _storageRoot = Path.Combine(
            Path.GetTempPath(), "nubarca-onnx-docs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(_storageRoot, 64 * 1024 * 1024);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { }
    }

    [SkippableFact]
    public async Task Real_Onnx_Private_Retrieval_Answers_From_The_Owners_Own_Document()
    {
        var modelDir = ModelDir;
        Skip.If(modelDir is null,
            "Set Ai__Onnx__ModelDir to a directory containing multilingual-e5-small/.");

        var profile = SeedOnnxProfile();
        await SeedLibraryAsync();

        // 1. EXTRACT, CHUNK AND EMBED — real bytes off disk, real inference.
        var indexer = BuildIndexer(modelDir!);
        var outcome = await indexer.IndexOwnerAsync(_ownerA, embed: true);
        await indexer.IndexOwnerAsync(_ownerB, embed: true);

        _output.WriteLine(
            $"indexed: files={outcome.FilesSeen} extracted={outcome.Extracted} "
            + $"chunks={outcome.ChunksCreated} embeddings={outcome.EmbeddingsCreated} "
            + $"profile={outcome.EmbeddingProfileKey} reason={outcome.EmbeddingReason ?? "(none)"}");

        Assert.Null(outcome.EmbeddingReason);
        Assert.True(outcome.EmbeddingsCreated > 0, "the real model produced no vectors");

        // Real 384-dimension vectors, finite, and stored canonically.
        var embeddings = await _db.DocumentChunkEmbeddings.ToListAsync();
        Assert.All(embeddings, e =>
        {
            Assert.Equal(RagTextEmbeddingModels.MultilingualE5SmallDimension, e.Dimension);
            Assert.Equal(e.Dimension * 4, e.EmbeddingBytes.Length);
            var vector = new AiVectorSerializer().Deserialize(e.EmbeddingBytes, e.Dimension);
            Assert.All(vector, v => Assert.True(float.IsFinite(v)));
            Assert.NotEqual(0.0, vector.Sum(v => Math.Abs(v)), 6);
        });

        // 2. RETRIEVE — the question embedded as a QUERY, not as a passage.
        var retriever = BuildRetriever(modelDir!);
        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.UserDocuments,
            "Ogni quanto devo pulire il filtro secondo il mio manuale?",
            _ownerA, 5, 8000));

        _output.WriteLine(
            $"retrieval: outcome={result.Outcome} mode={result.Mode} "
            + $"evidence={result.Evidence.Count} profile={result.EmbeddingProfileKey}");
        foreach (var evidence in result.Evidence)
        {
            _output.WriteLine($"  {evidence.Title} — {evidence.Section} (rank {evidence.FusionRank})");
        }

        Assert.Equal(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Equal(RagRetrievalModes.Hybrid, result.Mode);
        Assert.Equal(RagTextEmbeddingModels.MultilingualE5SmallProfileKey, result.EmbeddingProfileKey);

        // The owner's own manual answers, and nothing of owner B's appears —
        // with real neighbours, which is the condition that matters.
        Assert.Equal("manuale-caldaia.md", result.Evidence[0].Title);
        Assert.All(result.Evidence, e =>
        {
            Assert.Equal(_ownerA, e.OwnerUserId);
            Assert.DoesNotContain("OWNER_B", e.Text, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Real_Onnx_Semantic_Finds_A_Paraphrase_The_Lexical_Path_Would_Miss()
    {
        var modelDir = ModelDir;
        Skip.If(modelDir is null,
            "Set Ai__Onnx__ModelDir to a directory containing multilingual-e5-small/.");

        var profile = SeedOnnxProfile();
        await SeedLibraryAsync();
        await BuildIndexer(modelDir!).IndexOwnerAsync(_ownerA, embed: true);

        // A question sharing almost NO vocabulary with the document: the manual
        // says "filtro", "pulito", "caldaia"; the question says "manutenzione",
        // "impianto", "riscaldamento". This is what the embedding is for, and
        // the deterministic provider cannot demonstrate it.
        var result = await BuildRetriever(modelDir!).RetrieveAsync(new RagQuery(
            RagDomainKey.UserDocuments,
            "manutenzione periodica dell'impianto di riscaldamento",
            _ownerA, 5, 8000));

        _output.WriteLine($"paraphrase: outcome={result.Outcome} mode={result.Mode} "
            + $"evidence={result.Evidence.Count}");
        foreach (var evidence in result.Evidence)
        {
            _output.WriteLine($"  {evidence.Title} vector-rank={evidence.VectorRank} "
                + $"lexical-rank={evidence.LexicalRank?.ToString() ?? "-"}");
        }

        Assert.Equal(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Contains(result.Evidence, e => e.Title == "manuale-caldaia.md");
        // The manual was reached by the VECTOR path — otherwise this measures
        // the lexical index and the model is decoration.
        Assert.Contains(
            result.Evidence,
            e => e.Title == "manuale-caldaia.md" && e.VectorRank is not null);
    }

    [SkippableFact]
    public async Task Real_Onnx_Vectors_Never_Cross_Owners()
    {
        var modelDir = ModelDir;
        Skip.If(modelDir is null,
            "Set Ai__Onnx__ModelDir to a directory containing multilingual-e5-small/.");

        SeedOnnxProfile();
        await SeedLibraryAsync();
        var indexer = BuildIndexer(modelDir!);
        await indexer.IndexOwnerAsync(_ownerA, embed: true);
        await indexer.IndexOwnerAsync(_ownerB, embed: true);

        // Owner B's document is about the SAME subject, so with a real model its
        // vectors sit right next to owner A's question. Asked as B, owner A's
        // manual must not appear, and the reverse.
        var retriever = BuildRetriever(modelDir!);
        const string question = "Ogni quanto devo pulire il filtro secondo il mio manuale?";

        var asA = await retriever.RetrieveAsync(
            new RagQuery(RagDomainKey.UserDocuments, question, _ownerA, 5, 8000));
        var asB = await retriever.RetrieveAsync(
            new RagQuery(RagDomainKey.UserDocuments, question, _ownerB, 5, 8000));

        Assert.All(asA.Evidence, e =>
            Assert.DoesNotContain("OWNER_B", e.Text, StringComparison.Ordinal));
        Assert.All(asB.Evidence, e =>
            Assert.DoesNotContain("OWNER_A", e.Text, StringComparison.Ordinal));
    }

    // ---- wiring -------------------------------------------------------------

    private AiOptions OnnxOptions(string modelDir) => new()
    {
        Enabled = true,
        Provider = AiProviders.Onnx,
        Onnx = new AiOnnxOptions { ModelDir = modelDir },
    };

    private IOptions<RagOptions> RagOptions() => Options.Create(new NubArca.Api.Rag.RagOptions
    {
        // Explicit for THIS domain, both halves. An owner-private domain never
        // inherits, so an installation-wide setting would leave this off.
        Domains =
        {
            [RagDomains.UserDocuments] = new RagDomainSemanticOptions
            {
                SemanticEnabled = true,
                TextEmbeddingProfileKey = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
            },
        },
    });

    private TextEmbeddingResolver Embeddings(string modelDir)
    {
        var ai = Options.Create(OnnxOptions(modelDir));
        var provider = new OnnxTextEmbeddingProvider(
            ai, new OnnxInferenceSessionFactory(ai, NullLogger<OnnxInferenceSessionFactory>.Instance));

        return new TextEmbeddingResolver(
            _db,
            new ITextEmbeddingProvider[] { provider },
            new RagSemanticProfileResolver(RagDomainRegistry.Instance, RagOptions()));
    }

    private OwnerDocumentIndexer BuildIndexer(string modelDir)
        => new(
            _db,
            _storage,
            Embeddings(modelDir),
            new AiVectorSerializer(),
            new DocumentExtractionProviders(
                new IDocumentExtractionProvider[] { new NativeTextExtractionProvider() }),
            Options.Create(new DocumentExtractionOptions()),
            TimeProvider.System,
            NullLogger<OwnerDocumentIndexer>.Instance);

    private IRagRetriever BuildRetriever(string modelDir)
    {
        var options = RagOptions();
        var embeddings = Embeddings(modelDir);
        var serializer = new AiVectorSerializer();
        var corpus = new OwnerDocumentCorpusSource(_db);
        var vectorIndex = new RagVectorIndexService(_db, serializer, TimeProvider.System);

        return new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(
                new DatabaseRagCorpusSource(_db),
                new RagVectorRetriever(embeddings, vectorIndex, options),
                embeddings,
                vectorIndex,
                corpus,
                new OwnerDocumentVectorRetriever(_db, corpus, embeddings, serializer)),
            new BundledProductHelpCorpusSource(ProductHelpCorpus.Empty),
            new RagLexicalIndexCache(),
            options,
            new RagSemanticProfileResolver(RagDomainRegistry.Instance, options),
            NullLogger<RagRetriever>.Instance);
    }

    private AiProfile SeedOnnxProfile()
    {
        var config = RagTextEmbeddingModels.Catalog[RagTextEmbeddingModels.MultilingualE5SmallKey];
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = config.Key,
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = config.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = config.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            ConfigHash = config.Key,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
    }

    // ---- the library --------------------------------------------------------

    private async Task SeedLibraryAsync()
    {
        AddUser(_ownerA);
        AddUser(_ownerB);
        await _db.SaveChangesAsync();

        await AddFileAsync(_ownerA, "manuale-caldaia.md", """
            # Manuale della caldaia

            OWNER_A_PRIVATE_SENTINEL

            ## Pulizia del filtro

            Il filtro dell'acqua va pulito ogni sei mesi. Chiudere il rubinetto di
            ingresso, svitare il corpo del filtro e sciacquare la cartuccia sotto
            acqua corrente fino a rimuovere ogni residuo visibile.

            ## Controllo della pressione

            La pressione dell'impianto deve restare fra 1,2 e 1,5 bar a freddo.
            Se scende sotto 1 bar occorre reintegrare l'acqua dal rubinetto di
            caricamento fino a riportarla nell'intervallo corretto.
            """);

        await AddFileAsync(_ownerA, "appunti-viaggio.md", """
            # Appunti di viaggio

            Prima di partire controllare la scadenza del passaporto e portare una
            fotocopia della carta d'identità. Il biglietto del treno per Lisbona è
            prenotato per le sette del mattino e l'albergo si trova vicino alla
            stazione centrale, con la colazione inclusa nel prezzo della camera.
            """);

        // Owner B, on the SAME subject. With a real model this sits close to
        // owner A's question in the vector space — which is the point.
        await AddFileAsync(_ownerB, "caldaia-appunti.md", """
            # Note sulla caldaia

            OWNER_B_PRIVATE_SENTINEL

            ## Manutenzione del filtro

            Il filtro della caldaia va pulito periodicamente, indicativamente ogni
            sei mesi, chiudendo prima il rubinetto e sciacquando poi la cartuccia
            sotto acqua corrente per rimuovere i residui accumulati.
            """);
    }

    private void AddUser(Guid id) => _db.Users.Add(new User
    {
        Id = id,
        Email = $"owner-{id:N}@example.invalid",
        DisplayName = "Owner",
        CreatedAt = DateTime.UtcNow,
    });

    private async Task AddFileAsync(Guid owner, string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        var written = await _storage.WriteAsync(stream);

        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = written.Sha256,
            StorageKey = written.StorageKey,
            SizeBytes = written.SizeBytes,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.BlobObjects.Add(blob);
        _db.FileItems.Add(new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "text/markdown",
            SizeBytes = written.SizeBytes,
            MediaLibraryState = MediaLibraryState.Active,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        });
        await _db.SaveChangesAsync();
    }
}

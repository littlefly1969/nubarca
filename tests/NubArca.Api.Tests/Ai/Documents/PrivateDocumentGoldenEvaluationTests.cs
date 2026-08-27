using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Evaluation;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Ai.Documents;

// Does owner-private retrieval actually FIND things?
//
// The isolation tests prove nothing of anybody else's comes back. That is
// satisfied perfectly by a corpus that returns nothing at all, which is why this
// file exists: a synthetic library of one person's documents, six questions of
// the shapes a person actually asks, and the same three metrics the system
// domains are measured with.
//
// The corpus is SYNTHETIC and non-secret on purpose — a benchmark made of real
// private documents could not be committed, and one made of documents written to
// match the queries would measure nothing. The questions are written first, in
// the words somebody would type, and the documents are written as the documents
// would be.
//
// The floors below are deliberately LOOSE. They are a regression tripwire, not
// a target: six questions is a small set, and tuning weights until a small set
// scores well moves the number and not the product. The measured values are
// reported so a change that moves them is visible.
public sealed class PrivateDocumentGoldenEvaluationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherOwner = Guid.NewGuid();
    private AiProfile _extraction = null!;

    public PrivateDocumentGoldenEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- the golden set -----------------------------------------------------

    /// Six question SHAPES, each one a different way of asking.
    ///
    /// They are not variations on one query: an exact-phrase lookup, a
    /// paraphrase that shares almost no vocabulary with the document, a question
    /// by filename, a multi-sentence question, an exact configuration key, and
    /// one the corpus genuinely cannot answer.
    private static IReadOnlyList<RagGoldenCase> Cases =>
        new[]
        {
            // Exact phrase from the document.
            Case("ogni quanto va pulito il filtro della caldaia", "manuale-caldaia.md"),

            // Paraphrase: "manutenzione periodica" and "impianto di
            // riscaldamento" do not appear in the manual at all.
            Case("manutenzione periodica dell'impianto di riscaldamento", "manuale-caldaia.md"),

            // By filename — a person often remembers what the document is called
            // and nothing else about it.
            Case("cosa dice il documento appunti-viaggio", "appunti-viaggio.md"),

            // A multi-sentence question, the shape somebody types when they are
            // describing a situation rather than searching.
            Case(
                "devo rifare il passaporto prima di partire, cosa avevo annotato sui documenti "
                + "necessari per il viaggio?",
                "appunti-viaggio.md"),

            // An exact configuration key. Vectors are worse at this and the
            // lexical path is why it is kept first-class.
            Case("NUBARCA_STORAGE_ROOT", "note-configurazione.md"),

            // Deliberately unanswerable by this corpus.
            Case("quali sono gli orari del museo egizio di torino"),
        };

    private static RagGoldenCase Case(string query, params string[] expected)
        => new(RagDomains.UserDocuments, RagLanguages.Italian, query, expected,
            Array.Empty<string>(), Conceptual: false);

    // ---- measurement --------------------------------------------------------

    [Fact]
    public async Task Private_Lexical_Retrieval_Finds_The_Right_Document()
    {
        var answerable = Cases.Where(c => c.ExpectedSourcePrefixes.Count > 0).ToList();
        var report = await EvaluateAsync(answerable, semantic: false);

        Report("lexical", report);

        // A tripwire, not a target. Anything below these means retrieval stopped
        // working rather than "scored slightly worse".
        Assert.Equal(RagRetrievalModes.Lexical, report.Mode);
        Assert.True(report.RecallAtFive >= 0.60,
            $"private lexical Recall@5 fell to {report.RecallAtFive:F3}");
        Assert.True(report.MeanReciprocalRank >= 0.50,
            $"private lexical MRR fell to {report.MeanReciprocalRank:F3}");
        Assert.True(report.TopThreePassed >= 3,
            $"private lexical top-3 fell to {report.TopThreePassed}/{report.Queries}");
    }

    [Fact]
    public async Task Private_Hybrid_Retrieval_Is_At_Least_As_Good()
    {
        // The deterministic embedding provider is NOT a semantic model — it
        // hashes text into a vector — so this measures the PLUMBING, not
        // semantic quality: that hybrid runs, that fusion does not lose the
        // lexical hits, and that the mode is reported honestly. Real semantic
        // quality is measured against multilingual-e5-small outside the fast
        // suite, for the same reason the repository domain is.
        var answerable = Cases.Where(c => c.ExpectedSourcePrefixes.Count > 0).ToList();
        var lexical = await EvaluateAsync(answerable, semantic: false);
        var hybrid = await EvaluateAsync(answerable, semantic: true);

        Report("hybrid", hybrid);

        Assert.Equal(RagRetrievalModes.Hybrid, hybrid.Mode);
        Assert.Equal("rag-text-deterministic-v1", hybrid.EmbeddingProfileKey);
        // Fusion must not LOSE what lexical already found. A drop here means RRF
        // is displacing correct results with vector noise.
        Assert.True(hybrid.RecallAtFive >= lexical.RecallAtFive - 0.001,
            $"hybrid Recall@5 {hybrid.RecallAtFive:F3} is below lexical {lexical.RecallAtFive:F3}");
    }

    [Fact]
    public async Task An_Unanswerable_Question_Returns_No_Strong_Evidence()
    {
        // The other half of a benchmark. A corpus that answers everything is not
        // retrieving, it is guessing — and for "answer from MY documents", a
        // confident answer with nothing behind it is the worst outcome.
        var retriever = Build(semantic: false);
        var unanswerable = Cases.Single(c => c.ExpectedSourcePrefixes.Count == 0);

        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.UserDocuments, unanswerable.Query, _owner, 5, 8000));

        Assert.NotEqual(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task Every_Expected_Document_Actually_Exists()
    {
        // A rename must not silently invalidate half the set: a case expecting a
        // document nobody has scores zero forever and looks like a retrieval
        // regression.
        var names = await _db.FileItems
            .Where(f => f.OwnerUserId == _owner)
            .Select(f => f.Name)
            .ToListAsync();

        foreach (var golden in Cases.SelectMany(c => c.ExpectedSourcePrefixes))
        {
            Assert.Contains(golden, names);
        }
    }

    [Fact]
    public async Task The_Other_Owner_Scores_Nothing_On_This_Set()
    {
        // The same six questions asked as somebody who has none of these
        // documents. Every metric must be zero — which is also a check that the
        // evaluation is genuinely owner-scoped rather than measuring a shared
        // corpus.
        var answerable = Cases.Where(c => c.ExpectedSourcePrefixes.Count > 0).ToList();
        var report = await EvaluateAsync(answerable, semantic: false, owner: _otherOwner);

        Assert.Equal(0.0, report.RecallAtFive);
        Assert.Equal(0.0, report.MeanReciprocalRank);
        Assert.Equal(0, report.TopThreePassed);
    }

    private void Report(string mode, RagEvaluationReport report)
    {
        _output.WriteLine(
            $"user-documents {mode}: Recall@5 {report.RecallAtFive:F3} "
            + $"MRR {report.MeanReciprocalRank:F3} "
            + $"top-3 {report.TopThreePassed}/{report.Queries}");
        foreach (var outcome in report.Outcomes)
        {
            _output.WriteLine(
                $"  rank={outcome.FirstExpectedRank} \"{outcome.Case.Query}\" "
                + $"→ [{string.Join(", ", outcome.TopSources.Take(3))}]");
        }
    }

    private Task<RagEvaluationReport> EvaluateAsync(
        IReadOnlyList<RagGoldenCase> cases, bool semantic, Guid? owner = null)
        => new RagEvaluator(Build(semantic))
            .EvaluateAsync(RagDomains.UserDocuments, cases, ownerUserId: owner ?? _owner);

    private IRagRetriever Build(bool semantic)
    {
        var options = Options.Create(new RagOptions
        {
            Domains = semantic
                ? new(StringComparer.OrdinalIgnoreCase)
                {
                    [RagDomains.UserDocuments] = new()
                    {
                        SemanticEnabled = true,
                        TextEmbeddingProfileKey = "rag-text-deterministic-v1",
                    },
                }
                : new(StringComparer.OrdinalIgnoreCase),
        });

        var semanticResolver = new RagSemanticProfileResolver(RagDomainRegistry.Instance, options);
        var embeddings = new TextEmbeddingResolver(
            _db,
            new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
            semanticResolver);
        var serializer = new AiVectorSerializer();
        var corpus = new OwnerDocumentCorpusSource(_db);

        return new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(
                new DatabaseRagCorpusSource(_db),
                new RagVectorRetriever(
                    embeddings, new RagVectorIndexService(_db, serializer, TimeProvider.System),
                    options),
                embeddings,
                new RagVectorIndexService(_db, serializer, TimeProvider.System),
                corpus,
                new OwnerDocumentVectorRetriever(_db, corpus, embeddings, serializer)),
            new BundledProductHelpCorpusSource(ProductHelpCorpus.Empty),
            new RagLexicalIndexCache(),
            options,
            semanticResolver,
            NullLogger<RagRetriever>.Instance);
    }

    // ---- the synthetic corpus ----------------------------------------------

    private void Seed()
    {
        AddUser(_owner);
        AddUser(_otherOwner);
        _extraction = SeedExtractionProfile();
        SeedEmbeddingProfile();

        Indexed(_owner, "manuale-caldaia.md", "Manutenzione › Pulizia del filtro",
            "Il filtro dell'acqua della caldaia va pulito ogni sei mesi. Chiudere il "
            + "rubinetto di ingresso, svitare il corpo del filtro e sciacquare la cartuccia "
            + "sotto acqua corrente. Controllare inoltre che la pressione dell'impianto "
            + "resti fra 1,2 e 1,5 bar a freddo, reintegrando l'acqua dal rubinetto di "
            + "caricamento quando scende sotto un bar.");

        Indexed(_owner, "appunti-viaggio.md", "Documenti e prenotazioni",
            "Prima di partire controllare la scadenza del passaporto e portare una "
            + "fotocopia della carta d'identità. Il biglietto del treno per Lisbona è "
            + "prenotato per le sette del mattino e l'albergo si trova vicino alla "
            + "stazione centrale, con colazione inclusa nel prezzo della camera.");

        Indexed(_owner, "note-configurazione.md", "Variabili di ambiente",
            "La cartella dei dati è indicata da NUBARCA_STORAGE_ROOT e deve puntare a un "
            + "volume dedicato. La porta di ascolto predefinita è 8080 e il livello di log "
            + "è impostato su info. I backup vengono scritti nella cartella indicata da "
            + "BACKUP_DIR, che non deve trovarsi sul filesystem di root.");

        Indexed(_owner, "note-progetto.md", "Riunioni",
            "La riunione settimanale si tiene il martedì mattina. Le decisioni prese "
            + "vengono registrate nel verbale e le attività assegnate ai responsabili con "
            + "una scadenza concordata durante l'incontro.");

        Indexed(_owner, "ricette.md", "Primi piatti",
            "Per il risotto ai funghi tostare il riso a secco, sfumare con il vino bianco "
            + "e aggiungere il brodo caldo un mestolo alla volta, mescolando fino a "
            + "completa cottura. Mantecare fuori dal fuoco con burro e parmigiano.");

        // The other owner has a library too, so "found nothing" for them is a
        // measured result rather than an empty database.
        Indexed(_otherOwner, "altro.md", "Note",
            "Appunti personali di un altro utente, senza alcun rapporto con la caldaia, "
            + "il viaggio o la configurazione descritti negli altri documenti.");

        _db.SaveChanges();
    }

    private void AddUser(Guid id) => _db.Users.Add(new User
    {
        Id = id,
        Email = $"owner-{id:N}@example.invalid",
        DisplayName = "Owner",
        CreatedAt = DateTime.UtcNow,
    });

    private AiProfile SeedExtractionProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeModelKey,
            Provider = AiProviders.None,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
    }

    private void SeedEmbeddingProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "rag-text-deterministic-v1-model",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "rag-text-deterministic-v1",
            AiModelId = model.Id,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void Indexed(Guid owner, string name, string heading, string body)
    {
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = body.Length,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.BlobObjects.Add(blob);

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "text/markdown",
            SizeBytes = body.Length,
            MediaLibraryState = MediaLibraryState.Active,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        _db.FileItems.Add(file);

        var document = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            SourceBlobObjectId = blob.Id,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            TextHash = RagHash.Sha256Hex(body),
            Text = body,
            CharCount = body.Length,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DocumentTexts.Add(document);

        _db.DocumentChunks.Add(new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            Ordinal = 1,
            Heading = heading,
            Text = body,
            TextHash = RagHash.Sha256Hex(body),
            CreatedAt = DateTime.UtcNow,
        });
    }
}

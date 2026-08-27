using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Assistant;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// TWO THINGS THAT WERE TRUE BY COINCIDENCE.
//
// Owner provenance and the in-memory ceiling both looked correct from the
// outside and were not. Evidence carried the right owner because the query had
// been scoped correctly — so the Assistant's gate, which exists to catch a
// query that WASN'T, was comparing the caller against a copy of the caller.
// And a private corpus was the one index in the system nothing bounded, because
// the ceiling is enforced by the indexer and a private corpus never goes near
// it.
//
// Neither had a failing test, because a passing system produces the same
// observable answer either way. These are the tests that can tell.
public sealed class OwnerProvenanceAndCeilingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private AiProfile _extraction = null!;

    public OwnerProvenanceAndCeilingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();

        AddUser(_owner);
        AddUser(_other);
        _extraction = SeedExtractionProfile();
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- owner provenance comes from the corpus -----------------------------

    [Fact]
    public async Task Private_Chunks_Carry_The_Live_Owner()
    {
        Indexed(_owner, "manuale.md", "Filtro", Body);

        var corpus = await new OwnerDocumentCorpusSource(_db).LoadAsync(_owner);

        Assert.NotEmpty(corpus.Chunks);
        Assert.All(corpus.Chunks, c => Assert.Equal(_owner, c.OwnerUserId));
    }

    [Fact]
    public async Task Private_Owner_Is_Read_From_The_File_Not_From_The_Chunk()
    {
        // The denormalized column and the live file disagree. Whichever one the
        // corpus stamps is the one the Assistant gate will end up trusting, so
        // this pins it to the live row — the same row the eligibility predicate
        // was actually enforced against.
        Indexed(_owner, "manuale.md", "Filtro", Body);

        // A chunk that claims the other owner while its file still says `_owner`
        // must not be in this corpus at all — and must certainly not drag that
        // claim into the evidence.
        var chunk = await _db.DocumentChunks.SingleAsync();
        chunk.OwnerUserId = _other;
        await _db.SaveChangesAsync();

        var corpus = await new OwnerDocumentCorpusSource(_db).LoadAsync(_owner);

        Assert.Empty(corpus.Chunks);
    }

    [Fact]
    public async Task Evidence_Owner_Comes_From_The_Chunk_Not_From_The_Query()
    {
        // THE REGRESSION TEST FOR THE CIRCULAR STAMP.
        //
        // A SYSTEM domain — whose chunks carry no owner — queried with an owner
        // on the request. The old BuildEvidence copied `query.OwnerUserId` onto
        // every piece of evidence, so this evidence came back stamped with a
        // person who has nothing to do with it, and the Assistant's gate would
        // have accepted a system chunk into a private answer. Reading the owner
        // off the chunk makes it null, which is what the gate refuses.
        var retriever = ProductHelpRetriever();

        var result = await retriever.RetrieveAsync(new RagQuery(
            new RagDomainKey(RagDomains.ProductHelp),
            "Come si usa la ricerca semantica delle foto?",
            _owner, 5, 8000));

        Assert.NotEmpty(result.Evidence);
        Assert.All(result.Evidence, e => Assert.Null(e.OwnerUserId));
    }

    [Fact]
    public void The_Assistant_Gate_Refuses_Unstamped_Evidence_For_An_Owner_Domain()
    {
        // The second gate, checked independently: even granting that retrieval
        // returned something, evidence with no owner cannot enter a private
        // prompt. Null is refused exactly as firmly as wrong.
        Assert.True(RagDomainRegistry.Instance.TryGet(RagDomains.UserDocuments, out var domain));

        var unstamped = new[] { Evidence(RagDomains.UserDocuments, owner: null) };
        var wrongOwner = new[] { Evidence(RagDomains.UserDocuments, owner: _other) };
        var correct = new[] { Evidence(RagDomains.UserDocuments, owner: _owner) };

        Assert.NotNull(AssistantRagPolicy.Refuse(
            AssistantModelTrust.LocalTrusted, domain, unstamped, _owner));
        Assert.NotNull(AssistantRagPolicy.Refuse(
            AssistantModelTrust.LocalTrusted, domain, wrongOwner, _owner));
        Assert.Null(AssistantRagPolicy.Refuse(
            AssistantModelTrust.LocalTrusted, domain, correct, _owner));
    }

    // ---- the in-memory ceiling ---------------------------------------------

    [Fact]
    public async Task A_Private_Corpus_At_The_Ceiling_Still_Answers()
    {
        // The boundary from below. Exactly `max` chunks is a corpus that fits,
        // and a bound that refused here would be an off-by-one that looked like
        // a privacy feature.
        for (var i = 0; i < 3; i++)
        {
            Indexed(_owner, $"manuale-{i}.md", "Filtro", Body);
        }

        var result = await PrivateRetriever(ceiling: 3).RetrieveAsync(PrivateQuery());

        Assert.Equal(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.NotEmpty(result.Evidence);
    }

    [Fact]
    public async Task A_Private_Corpus_At_The_Ceiling_Plus_One_Is_Refused()
    {
        for (var i = 0; i < 4; i++)
        {
            Indexed(_owner, $"manuale-{i}.md", "Filtro", Body);
        }

        var result = await PrivateRetriever(ceiling: 3).RetrieveAsync(PrivateQuery());

        // Refused, and refused with its OWN reason. `rag_index_unavailable`
        // would send an operator looking for a missing index when what they
        // have is a library that outgrew the configured bound.
        Assert.Equal(RagRetrievalOutcome.Unavailable, result.Outcome);
        Assert.Equal(RagFailureReasons.CorpusTooLarge, result.Reason);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task An_Over_Limit_Private_Corpus_Is_Never_Silently_Truncated()
    {
        // The failure mode the refusal exists to prevent. A `Take(max)` would
        // answer from an arbitrary alphabetical prefix of somebody's library
        // and look, to them, exactly like an answer from all of it.
        for (var i = 0; i < 4; i++)
        {
            Indexed(_owner, $"manuale-{i}.md", "Filtro", Body);
        }

        var result = await PrivateRetriever(ceiling: 3).RetrieveAsync(PrivateQuery());

        Assert.NotEqual(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task An_Over_Limit_Private_Query_Makes_Zero_Model_Calls()
    {
        // The ceiling is upstream of the embedding provider, so an over-limit
        // library costs nothing at all — not one embedding call and then a
        // refusal. The counting provider is the proof: it records every call it
        // is asked to make, and it must record none.
        SeedEmbeddingProfile();
        for (var i = 0; i < 4; i++)
        {
            Indexed(_owner, $"manuale-{i}.md", "Filtro", Body);
        }

        var counting = new CountingEmbeddingProvider();
        var result = await PrivateRetriever(ceiling: 3, provider: counting, semantic: true)
            .RetrieveAsync(PrivateQuery());

        Assert.Equal(RagRetrievalOutcome.Unavailable, result.Outcome);
        Assert.Equal(RagFailureReasons.CorpusTooLarge, result.Reason);
        Assert.Equal(0, counting.Calls);
    }

    [Fact]
    public async Task A_Within_Limit_Private_Query_Does_Reach_The_Embedding_Provider()
    {
        // The control for the test above: with semantic on and the corpus
        // inside the bound, the provider IS called. Without this, "zero calls"
        // would also pass if the semantic path were simply never wired up.
        SeedEmbeddingProfile();
        Indexed(_owner, "manuale.md", "Filtro", Body);

        var counting = new CountingEmbeddingProvider();
        await PrivateRetriever(ceiling: 100, provider: counting, semantic: true)
            .RetrieveAsync(PrivateQuery());

        Assert.True(counting.Calls > 0);
    }

    // ---- fixture ------------------------------------------------------------

    private const string Body = """
        Il filtro dell'acqua della caldaia va pulito ogni sei mesi. Chiudere il
        rubinetto di ingresso, svitare il corpo del filtro e sciacquare la
        cartuccia sotto acqua corrente fino a rimuovere ogni residuo visibile.
        """;

    private RagQuery PrivateQuery()
        => new(RagDomainKey.UserDocuments,
            "Ogni quanto va pulito il filtro della caldaia?",
            _owner, 5, 8000);

    private static RagEvidence Evidence(string domain, Guid? owner)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            Domain: new RagDomainKey(domain),
            Path: "manuale.md",
            Title: "manuale.md",
            Section: "Filtro",
            Text: Body,
            Feature: string.Empty,
            SourceKind: RagSourceKinds.Documentation,
            Audience: string.Empty,
            Intent: string.Empty,
            Language: RagLanguages.Unknown,
            Score: 1.0,
            SourceKey: "manuale.md",
            Revision: OwnerDocumentCorpusSource.PrivateRevision,
            OwnerUserId: owner);

    private RagRetriever PrivateRetriever(
        int ceiling, ITextEmbeddingProvider? provider = null, bool semantic = false)
    {
        var options = Options.Create(new RagOptions
        {
            MaxIndexedChunks = ceiling,
            SemanticEnabled = semantic,
            TextEmbeddingProfileKey = EmbeddingProfileKey,
            Domains = semantic
                ? new(StringComparer.OrdinalIgnoreCase)
                {
                    [RagDomains.UserDocuments] = new()
                    {
                        SemanticEnabled = true,
                        TextEmbeddingProfileKey = EmbeddingProfileKey,
                    },
                }
                : new(StringComparer.OrdinalIgnoreCase),
        });

        var resolver = new RagSemanticProfileResolver(RagDomainRegistry.Instance, options);
        var embeddings = new TextEmbeddingResolver(
            _db,
            new[] { provider ?? new DeterministicTextEmbeddingProvider() },
            resolver);
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
            resolver,
            NullLogger<RagRetriever>.Instance);
    }

    /// The real Product Help corpus this release ships, so the system-domain
    /// provenance test runs against chunks a corpus source actually produced
    /// rather than ones a test wrote by hand.
    private static RagRetriever ProductHelpRetriever()
        => Tests.Rag.RagTestHarness.ForProductHelp(
            Tests.Rag.RagTestHarness.ShippedProductHelp(RagRetriever.RunningRevision));

    /// Counts what it is asked to embed. The deterministic provider underneath
    /// keeps the vectors real, so a call that DOES happen still produces a
    /// usable result and the control test can prove the path is wired.
    private sealed class CountingEmbeddingProvider : ITextEmbeddingProvider
    {
        private readonly DeterministicTextEmbeddingProvider _inner = new();

        public int Calls { get; private set; }

        public string Provider => _inner.Provider;

        public TextEmbeddingReadiness CheckReadiness(AiProfile profile)
            => _inner.CheckReadiness(profile);

        public Task<TextEmbeddingResult> EmbedAsync(
            AiProfile profile, string text, TextEmbeddingInputKind kind,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.EmbedAsync(profile, text, kind, cancellationToken);
        }
    }

    private const string EmbeddingProfileKey = "rag-text-deterministic-v1";

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
            Modality = AiModalities.Text,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Text,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        return profile;
    }

    private void SeedEmbeddingProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = EmbeddingProfileKey + "-model",
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
            Key = EmbeddingProfileKey,
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
            // The fixture seeds a document as the CURRENT reading of its
            // file, which is what every one of these tests means by
            // "this person has this document indexed".
            IsCurrent = true,
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
        _db.SaveChanges();
    }
}

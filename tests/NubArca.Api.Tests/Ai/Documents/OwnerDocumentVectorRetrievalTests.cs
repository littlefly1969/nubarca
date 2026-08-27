using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// The SEMANTIC half of owner isolation.
//
// Lexical isolation is easy to believe: the index is built from one owner's
// rows. The vector path is where the plausible-looking mistake lives — a global
// index with `WHERE OwnerUserId = …` reads like an owner-prefiltered search and
// is not one, because the traversal happens over everybody's vectors and the
// predicate only filters what it happens to surface.
//
// So the adversarial fixture below gives the OTHER owner vectors that are
// strictly closer to the query than anything the asker has. Under a
// filter-after-search implementation the asker gets fewer, worse results — or
// nothing — and the test says so.
public sealed class OwnerDocumentVectorRetrievalTests : IDisposable
{
    private const int Dimension = DeterministicTextEmbeddingProvider.Dimension;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AiVectorSerializer _serializer = new();
    private readonly OwnerDocumentCorpusSource _corpus;

    private readonly Guid _ownerA = Guid.NewGuid();
    private readonly Guid _ownerB = Guid.NewGuid();
    private AiProfile _profile = null!;
    private PrivateVault _vault = null!;

    public OwnerDocumentVectorRetrievalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();
        _corpus = new OwnerDocumentCorpusSource(_db);
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task OwnerA_VectorQuery_NeverReturnsOwnerB()
    {
        var hits = await SearchAsync(_ownerA);

        Assert.NotEmpty(hits.Hits);
        Assert.All(hits.Hits, h =>
            Assert.DoesNotContain("OWNER_B", h.Chunk.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerB_VectorQuery_NeverReturnsOwnerA()
    {
        var hits = await SearchAsync(_ownerB);

        Assert.NotEmpty(hits.Hits);
        Assert.All(hits.Hits, h =>
            Assert.DoesNotContain("OWNER_A", h.Chunk.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticOwnerFilter_IsAppliedBeforeLimit()
    {
        // ADVERSARIAL. Owner B holds 30 chunks whose vectors are all closer to
        // the query than anything owner A has. A `LIMIT 5` applied after an
        // unfiltered nearest-neighbour pass would return five of B's — and after
        // a post-filter, nothing at all.
        var hits = await SearchAsync(_ownerA, take: 5);

        Assert.NotEmpty(hits.Hits);
        Assert.All(hits.Hits, h =>
            Assert.DoesNotContain("OWNER_B", h.Chunk.Text, StringComparison.Ordinal));
        // The asker's own best match is present, not crowded out.
        Assert.Contains(hits.Hits, h => h.Chunk.Text.Contains("OWNER_A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Vaulted_And_Deleted_Vectors_Are_Not_Candidates()
    {
        // Their embedding rows exist — deliberately — and they contribute
        // nothing, because the candidate query joins the live FileItem.
        Assert.True(await _db.DocumentChunkEmbeddings.CountAsync() > 0);

        var hits = await SearchAsync(_ownerA, take: 50);

        Assert.All(hits.Hits, h =>
        {
            Assert.DoesNotContain("VAULT", h.Chunk.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETED", h.Chunk.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task StaleVectorRow_CannotResurrectADeletedFile()
    {
        // Delete the file now, leaving every derived row behind. The vector is
        // still there and is still unreachable on the very next question.
        var file = await _db.FileItems.SingleAsync(f => f.Name == "a-manual.md");
        file.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var hits = await SearchAsync(_ownerA, take: 50);

        Assert.All(hits.Hits, h =>
            Assert.DoesNotContain("OWNER_A_MANUAL", h.Chunk.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_Owner_Is_Refused_Rather_Than_Answered()
    {
        var hits = await SearchAsync(Guid.Empty);

        Assert.False(hits.IsAvailable);
        Assert.Equal(RagFailureReasons.OwnerRequired, hits.Reason);
        Assert.Empty(hits.Hits);
    }

    [Fact]
    public async Task SemanticDisabled_FallsBackWithoutBroadeningScope()
    {
        var hits = await SearchAsync(_ownerA, semanticEnabled: false);

        Assert.False(hits.IsAvailable);
        Assert.Equal(RagFailureReasons.EmbeddingDisabled, hits.Reason);
        // Degradation returns NOTHING rather than everything. A fallback that
        // widened the scope would be the one bug this domain cannot survive.
        Assert.Empty(hits.Hits);
    }

    [Fact]
    public async Task A_Different_Profile_Contributes_No_Candidates()
    {
        // Two profiles are two coordinate systems and a cosine between them is a
        // number with no meaning. Vectors written under another profile are not
        // reinterpreted — they are not candidates.
        var other = SeedProfile("rag-text-other-v1");
        foreach (var embedding in await _db.DocumentChunkEmbeddings.ToListAsync())
        {
            embedding.ProfileId = other.Id;
        }
        await _db.SaveChangesAsync();

        var hits = await SearchAsync(_ownerA, take: 50);

        Assert.True(hits.IsAvailable);
        Assert.Empty(hits.Hits);
    }

    [Fact]
    public async Task Ties_Are_Deterministic()
    {
        // Two runs must rank identically, or the fusion below is unstable and a
        // golden evaluation measures noise.
        var first = await SearchAsync(_ownerA, take: 10);
        var second = await SearchAsync(_ownerA, take: 10);

        Assert.Equal(
            first.Hits.Select(h => h.Chunk.Id),
            second.Hits.Select(h => h.Chunk.Id));
    }

    // ---- fixture ------------------------------------------------------------

    private async Task<RagVectorSearchOutcome> SearchAsync(
        Guid owner, int take = 10, bool semanticEnabled = true)
    {
        var options = Options.Create(new RagOptions
        {
            Domains = semanticEnabled
                ? new(StringComparer.OrdinalIgnoreCase)
                {
                    [RagDomains.UserDocuments] = new()
                    {
                        SemanticEnabled = true,
                        TextEmbeddingProfileKey = _profile.Key,
                    },
                }
                : new(StringComparer.OrdinalIgnoreCase),
        });

        var retriever = new OwnerDocumentVectorRetriever(
            _db,
            _corpus,
            new TextEmbeddingResolver(
                _db,
                new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
                new RagSemanticProfileResolver(RagDomainRegistry.Instance, options)),
            _serializer);

        var corpus = await _corpus.LoadAsync(owner);
        var index = new RagLexicalIndex(corpus, RagRankingProfiles.For(RagDomainKey.UserDocuments));

        return await retriever.SearchAsync(index, owner, "pulizia del filtro della caldaia", take);
    }

    private void Seed()
    {
        AddUser(_ownerA);
        AddUser(_ownerB);
        _profile = SeedProfile("rag-text-deterministic-v1");

        _vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = _ownerA, CreatedAt = DateTime.UtcNow,
        };
        _db.PrivateVaults.Add(_vault);
        _db.SaveChanges();

        // The query vector, so "closer" and "further" are constructed rather
        // than hoped for.
        var query = Embed("pulizia del filtro della caldaia");

        // Owner A: one ordinary document, deliberately FURTHER from the query
        // than everything owner B has.
        Indexed(_ownerA, "a-manual.md", "OWNER_A OWNER_A_MANUAL pulizia del filtro",
            Perturb(query, 0.45f));
        Indexed(_ownerA, "vault.md", "VAULT pulizia del filtro riservata",
            Perturb(query, 0.01f), vaultId: _vault.Id);
        Indexed(_ownerA, "deleted.md", "DELETED pulizia del filtro cancellata",
            Perturb(query, 0.01f), deleted: true);

        // Owner B: thirty chunks, every one closer than owner A's best.
        for (var i = 0; i < 30; i++)
        {
            Indexed(_ownerB, $"b-notes-{i:D2}.md", $"OWNER_B pulizia del filtro nota {i}",
                Perturb(query, 0.02f + i * 0.001f));
        }

        _db.SaveChanges();
    }

    private void AddUser(Guid id) => _db.Users.Add(new User
    {
        Id = id,
        Email = $"owner-{id:N}@example.invalid",
        DisplayName = "Owner",
        CreatedAt = DateTime.UtcNow,
    });

    private AiProfile SeedProfile(string key)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key + "-model",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = model.Id,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
    }

    private static float[] Embed(string text)
        => new DeterministicTextEmbeddingProvider()
            .EmbedAsync(
                new AiProfile { Dimension = Dimension },
                text,
                TextEmbeddingInputKind.Query)
            .GetAwaiter().GetResult().Vector;

    /// A vector at a controlled distance from `origin`: the larger `amount`, the
    /// further away. Deterministic, so "closer than" is a fact about the fixture
    /// rather than about which text happened to hash where.
    private static float[] Perturb(float[] origin, float amount)
    {
        var result = new float[origin.Length];
        for (var i = 0; i < origin.Length; i++)
        {
            result[i] = origin[i] + (i % 2 == 0 ? amount : -amount);
        }
        return result;
    }

    private void Indexed(
        Guid owner, string name, string body, float[] vector,
        Guid? vaultId = null, bool deleted = false)
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
            PrivateVaultId = vaultId,
            DeletedAt = deleted ? DateTime.UtcNow : null,
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
            ProfileId = _profile.Id,
            SourceBlobObjectId = blob.Id,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            TextHash = new string('a', 64),
            Text = body,
            CharCount = body.Length,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DocumentTexts.Add(document);

        var chunk = new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = _profile.Id,
            Ordinal = 1,
            Heading = "Sezione",
            Text = body,
            TextHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
        };
        _db.DocumentChunks.Add(chunk);

        _db.DocumentChunkEmbeddings.Add(new DocumentChunkEmbedding
        {
            Id = Guid.NewGuid(),
            DocumentChunkId = chunk.Id,
            ProfileId = _profile.Id,
            EmbeddingBytes = _serializer.Serialize(vector, Dimension),
            Dimension = Dimension,
            CreatedAt = DateTime.UtcNow,
        });
    }
}

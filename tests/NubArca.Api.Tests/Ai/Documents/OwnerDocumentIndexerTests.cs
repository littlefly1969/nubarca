using System.Text;
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
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// Turning one person's files into their own corpus, with real bytes on a real
// filesystem.
//
// The property the whole design rests on is the same one the system indexer
// has: running it twice does nothing the second time. What differs is WHY —
// blobs are content-addressed and immutable, so "same bytes" is an id
// comparison rather than a hash of anything, and a rename or a move is
// therefore free by construction rather than by an optimisation.
public sealed class OwnerDocumentIndexerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly Guid _owner = Guid.NewGuid();

    public OwnerDocumentIndexerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();

        _storageRoot = Path.Combine(
            Path.GetTempPath(), "nubarca-docs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(_storageRoot, 64 * 1024 * 1024);

        _db.Users.Add(new User
        {
            Id = _owner,
            Email = $"owner-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { }
    }

    private const string Manual = """
        # Manuale della caldaia

        Questo manuale descrive la manutenzione ordinaria della caldaia installata
        nell'appartamento, comprese le operazioni che il proprietario può eseguire
        senza chiamare un tecnico specializzato.

        ## Pulizia del filtro

        Il filtro dell'acqua va pulito ogni sei mesi. Chiudere il rubinetto di
        ingresso, svitare il corpo del filtro e sciacquare la cartuccia sotto acqua
        corrente fino a rimuovere ogni residuo visibile.
        """;

    // ---- extraction ---------------------------------------------------------

    [Fact]
    public async Task Markdown_IsExtractedChunkedAndOwnerScoped()
    {
        await AddFileAsync("boiler-manual.md", Manual);

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.FilesSeen);
        Assert.Equal(1, outcome.Extracted);
        Assert.True(outcome.ChunksCreated > 0);

        var document = await _db.DocumentTexts.SingleAsync();
        Assert.Equal(_owner, document.OwnerUserId);
        Assert.Equal(DocumentTextSources.Native, document.Source);
        Assert.Equal(AiArtifactStatuses.Completed, document.Status);
        Assert.Contains("ogni sei mesi", document.Text!, StringComparison.Ordinal);
        Assert.Equal(OwnerDocumentChunkFormat.Current, document.ChunkFormatVersion);

        // Every chunk carries the owner itself rather than inheriting one
        // through a join, so an owner-scoped query needs no join to be correct.
        Assert.All(await _db.DocumentChunks.ToListAsync(), c =>
        {
            Assert.Equal(_owner, c.OwnerUserId);
            // Native text has no pages. Null rather than 1: an invented page
            // number is worse than an absent one.
            Assert.Null(c.Page);
        });
    }

    [Fact]
    public async Task Extraction_IsIdempotent()
    {
        await AddFileAsync("boiler-manual.md", Manual);
        var first = await Indexer().IndexOwnerAsync(_owner);
        var chunkIds = await _db.DocumentChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync();

        var second = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.Extracted);
        Assert.Equal(0, second.ChunksCreated);
        Assert.Equal(first.ChunksCreated, await _db.DocumentChunks.CountAsync());
        Assert.Equal(
            chunkIds, await _db.DocumentChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync());
    }

    [Fact]
    public async Task RenameAndMove_DoNotReExtract()
    {
        // A rename is DB-only and leaves the content-addressed blob alone, so
        // the extraction, the chunks and every embedding are still correct. This
        // is the case that would otherwise cost an hour of inference for
        // renaming a folder.
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        var chunkIds = await _db.DocumentChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync();

        file.Name = "manuale-caldaia.md";
        file.ParentFolderId = null;
        file.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal(0, outcome.ChunksCreated);
        Assert.Equal(
            chunkIds, await _db.DocumentChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync());
    }

    [Fact]
    public async Task ContentChange_ProducesANewCurrentExtraction()
    {
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);

        // New bytes are a NEW BLOB — that is what content-addressed storage
        // means — so the file points somewhere else and the extraction is stale.
        var edited = Manual.Replace("ogni sei mesi", "ogni tre mesi", StringComparison.Ordinal);
        file.BlobObjectId = (await WriteBlobAsync(edited)).Id;
        await _db.SaveChangesAsync();

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.Extracted);
        var document = await _db.DocumentTexts.SingleAsync();
        Assert.Contains("ogni tre mesi", document.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("ogni sei mesi", document.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await _db.DocumentChunks.Select(c => c.Text!).ToListAsync(),
            t => t.Contains("ogni sei mesi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Binary_And_Unsupported_Files_Are_Skipped_Permanently()
    {
        await AddFileAsync("photo.jpg", "not really a jpeg but declared as one", mime: "image/jpeg");
        await AddBinaryFileAsync("archive.txt");

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        // The jpeg is not even a candidate — the eligibility query never offers
        // it — so only the mislabelled binary is seen and skipped.
        Assert.Equal(1, outcome.FilesSeen);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.Binary]);

        var document = await _db.DocumentTexts.SingleAsync();
        Assert.Equal(AiArtifactStatuses.Skipped, document.Status);
        Assert.Equal(DocumentExtractionReasons.Binary, document.ErrorCode);
        Assert.Null(document.Text);
        Assert.Empty(await _db.DocumentChunks.ToListAsync());
    }

    [Fact]
    public async Task OversizedFile_IsRejectedBeforeItIsRead()
    {
        // The recorded size disqualifies it, so the blob is never opened. Proven
        // by deleting the bytes from storage: a read would throw.
        var file = await AddFileAsync("huge.md", Manual);
        file.SizeBytes = 900L * 1024 * 1024;
        await _db.SaveChangesAsync();
        DeleteStoredBytes(file.BlobObjectId);

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.TooLarge]);
        Assert.Empty(await _db.DocumentTexts.ToListAsync());
    }

    [Fact]
    public async Task Vault_And_Deleted_Files_Are_Never_Extracted()
    {
        await AddFileAsync("ordinary.md", Manual);
        var vaulted = await AddFileAsync("vaulted.md", Manual);
        var deleted = await AddFileAsync("deleted.md", Manual);

        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = _owner, CreatedAt = DateTime.UtcNow,
        };
        _db.PrivateVaults.Add(vault);
        vaulted.PrivateVaultId = vault.Id;
        deleted.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        // Not "read and discarded" — never offered. Refusing to index a file and
        // refusing to open it are different strengths of the same statement.
        Assert.Equal(1, outcome.FilesSeen);
        var document = await _db.DocumentTexts.SingleAsync();
        Assert.Equal(
            "ordinary.md",
            await _db.FileItems.Where(f => f.Id == document.FileItemId).Select(f => f.Name).SingleAsync());
    }

    [Fact]
    public async Task AnotherOwnersFiles_AreNeverSeen()
    {
        var other = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = other,
            Email = $"other-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Other",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await AddFileAsync("mine.md", Manual);
        await AddFileAsync("theirs.md", Manual, owner: other);

        var outcome = await Indexer().IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.FilesSeen);
        Assert.All(await _db.DocumentTexts.ToListAsync(), d => Assert.Equal(_owner, d.OwnerUserId));
    }

    // ---- embeddings ---------------------------------------------------------

    [Fact]
    public async Task Embeddings_AreProfileScoped_AndNotRederivedWhenUnchanged()
    {
        var profile = SeedEmbeddingProfile();
        await AddFileAsync("boiler-manual.md", Manual);

        var first = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);
        Assert.True(first.EmbeddingsCreated > 0);
        Assert.Null(first.EmbeddingReason);

        var ids = await _db.DocumentChunkEmbeddings.Select(e => e.Id).OrderBy(i => i).ToListAsync();
        Assert.All(
            await _db.DocumentChunkEmbeddings.ToListAsync(),
            e => Assert.Equal(profile.Id, e.ProfileId));

        var second = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(0, second.EmbeddingsCreated);
        Assert.Equal(
            ids,
            await _db.DocumentChunkEmbeddings.Select(e => e.Id).OrderBy(i => i).ToListAsync());
    }

    [Fact]
    public async Task UserDocuments_DoesNotEmbed_WithoutBeingExplicitlyEnabled()
    {
        // The owner-private asymmetry, end to end. Semantic is on
        // installation-wide and this domain still does not embed, because
        // nobody said so about people's own documents.
        SeedEmbeddingProfile();
        await AddFileAsync("boiler-manual.md", Manual);

        var outcome = await Indexer(globalSemantic: true).IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Equal(RagFailureReasons.EmbeddingDisabled, outcome.EmbeddingReason);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task ChangedChunk_DropsItsStaleEmbedding()
    {
        var profile = SeedEmbeddingProfile();
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);
        var before = await _db.DocumentChunkEmbeddings.CountAsync();
        Assert.True(before > 0);

        // Rewrite the FIRST section, so ordinal 1's text hash changes and its
        // old vector describes text that no longer exists anywhere.
        var edited = Manual.Replace(
            "Questo manuale descrive", "Questo manuale riscritto descrive", StringComparison.Ordinal);
        file.BlobObjectId = (await WriteBlobAsync(edited)).Id;
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: false);

        Assert.True(outcome.EmbeddingsRemoved > 0);
        Assert.True(await _db.DocumentChunkEmbeddings.CountAsync() < before);
    }

    [Fact]
    public async Task Without_A_Profile_The_Text_Is_Still_Indexed()
    {
        // Semantic being unavailable is a supported configuration, not an error.
        // The lexical corpus is complete and the reason is reported.
        await AddFileAsync("boiler-manual.md", Manual);

        var outcome = await Indexer(semantic: "missing-profile").IndexOwnerAsync(_owner, embed: true);

        Assert.True(outcome.ChunksCreated > 0);
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Equal(RagFailureReasons.EmbeddingProfileUnavailable, outcome.EmbeddingReason);
    }

    [Fact]
    public async Task An_Owner_Is_Required()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Indexer().IndexOwnerAsync(Guid.Empty));
    }

    [Fact]
    public async Task A_Limited_Run_Is_Bounded()
    {
        await AddFileAsync("a.md", Manual);
        await AddFileAsync("b.md", Manual + "\n\nUn secondo documento con altro contenuto.");
        await AddFileAsync("c.md", Manual + "\n\nUn terzo documento con contenuto diverso.");

        var outcome = await Indexer().IndexOwnerAsync(_owner, limit: 1);

        Assert.Equal(1, outcome.FilesSeen);
        Assert.True(outcome.Partial);
        Assert.Equal(1, await _db.DocumentTexts.CountAsync());
    }

    // ---- fixture ------------------------------------------------------------

    private OwnerDocumentIndexer Indexer(string? semantic = null, bool globalSemantic = false)
    {
        var options = Options.Create(new RagOptions
        {
            SemanticEnabled = globalSemantic || semantic is not null,
            TextEmbeddingProfileKey = semantic ?? "rag-text-deterministic-v1",
            Domains = semantic is null
                ? new Dictionary<string, RagDomainSemanticOptions>(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase)
                {
                    [RagDomains.UserDocuments] = new()
                    {
                        SemanticEnabled = true,
                        TextEmbeddingProfileKey = semantic,
                    },
                },
        });

        return new OwnerDocumentIndexer(
            _db,
            _storage,
            new TextEmbeddingResolver(
                _db,
                new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
                new RagSemanticProfileResolver(RagDomainRegistry.Instance, options)),
            new AiVectorSerializer(),
            Options.Create(new DocumentExtractionOptions()),
            TimeProvider.System,
            NullLogger<OwnerDocumentIndexer>.Instance);
    }

    private AiProfile SeedEmbeddingProfile(string key = "rag-text-deterministic-v1")
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key + "-model",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
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
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
    }

    private async Task<BlobObject> WriteBlobAsync(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        var written = await _storage.WriteAsync(stream);

        var existing = await _db.BlobObjects.FirstOrDefaultAsync(b => b.Sha256 == written.Sha256);
        if (existing is not null) return existing;

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
        await _db.SaveChangesAsync();
        return blob;
    }

    private async Task<FileItem> AddFileAsync(
        string name, string content, string mime = "text/markdown", Guid? owner = null)
    {
        var blob = await WriteBlobAsync(content);
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner ?? _owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = mime,
            SizeBytes = blob.SizeBytes,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        _db.FileItems.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    /// A `.txt` full of NULs. The name says text and the bytes say otherwise,
    /// and the bytes are what decide.
    private async Task AddBinaryFileAsync(string name)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'P';
        bytes[1] = (byte)'K';
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
            OwnerUserId = _owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "text/plain",
            SizeBytes = written.SizeBytes,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        });
        await _db.SaveChangesAsync();
    }

    private void DeleteStoredBytes(Guid blobId)
    {
        var key = _db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.StorageKey).Single();
        var path = Path.Combine(_storageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }
}

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

    // ---- embedding goes through the LIVE boundary ---------------------------
    //
    // The adversarial shape for all of these is the same, and it is the reason
    // they exist: index the document while it is perfectly ordinary, THEN take
    // the file away, then ask for embeddings. The chunks are still sitting in
    // `document_chunks` — deliberately, because cleanup is housekeeping and
    // these tests refuse to let a sweeper be the thing that makes them pass.
    //
    // Retrieval already refuses to read those rows, so nothing here is a leak.
    // What it would be is a person's deleted document quietly acquiring FRESH
    // derived data, produced by local inference, every time the indexer ran —
    // stale rows being re-armed rather than left inert. An embedder that selects
    // candidates by `chunk.OwnerUserId` alone does exactly that.

    [Fact]
    public async Task Embedding_Skips_Chunks_Whose_File_Was_Vaulted()
    {
        var profile = SeedEmbeddingProfile();
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = _owner, CreatedAt = DateTime.UtcNow,
        };
        _db.PrivateVaults.Add(vault);
        file.PrivateVaultId = vault.Id;
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(await _db.DocumentChunks.AnyAsync(), "the stale chunks must still be there");
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task Embedding_Skips_Chunks_Whose_File_Was_Deleted()
    {
        var profile = SeedEmbeddingProfile();
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        file.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(await _db.DocumentChunks.AnyAsync());
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task Embedding_Skips_Chunks_Whose_File_Left_The_Library()
    {
        var profile = SeedEmbeddingProfile();
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        // The owner told NubArca not to process this one. Producing a fresh
        // embedding for it is processing.
        file.MediaLibraryState = MediaLibraryState.Excluded;
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(await _db.DocumentChunks.AnyAsync());
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task Embedding_Skips_Chunks_Whose_Extraction_Never_Completed()
    {
        // ONLY the document status separates this from the happy path: the file
        // is still owned, undeleted, unvaulted and in the library, and its
        // chunks from the earlier successful pass are all still there.
        //
        // Keeping it non-completed takes some care, because a re-index REPAIRS a
        // failed extraction — correctly — and a repaired document is completed
        // and ought to be embedded. So the bytes go away underneath it: storage
        // can no longer be read, extraction is skipped as unreadable rather than
        // reaching a verdict, and the document is still sitting at Failed when
        // the embedding pass looks at it.
        var profile = SeedEmbeddingProfile();
        await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        var document = await _db.DocumentTexts.SingleAsync();
        document.Status = AiArtifactStatuses.Failed;
        await _db.SaveChangesAsync();
        foreach (var path in Directory.EnumerateFiles(
                     _storageRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(
            AiArtifactStatuses.Failed,
            await _db.DocumentTexts.Select(d => d.Status).SingleAsync());
        Assert.True(await _db.DocumentChunks.AnyAsync());
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task Embedding_Skips_Chunks_Stamped_With_This_Owner_But_Owned_By_Another()
    {
        // THE DENORMALIZED COPY IS WRONG, and the live rows are right. Whatever
        // wrote this — a bug, a bad backfill, a restored table — the chunk claims
        // to belong to `_owner` while the document and the file belong to
        // somebody else. Owner-on-the-chunk alone would embed it.
        var profile = SeedEmbeddingProfile();
        var other = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = other,
            Email = $"other-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Other",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await AddFileAsync("theirs.md", Manual, owner: other);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(other);

        foreach (var chunk in await _db.DocumentChunks.ToListAsync())
        {
            chunk.OwnerUserId = _owner;
        }
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(await _db.DocumentChunks.AnyAsync());
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task Embedding_Still_Runs_For_The_Documents_That_Are_Fine()
    {
        // The control. Every refusal above has to be the boundary doing its job
        // rather than embedding being broken, and only this test can say which.
        var profile = SeedEmbeddingProfile();
        var kept = await AddFileAsync("kept.md", Manual);
        var removed = await AddFileAsync(
            "removed.md", Manual + "\n\nUn secondo documento con contenuto diverso.");
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        removed.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(outcome.EmbeddingsCreated > 0);

        var keptDocument = await _db.DocumentTexts
            .Where(d => d.FileItemId == kept.Id).Select(d => d.Id).SingleAsync();
        var embeddedDocuments = await _db.DocumentChunkEmbeddings
            .Join(_db.DocumentChunks, e => e.DocumentChunkId, c => c.Id, (e, c) => c.DocumentTextId)
            .Distinct().ToListAsync();

        Assert.Equal(new[] { keptDocument }, embeddedDocuments);
    }

    // ---- which reading of a file is authority -------------------------------
    //
    // Slice 3 had one extraction profile, so "the row for this file" and "the
    // extraction of this file" were the same sentence and nothing had to choose.
    // Rich ingestion makes several readings of one file ordinary, and the moment
    // that becomes true, a retrieval that resolves authority by timestamp or by
    // query order starts answering questions from a superseded interpretation
    // with no symptom that anything went wrong.

    [Fact]
    public async Task An_Extraction_Becomes_The_Current_Reading_Of_Its_File()
    {
        await AddFileAsync("boiler-manual.md", Manual);

        await Indexer().IndexOwnerAsync(_owner);

        var document = await _db.DocumentTexts.SingleAsync();
        Assert.True(document.IsCurrent);
    }

    [Fact]
    public async Task A_Second_Profiles_Reading_Supersedes_The_First()
    {
        // The shape rich ingestion introduces: the same bytes read again by a
        // different extractor. Both rows are completed and both describe the
        // file honestly — and exactly one of them may answer a question.
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        var first = await _db.DocumentTexts.SingleAsync();

        var second = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _owner,
            ProfileId = SeedSecondExtractionProfile().Id,
            SourceBlobObjectId = first.SourceBlobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            TextHash = first.TextHash,
            Text = first.Text,
            CharCount = first.CharCount,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            IsCurrent = false,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DocumentTexts.Add(second);
        await _db.SaveChangesAsync();

        // Two completed rows, one current. The historical one is provenance, not
        // authority: it records which profile produced what, which is what a
        // later extractor upgrade reads.
        Assert.Equal(2, await _db.DocumentTexts.CountAsync());
        Assert.Equal(
            first.Id,
            await _db.DocumentTexts.Where(d => d.IsCurrent).Select(d => d.Id).SingleAsync());
    }

    [Fact]
    public async Task A_Skip_Verdict_Supersedes_An_Earlier_Successful_Reading()
    {
        // The file was readable, then it was replaced with something that is
        // not. Without the swap, the old extraction of DIFFERENT bytes would
        // stay current and keep answering questions about a document that no
        // longer says any of it.
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        Assert.True(await _db.DocumentTexts.SingleAsync() is { IsCurrent: true });

        var binary = await WriteBlobAsync("\u0000\u0001\u0002 not text at all \u0000");
        file.BlobObjectId = binary.Id;
        file.SizeBytes = binary.SizeBytes;
        await _db.SaveChangesAsync();

        await Indexer().IndexOwnerAsync(_owner);

        var document = await _db.DocumentTexts.SingleAsync();
        Assert.Equal(AiArtifactStatuses.Skipped, document.Status);
        Assert.True(document.IsCurrent);
        // The old bytes stopped being authority, chunks included. A corpus that
        // kept them would answer from a version of the file that is gone.
        Assert.Empty(await _db.DocumentChunks.ToListAsync());
    }

    [Fact]
    public async Task A_Historical_Reading_Is_Neither_Retrieved_Nor_Embedded()
    {
        // The rows are all still there — the chunks of the superseded reading
        // included — and none of them is evidence. This is the property the
        // whole flag exists for, checked through the shared boundary rather than
        // by trusting that something cleaned up.
        var profile = SeedEmbeddingProfile();
        await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner);

        var document = await _db.DocumentTexts.SingleAsync();
        document.IsCurrent = false;
        await _db.SaveChangesAsync();

        var corpus = await new NubArca.Api.Rag.Retrieval.OwnerDocumentCorpusSource(_db)
            .LoadAsync(_owner);
        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        Assert.True(await _db.DocumentChunks.AnyAsync(), "the chunks must still be there");
        Assert.Empty(corpus.Chunks);
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task A_File_Cannot_Hold_Two_Current_Readings()
    {
        // The database refuses it. Application discipline would not be enough:
        // two current rows throw nowhere, they just quietly make one question
        // answerable from two interpretations of the same document.
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        var first = await _db.DocumentTexts.SingleAsync();

        _db.DocumentTexts.Add(new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _owner,
            ProfileId = SeedSecondExtractionProfile().Id,
            SourceBlobObjectId = first.SourceBlobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            IsCurrent = true,
            CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_Owners_Sharing_Bytes_Keep_Independent_Current_Readings()
    {
        // Deduplication is a storage fact. The uniqueness is per FILE, so the
        // same blob held by two people is two files, two extractions and two
        // current rows — a constraint scoped to the blob would have made one
        // person's document supersede another's.
        var other = Guid.NewGuid();
        _db.Users.Add(new User
        {
            Id = other,
            Email = $"other-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Other",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var mine = await AddFileAsync("mine.md", Manual);
        var theirs = await AddFileAsync("theirs.md", Manual, owner: other);
        // Content-addressed storage: identical text is one blob.
        Assert.Equal(mine.BlobObjectId, theirs.BlobObjectId);

        await Indexer().IndexOwnerAsync(_owner);
        await Indexer().IndexOwnerAsync(other);

        var current = await _db.DocumentTexts.Where(d => d.IsCurrent).ToListAsync();
        Assert.Equal(2, current.Count);
        Assert.Equal(
            new[] { _owner, other }.OrderBy(g => g),
            current.Select(d => d.OwnerUserId).OrderBy(g => g));
    }

    // ---- same bytes vs changed bytes ----------------------------------------
    //
    // The two halves of the swap rule are opposites and the code has to tell
    // them apart, because "the extraction that is current failed to be replaced"
    // means something completely different depending on whose bytes failed.
    //
    // Same blob: a second extractor is having a go at a document that is
    // already being read perfectly well. It may promote itself on success and
    // must change nothing otherwise — an upgrade that withdraws a working
    // document from its owner's corpus is data loss wearing a version number.
    //
    // Changed blob: the file is no longer the thing the current row describes.
    // The old reading must stop being authority whatever happens next, because
    // the alternative is answering a question about a replaced document with
    // full confidence and no symptom.

    [Fact]
    public async Task Same_Bytes_A_Failing_New_Profile_Does_Not_Withdraw_A_Working_Reading()
    {
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        var working = await _db.DocumentTexts.SingleAsync();
        Assert.True(working.IsCurrent);
        Assert.Equal(AiArtifactStatuses.Completed, working.Status);

        // A second extraction profile refuses the SAME blob — a parser added
        // later that cannot handle this format.
        await RecordSkipForSecondProfileAsync(file, working.SourceBlobObjectId);

        var rows = await _db.DocumentTexts.OrderBy(d => d.Status).ToListAsync();
        Assert.Equal(2, rows.Count);

        // The working reading keeps authority; the refusal is recorded as
        // provenance and is not current.
        var current = await _db.DocumentTexts.Where(d => d.IsCurrent).SingleAsync();
        Assert.Equal(working.Id, current.Id);
        Assert.Equal(AiArtifactStatuses.Completed, current.Status);

        // And it is still answerable, which is the thing that would have been
        // lost.
        var corpus = await new NubArca.Api.Rag.Retrieval.OwnerDocumentCorpusSource(_db)
            .LoadAsync(_owner);
        Assert.NotEmpty(corpus.Chunks);
    }

    [Fact]
    public async Task Same_Bytes_A_Succeeding_New_Profile_Takes_Over_Atomically()
    {
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);
        var first = await _db.DocumentTexts.SingleAsync();

        // The successful upgrade path: same blob, newer reading, promoted only
        // once it is complete.
        var second = await AddCompletedReadingForSecondProfileAsync(
            file, first.SourceBlobObjectId);

        Assert.Equal(2, await _db.DocumentTexts.CountAsync());
        var current = await _db.DocumentTexts.Where(d => d.IsCurrent).SingleAsync();
        Assert.Equal(second.Id, current.Id);
        Assert.False(await _db.DocumentTexts.Where(d => d.Id == first.Id).Select(d => d.IsCurrent).SingleAsync());
    }

    [Fact]
    public async Task Changed_Bytes_A_Retryable_Failure_Leaves_No_Current_Knowledge()
    {
        // THE HOLE THIS TEST EXISTS FOR. The blob changed and storage cannot be
        // read — an environment failure, so no content verdict is recorded and
        // the pass returns having produced nothing. If the previous reading were
        // still current, the corpus would answer questions about the OLD
        // document as though it described the file the person now has.
        var profile = SeedEmbeddingProfile();
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);
        Assert.True(await _db.DocumentTexts.SingleAsync() is { IsCurrent: true });

        var replacement = await WriteBlobAsync(
            "Un contenuto completamente diverso, che non parla di caldaie.");
        file.BlobObjectId = replacement.Id;
        file.SizeBytes = replacement.SizeBytes;
        await _db.SaveChangesAsync();

        // The new bytes are unreadable from storage. Retryable, not a verdict.
        foreach (var path in Directory.EnumerateFiles(
                     _storageRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        var outcome = await Indexer(semantic: profile.Key).IndexOwnerAsync(_owner, embed: true);

        // Nothing completed for the new bytes, and nothing current at all.
        Assert.Contains("unreadable", outcome.SkipReasons);
        Assert.Empty(await _db.DocumentTexts.Where(d => d.IsCurrent).ToListAsync());

        // Not retrievable and not embeddable, through the shared boundary.
        var corpus = await new NubArca.Api.Rag.Retrieval.OwnerDocumentCorpusSource(_db)
            .LoadAsync(_owner);
        Assert.Empty(corpus.Chunks);
        Assert.Equal(0, outcome.EmbeddingsCreated);
    }

    [Fact]
    public async Task Changed_Bytes_Recover_When_The_New_Bytes_Become_Readable()
    {
        // The control for the test above: the refusal is a pause, not a
        // tombstone. Once storage answers again the new document is extracted
        // and becomes current on its own.
        var file = await AddFileAsync("boiler-manual.md", Manual);
        await Indexer().IndexOwnerAsync(_owner);

        const string replacementText = """
            # Contratto di manutenzione

            Il contratto prevede una verifica annuale programmata e un intervento
            di emergenza entro quarantotto ore dalla segnalazione del guasto.
            """;
        var replacement = await WriteBlobAsync(replacementText);
        var storedPath = Directory.EnumerateFiles(
            _storageRoot, "*", SearchOption.AllDirectories).ToList();
        file.BlobObjectId = replacement.Id;
        file.SizeBytes = replacement.SizeBytes;
        await _db.SaveChangesAsync();

        var moved = storedPath.ToDictionary(p => p, p => p + ".hidden");
        foreach (var (from, to) in moved) File.Move(from, to);
        await Indexer().IndexOwnerAsync(_owner);
        Assert.Empty(await _db.DocumentTexts.Where(d => d.IsCurrent).ToListAsync());

        foreach (var (from, to) in moved) File.Move(to, from);
        await Indexer().IndexOwnerAsync(_owner);

        var current = await _db.DocumentTexts.Where(d => d.IsCurrent).SingleAsync();
        Assert.Equal(AiArtifactStatuses.Completed, current.Status);
        Assert.Equal(replacement.Id, current.SourceBlobObjectId);
        Assert.Contains("manutenzione", current.Text!, StringComparison.OrdinalIgnoreCase);
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

    /// A second profile REFUSING the same blob, written the way the indexer
    /// writes a content verdict.
    private async Task RecordSkipForSecondProfileAsync(FileItem file, Guid blobObjectId)
    {
        var profile = SeedSecondExtractionProfile();
        var readableElsewhere = await _db.DocumentTexts.AnyAsync(
            d => d.FileItemId == file.Id
                 && d.IsCurrent
                 && d.Status == AiArtifactStatuses.Completed
                 && d.SourceBlobObjectId == blobObjectId);

        _db.DocumentTexts.Add(new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _owner,
            ProfileId = profile.Id,
            SourceBlobObjectId = blobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Skipped,
            ErrorCode = "unsupported-document-format",
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            IsCurrent = !readableElsewhere,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    /// A second profile SUCCEEDING on the same blob, promoted atomically.
    private async Task<DocumentText> AddCompletedReadingForSecondProfileAsync(
        FileItem file, Guid blobObjectId)
    {
        var profile = SeedSecondExtractionProfile();

        foreach (var superseded in await _db.DocumentTexts
                     .Where(d => d.FileItemId == file.Id && d.IsCurrent).ToListAsync())
        {
            superseded.IsCurrent = false;
        }
        await _db.SaveChangesAsync();

        var row = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _owner,
            ProfileId = profile.Id,
            SourceBlobObjectId = blobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            TextHash = RagHash.Sha256Hex(Manual),
            Text = Manual,
            CharCount = Manual.Length,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            IsCurrent = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DocumentTexts.Add(row);
        await _db.SaveChangesAsync();
        return row;
    }

    /// A second extraction profile, which is what rich ingestion adds: another
    /// way of reading the same file.
    private AiProfile SeedSecondExtractionProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "doc-second-extractor-" + Guid.NewGuid().ToString("N")[..8],
            Provider = AiProviders.None,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Text,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "doc-second-extractor-profile-" + Guid.NewGuid().ToString("N")[..8],
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Text,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Retrieval;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// THE ISOLATION PROOF for the owner-private corpus.
//
// Every test here is the same shape: seed two people with unique sentinels,
// ask as one of them, and assert the other's sentinel is nowhere in the answer.
// Sentinels rather than counts, because a test that only checks "returned 3
// rows" passes on the day it returns the wrong three.
//
// The Vault, deletion and shared-blob cases are the ones that fail LATER rather
// than immediately: the derived rows for a vaulted or deleted document still
// exist, and every one of these tests deliberately leaves them there. If
// retrieval depended on cleanup having run, all of them would pass with the
// rows deleted and fail in production the moment a sweeper fell behind.
public sealed class OwnerDocumentIsolationTests : IDisposable
{
    private const string OwnerASentinel = "OWNER_A_PRIVATE_SENTINEL";
    private const string OwnerBSentinel = "OWNER_B_PRIVATE_SENTINEL";
    private const string VaultSentinel = "VAULT_PRIVATE_SENTINEL";
    private const string DeletedSentinel = "DELETED_PRIVATE_SENTINEL";
    private const string ExcludedSentinel = "EXCLUDED_PRIVATE_SENTINEL";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly OwnerDocumentCorpusSource _corpus;

    private readonly Guid _ownerA = Guid.NewGuid();
    private readonly Guid _ownerB = Guid.NewGuid();
    private Guid _profileId;

    public OwnerDocumentIsolationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        // `users.RoleKey` is a foreign key into `access_roles`, so no account can
        // exist before the roles do. They are part of an empty NubArca schema
        // rather than test data.
        _db.SeedBuiltInRoles();
        _corpus = new OwnerDocumentCorpusSource(_db);
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- owner isolation ----------------------------------------------------

    [Fact]
    public async Task OwnerA_Corpus_NeverContainsOwnerB()
    {
        var corpus = await _corpus.LoadAsync(_ownerA);

        Assert.Contains(corpus.Chunks, c => c.Text.Contains(OwnerASentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(OwnerBSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerB_Corpus_NeverContainsOwnerA()
    {
        // Both directions. An asymmetric bug — say, a predicate that compares
        // against the first user in the table — passes one of these.
        var corpus = await _corpus.LoadAsync(_ownerB);

        Assert.Contains(corpus.Chunks, c => c.Text.Contains(OwnerBSentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(OwnerASentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LexicalOwnerFilter_IsAppliedBeforeRanking()
    {
        // ADVERSARIAL: owner B's document is a far better lexical match for the
        // question than anything owner A has. If the filter ran after ranking —
        // or if both owners' chunks shared one index — B's document would win
        // and then be dropped, or worse, not be dropped.
        var index = new RagLexicalIndex(
            await _corpus.LoadAsync(_ownerA),
            RagRankingProfiles.For(RagDomainKey.UserDocuments));

        var shape = RagQueryShape.For("pulizia del filtro della caldaia ogni sei mesi", false);
        var hits = index.Search(shape, 20);

        Assert.NotEmpty(hits);
        // The whole index contains only owner A. There is nothing of B's to
        // rank, which is a stronger statement than "B did not rank first".
        Assert.All(index.Corpus.Chunks, c =>
            Assert.DoesNotContain(OwnerBSentinel, c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Empty_Owner_Retrieves_Nothing()
    {
        // Not "everything" and not "the first owner's". A missing owner is a
        // refusal, and the corpus for one is empty by construction.
        var corpus = await _corpus.LoadAsync(Guid.Empty);
        Assert.True(corpus.IsEmpty);
    }

    [Fact]
    public async Task An_Unknown_Owner_Retrieves_Nothing()
    {
        var corpus = await _corpus.LoadAsync(Guid.NewGuid());
        Assert.True(corpus.IsEmpty);
    }

    // ---- Private Vault ------------------------------------------------------

    [Fact]
    public async Task VaultFile_IsNotRetrieved_EvenWithDerivedRowsPresent()
    {
        // The vaulted document has a DocumentText and chunks, deliberately left
        // in place. Retrieval must not find it anyway — the exclusion is the
        // live join, not a cleanup that may or may not have happened.
        Assert.True(await _db.DocumentChunks
            .AnyAsync(c => c.Text!.Contains(VaultSentinel)));

        var corpus = await _corpus.LoadAsync(_ownerA);

        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(VaultSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task VaultFile_IsNotEvenExtractable()
    {
        // One step earlier: the ingestion candidate query does not offer it, so
        // the bytes of a vaulted document are never read at all.
        var candidates = await OwnerDocumentEligibility
            .Extractable(_db.FileItems, _ownerA)
            .Select(f => f.Name)
            .ToListAsync();

        Assert.DoesNotContain("vault-secret.md", candidates);
        Assert.Contains("boiler-manual.md", candidates);
    }

    [Fact]
    public async Task MoveIntoVault_ImmediatelyRemovesRetrievability()
    {
        var before = await _corpus.LoadAsync(_ownerA);
        Assert.Contains(before.Chunks, c => c.Text.Contains(OwnerASentinel, StringComparison.Ordinal));

        // A DB-only move, exactly as the vault performs it. No derived row is
        // touched, and nothing is given a chance to clean up.
        var file = await _db.FileItems.SingleAsync(f => f.Name == "boiler-manual.md");
        file.PrivateVaultId = await _db.PrivateVaults
            .Where(v => v.OwnerUserId == _ownerA).Select(v => v.Id).FirstAsync();
        await _db.SaveChangesAsync();

        var after = await _corpus.LoadAsync(_ownerA);
        Assert.DoesNotContain(
            after.Chunks, c => c.Text.Contains(OwnerASentinel, StringComparison.Ordinal));
    }

    // ---- deletion -----------------------------------------------------------

    [Fact]
    public async Task DeletedFile_IsNotRetrieved_EvenIfDerivedRowsRemain()
    {
        Assert.True(await _db.DocumentChunks
            .AnyAsync(c => c.Text!.Contains(DeletedSentinel)));

        var corpus = await _corpus.LoadAsync(_ownerA);

        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(DeletedSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcludedFromLibrary_IsNotRetrieved()
    {
        // The owner moved it out of their media library, which means "do not
        // process this for AI". Answering questions from it is processing.
        var corpus = await _corpus.LoadAsync(_ownerA);

        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(ExcludedSentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleChunkRow_WithNoLiveFile_CannotResurrectContent()
    {
        // The nastiest shape: derived rows whose FileItem is gone entirely.
        // Nothing joins, so nothing is retrievable — and the corpus does not
        // error, because an orphan is a housekeeping matter rather than a
        // failure to answer.
        var deleted = await _db.FileItems.IgnoreQueryFilters()
            .SingleAsync(f => f.Name == "deleted-notes.md");
        _db.FileItems.Remove(deleted);
        await _db.SaveChangesAsync();

        var corpus = await _corpus.LoadAsync(_ownerA);

        Assert.DoesNotContain(
            corpus.Chunks, c => c.Text.Contains(DeletedSentinel, StringComparison.Ordinal));
        Assert.Contains(corpus.Chunks, c => c.Text.Contains(OwnerASentinel, StringComparison.Ordinal));
    }

    // ---- shared blob --------------------------------------------------------

    [Fact]
    public async Task SharedBlob_DoesNotShareDocumentAuthority()
    {
        // PERMANENT INVARIANT: blob identity is not knowledge authority.
        // Deduplication is a storage fact — two people who upload the same file
        // share one BlobObject — and it must not follow that either of them can
        // read the other's extraction of it.
        var shared = await _db.BlobObjects.SingleAsync(b => b.Sha256.StartsWith("5ha5ed"));
        var files = await _db.FileItems.Where(f => f.BlobObjectId == shared.Id).ToListAsync();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.OwnerUserId == _ownerA);
        Assert.Contains(files, f => f.OwnerUserId == _ownerB);

        var a = await _corpus.LoadAsync(_ownerA);
        var b = await _corpus.LoadAsync(_ownerB);

        // Each owner sees THEIR OWN document row for the shared bytes, and
        // neither sees the other's — same text, two separate authorities.
        Assert.Contains(a.Chunks, c => c.Text.Contains("SHARED_BLOB_BODY", StringComparison.Ordinal));
        Assert.Contains(b.Chunks, c => c.Text.Contains("SHARED_BLOB_BODY", StringComparison.Ordinal));

        var aIds = a.Chunks.Select(c => c.ChunkId).ToHashSet();
        var bIds = b.Chunks.Select(c => c.ChunkId).ToHashSet();
        // No derived id is shared across owners. If they were the same rows, a
        // cleanup for one owner would delete the other's.
        Assert.Empty(aIds.Intersect(bIds));
    }

    [Fact]
    public async Task OwnerA_Delete_DoesNotAffectOwnerB_Authority()
    {
        var sharedBlobId = (await _db.BlobObjects.SingleAsync(b => b.Sha256.StartsWith("5ha5ed"))).Id;
        var aFile = await _db.FileItems
            .SingleAsync(f => f.BlobObjectId == sharedBlobId && f.OwnerUserId == _ownerA);

        aFile.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var a = await _corpus.LoadAsync(_ownerA);
        var b = await _corpus.LoadAsync(_ownerB);

        Assert.DoesNotContain(a.Chunks, c => c.Text.Contains("SHARED_BLOB_BODY", StringComparison.Ordinal));
        // B never asked for anything to change, and nothing did.
        Assert.Contains(b.Chunks, c => c.Text.Contains("SHARED_BLOB_BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Citations_Carry_No_Internal_Identifier()
    {
        // What a corpus row exposes as Path/Title/SourceKey is what a citation
        // is built from. It must be the document's NAME and nothing that
        // addresses storage.
        var corpus = await _corpus.LoadAsync(_ownerA);
        var storageKeys = await _db.BlobObjects.Select(b => b.StorageKey).ToListAsync();

        Assert.All(corpus.Chunks, c =>
        {
            Assert.Equal(c.Title, c.Path);
            Assert.Equal(c.Title, c.SourceKey);
            Assert.DoesNotContain("/", c.Path, StringComparison.Ordinal);
            Assert.All(storageKeys, key =>
                Assert.DoesNotContain(key, c.Path, StringComparison.Ordinal));
        });
    }

    // ---- fixture ------------------------------------------------------------

    private void Seed()
    {
        AddUser(_ownerA, "a");
        AddUser(_ownerB, "b");

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
        _profileId = profile.Id;

        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _ownerA,
            CreatedAt = DateTime.UtcNow,
        };
        _db.PrivateVaults.Add(vault);
        _db.SaveChanges();

        // Owner A: one ordinary document, one vaulted, one deleted, one excluded.
        Indexed(_ownerA, "boiler-manual.md",
            $"Manutenzione › Pulizia filtro. {OwnerASentinel}. Il filtro della caldaia va pulito "
            + "ogni sei mesi chiudendo il rubinetto di ingresso.");
        Indexed(_ownerA, "vault-secret.md",
            $"{VaultSentinel}. Documento riservato conservato nella cassaforte privata.",
            vaultId: vault.Id);
        Indexed(_ownerA, "deleted-notes.md",
            $"{DeletedSentinel}. Appunti cancellati che non devono più essere recuperabili.",
            deleted: true);
        Indexed(_ownerA, "excluded-notes.md",
            $"{ExcludedSentinel}. Appunti esclusi dalla libreria multimediale dal proprietario.",
            excluded: true);

        // Owner B: a document that is a DELIBERATELY better lexical match for
        // the question owner A will ask.
        Indexed(_ownerB, "private-notes.md",
            $"{OwnerBSentinel}. Pulizia del filtro della caldaia: il filtro va pulito ogni sei "
            + "mesi, la caldaia va controllata, il filtro va sciacquato, filtro filtro caldaia.");

        // The same BYTES held by both people.
        // A 64-character hex sha whose prefix makes it findable in the tests.
        var sharedBlob = AddBlob(
            "5ha5ed" + (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..58]);
        Indexed(_ownerA, "shared-recipe.md", "SHARED_BLOB_BODY. Una ricetta condivisa fra due utenti.",
            blob: sharedBlob);
        Indexed(_ownerB, "shared-recipe.md", "SHARED_BLOB_BODY. Una ricetta condivisa fra due utenti.",
            blob: sharedBlob);

        _db.SaveChanges();
    }

    private void AddUser(Guid id, string tag) => _db.Users.Add(new User
    {
        Id = id,
        Email = $"owner-{tag}-{Guid.NewGuid():N}@example.invalid",
        DisplayName = $"Owner {tag.ToUpperInvariant()}",
        CreatedAt = DateTime.UtcNow,
    });

    private BlobObject AddBlob(string sha)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = 512,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.BlobObjects.Add(blob);
        return blob;
    }

    /// One document, all the way to chunks — the state a completed indexing run
    /// leaves behind.
    private void Indexed(
        Guid owner, string name, string body,
        Guid? vaultId = null, bool deleted = false, bool excluded = false, BlobObject? blob = null)
    {
        blob ??= AddBlob(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

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
            MediaLibraryState = excluded ? MediaLibraryState.Excluded : MediaLibraryState.Active,
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
            ProfileId = _profileId,
            SourceBlobObjectId = blob.Id,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            // The fixture seeds a document as the CURRENT reading of its
            // file, which is what every one of these tests means by
            // "this person has this document indexed".
            IsCurrent = true,
            TextHash = new string('a', 64),
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
            ProfileId = _profileId,
            Ordinal = 1,
            Heading = "Sezione",
            Text = body,
            TextHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
        });
    }
}

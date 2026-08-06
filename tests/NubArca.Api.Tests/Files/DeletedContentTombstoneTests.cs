using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Service-level tests for the deleted-content tombstone ledger + import skip
// evaluator: final-occurrence deletion semantics, delete-intent gating, Private
// Vault interaction, owner scoping, fingerprint privacy, and the two import
// skip options. SQLite in-memory + a real blob store.
public sealed class DeletedContentTombstoneTests : IDisposable
{
    private const string Pepper = "unit-test-pepper-v1";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly DeletedContentTombstoneService _tombstones;
    private readonly FileItemService _files;
    private readonly FolderService _folders;
    private readonly ImportSkipEvaluator _evaluator;

    public DeletedContentTombstoneTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nc-tombstone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));

        var options = Options.Create(new DeletedContentOptions { Pepper = Pepper });
        _tombstones = new DeletedContentTombstoneService(_db, TimeProvider.System, options);
        _files = new FileItemService(
            _db, _blobService, _thumbnails, TimeProvider.System,
            mediaLibrary: null, tombstones: _tombstones);
        _folders = new FolderService(_db, TimeProvider.System, _files, mediaLibrary: null);
        _evaluator = new ImportSkipEvaluator(_db, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- helpers -------------------------------------------------------------

    private async Task<Guid> SeedUserAsync(string email)
    {
        var user = new User { Id = Guid.NewGuid(), Email = email, DisplayName = email, CreatedAt = DateTime.UtcNow };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Folder> SeedFolderAsync(Guid ownerId, Guid? parentId, string name)
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            ParentFolderId = parentId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Folders.Add(folder);
        await _db.SaveChangesAsync();
        return folder;
    }

    private Task<FileItem> CreateAsync(Guid owner, Guid? folder, string name, string content)
        => _files.CreateAsync(owner, folder, name, "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private async Task<string> Sha256OfFileAsync(Guid fileId)
    {
        return await _db.FileItems.IgnoreQueryFilters()
            .Where(f => f.Id == fileId)
            .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id, (f, b) => b.Sha256)
            .FirstAsync();
    }

    private Task<int> TombstoneCountAsync(Guid owner)
        => _db.OwnerDeletedContentTombstones.CountAsync(t => t.OwnerUserId == owner);

    // ---- final-occurrence deletion semantics --------------------------------

    [Fact]
    public async Task DeleteOneOfThree_NoTombstone()
    {
        var owner = await SeedUserAsync("o1@x");
        var a = await CreateAsync(owner, null, "a.txt", "same-content");
        await CreateAsync(owner, null, "b.txt", "same-content");
        await CreateAsync(owner, null, "c.txt", "same-content");

        await _files.SoftDeleteAsync(owner, a.Id, default, FileDeleteReason.UserDelete);

        Assert.Equal(0, await TombstoneCountAsync(owner));
    }

    [Fact]
    public async Task DeleteTwoOfThree_NoTombstone()
    {
        var owner = await SeedUserAsync("o2@x");
        var a = await CreateAsync(owner, null, "a.txt", "dup");
        var b = await CreateAsync(owner, null, "b.txt", "dup");
        await CreateAsync(owner, null, "c.txt", "dup");

        await _files.SoftDeleteAsync(owner, a.Id, default, FileDeleteReason.UserDelete);
        await _files.SoftDeleteAsync(owner, b.Id, default, FileDeleteReason.UserDelete);

        Assert.Equal(0, await TombstoneCountAsync(owner));
    }

    [Fact]
    public async Task DeleteFinalOccurrence_CreatesTombstone()
    {
        var owner = await SeedUserAsync("o3@x");
        var a = await CreateAsync(owner, null, "a.txt", "dup");
        var b = await CreateAsync(owner, null, "b.txt", "dup");
        var c = await CreateAsync(owner, null, "c.txt", "dup");

        await _files.SoftDeleteAsync(owner, a.Id, default, FileDeleteReason.UserDelete);
        await _files.SoftDeleteAsync(owner, b.Id, default, FileDeleteReason.UserDelete);
        Assert.Equal(0, await TombstoneCountAsync(owner));

        await _files.SoftDeleteAsync(owner, c.Id, default, FileDeleteReason.UserDelete);
        Assert.Equal(1, await TombstoneCountAsync(owner));

        var t = await _db.OwnerDeletedContentTombstones.SingleAsync(x => x.OwnerUserId == owner);
        Assert.Equal(ContentFingerprint.Scheme, t.FingerprintScheme);
        Assert.Equal(1, t.DeletedCount);
        Assert.Equal("c.txt", t.LastFileNameSnapshot);
    }

    [Fact]
    public async Task FolderDelete_DuplicateOutsideFolder_NoTombstone()
    {
        var owner = await SeedUserAsync("o4@x");
        var folder = await SeedFolderAsync(owner, null, "trip");
        await CreateAsync(owner, folder.Id, "inside.txt", "shared");
        await CreateAsync(owner, null, "outside.txt", "shared"); // survives the folder delete

        var result = await _folders.SoftDeleteRecursiveAsync(owner, folder.Id);

        Assert.NotNull(result);
        Assert.Equal(0, await TombstoneCountAsync(owner));
    }

    [Fact]
    public async Task FolderDelete_AllOccurrencesInsideFolder_CreatesTombstone()
    {
        var owner = await SeedUserAsync("o5@x");
        var folder = await SeedFolderAsync(owner, null, "trip");
        await CreateAsync(owner, folder.Id, "one.txt", "shared");
        await CreateAsync(owner, folder.Id, "two.txt", "shared");

        await _folders.SoftDeleteRecursiveAsync(owner, folder.Id);

        Assert.Equal(1, await TombstoneCountAsync(owner));
    }

    [Fact]
    public async Task OtherOwnerWithSameContent_DoesNotPreventTombstone()
    {
        var alice = await SeedUserAsync("alice@x");
        var bob = await SeedUserAsync("bob@x");
        var aliceFile = await CreateAsync(alice, null, "a.txt", "shared-across-owners");
        await CreateAsync(bob, null, "b.txt", "shared-across-owners"); // bob keeps his copy

        await _files.SoftDeleteAsync(alice, aliceFile.Id, default, FileDeleteReason.UserDelete);

        Assert.Equal(1, await TombstoneCountAsync(alice)); // alice's only copy gone
        Assert.Equal(0, await TombstoneCountAsync(bob));
    }

    [Theory]
    [InlineData(FileDeleteReason.OrganizerExactDedupe)]
    [InlineData(FileDeleteReason.SystemCleanup)]
    [InlineData(FileDeleteReason.MoveToPrivateVault)]
    [InlineData(FileDeleteReason.Restore)]
    [InlineData(FileDeleteReason.Sweeper)]
    [InlineData(FileDeleteReason.RefcountRepair)]
    [InlineData(FileDeleteReason.Unspecified)]
    public async Task NonUserIntentReason_NeverCreatesTombstone(FileDeleteReason reason)
    {
        var owner = await SeedUserAsync($"reason-{reason}@x");
        var only = await CreateAsync(owner, null, "only.txt", "content");

        await _files.SoftDeleteAsync(owner, only.Id, default, reason);

        Assert.Equal(0, await TombstoneCountAsync(owner));
    }

    // ---- Private Vault interaction ------------------------------------------

    [Fact]
    public async Task ContentRemainsInVault_DeletingLastNormalCopy_NoTombstone()
    {
        var owner = await SeedUserAsync("vault@x");
        var normal = await CreateAsync(owner, null, "normal.txt", "vaulted-content");
        var vaultCopy = await CreateAsync(owner, null, "vault.txt", "vaulted-content");

        // Simulate the second copy living in the owner's Private Vault.
        var vaultId = Guid.NewGuid();
        _db.PrivateVaults.Add(new PrivateVault
        {
            Id = vaultId, OwnerUserId = owner, DisplayName = "Private",
            PasswordHash = "x", EncryptionMode = "none",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await _db.FileItems.IgnoreQueryFilters().Where(f => f.Id == vaultCopy.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.PrivateVaultId, vaultId));

        await _files.SoftDeleteAsync(owner, normal.Id, default, FileDeleteReason.UserDelete);

        // Owner still holds the content (in the vault) → no tombstone, and no
        // vault existence is signalled.
        Assert.Equal(0, await TombstoneCountAsync(owner));
    }

    // ---- restore -------------------------------------------------------------

    [Fact]
    public async Task Restore_DoesNotClearTombstone()
    {
        var owner = await SeedUserAsync("restore@x");
        var only = await CreateAsync(owner, null, "only.txt", "content");

        await _files.SoftDeleteAsync(owner, only.Id, default, FileDeleteReason.UserDelete);
        Assert.Equal(1, await TombstoneCountAsync(owner));

        await _files.RestoreAsync(owner, only.Id);

        Assert.Equal(1, await TombstoneCountAsync(owner)); // ledger is historical memory
    }

    [Fact]
    public async Task RepeatedFinalDelete_IncrementsDeletedCount()
    {
        var owner = await SeedUserAsync("recount@x");
        var only = await CreateAsync(owner, null, "only.txt", "content");

        await _files.SoftDeleteAsync(owner, only.Id, default, FileDeleteReason.UserDelete);
        await _files.RestoreAsync(owner, only.Id);
        await _files.SoftDeleteAsync(owner, only.Id, default, FileDeleteReason.UserDelete);

        var t = await _db.OwnerDeletedContentTombstones.SingleAsync(x => x.OwnerUserId == owner);
        Assert.Equal(2, t.DeletedCount);
    }

    // ---- fingerprint privacy -------------------------------------------------

    [Fact]
    public async Task Tombstone_StoresKeyedFingerprint_NotRawSha()
    {
        var owner = await SeedUserAsync("fp@x");
        var only = await CreateAsync(owner, null, "only.txt", "secret-bytes");
        var sha = await Sha256OfFileAsync(only.Id);

        await _files.SoftDeleteAsync(owner, only.Id, default, FileDeleteReason.UserDelete);

        var t = await _db.OwnerDeletedContentTombstones.SingleAsync(x => x.OwnerUserId == owner);
        Assert.NotEqual(sha, t.ContentFingerprint);
        Assert.Equal(ContentFingerprint.Compute(Pepper, sha), t.ContentFingerprint);
    }

    // ---- import skip evaluator ----------------------------------------------

    [Fact]
    public async Task Evaluator_PreviouslyDeleted_MatchesTombstonedContent()
    {
        var owner = await SeedUserAsync("ev1@x");
        var only = await CreateAsync(owner, null, "only.txt", "gone-content");
        var sha = await Sha256OfFileAsync(only.Id);
        await _files.SoftDeleteAsync(owner, only.Id, default, FileDeleteReason.UserDelete);

        var decisions = await _evaluator.EvaluateBatchAsync(
            owner, new[] { sha }, skipPreviouslyDeleted: true, skipExistingContent: false);

        Assert.Equal(ImportSkipReason.PreviouslyDeleted, decisions[sha]);
    }

    [Fact]
    public async Task Evaluator_AlreadyPresent_MatchesActiveLibraryContent()
    {
        var owner = await SeedUserAsync("ev2@x");
        var f = await CreateAsync(owner, null, "have.txt", "present-content");
        var sha = await Sha256OfFileAsync(f.Id);

        var decisions = await _evaluator.EvaluateBatchAsync(
            owner, new[] { sha }, skipPreviouslyDeleted: false, skipExistingContent: true);

        Assert.Equal(ImportSkipReason.AlreadyPresent, decisions[sha]);
    }

    [Fact]
    public async Task Evaluator_VaultOnlyContent_NotReportedAsPresent()
    {
        var owner = await SeedUserAsync("ev3@x");
        var f = await CreateAsync(owner, null, "v.txt", "vault-only");
        var sha = await Sha256OfFileAsync(f.Id);
        var vaultId = Guid.NewGuid();
        _db.PrivateVaults.Add(new PrivateVault
        {
            Id = vaultId, OwnerUserId = owner, DisplayName = "Private",
            PasswordHash = "x", EncryptionMode = "none",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await _db.FileItems.IgnoreQueryFilters().Where(x => x.Id == f.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PrivateVaultId, vaultId));

        var decisions = await _evaluator.EvaluateBatchAsync(
            owner, new[] { sha }, skipPreviouslyDeleted: false, skipExistingContent: true);

        Assert.False(decisions.ContainsKey(sha)); // vault content never reported present
    }

    [Fact]
    public async Task Evaluator_IsOwnerScoped()
    {
        var alice = await SeedUserAsync("evalice@x");
        var bob = await SeedUserAsync("evbob@x");
        var aliceFile = await CreateAsync(alice, null, "a.txt", "alice-only");
        var sha = await Sha256OfFileAsync(aliceFile.Id);
        await _files.SoftDeleteAsync(alice, aliceFile.Id, default, FileDeleteReason.UserDelete);

        // Bob imports the same content: neither option should match for Bob.
        var decisions = await _evaluator.EvaluateBatchAsync(
            bob, new[] { sha }, skipPreviouslyDeleted: true, skipExistingContent: true);

        Assert.False(decisions.ContainsKey(sha));
    }

    [Fact]
    public async Task Evaluator_PreviouslyDeletedWins_OverAlreadyPresent()
    {
        // Content that is BOTH tombstoned and currently active: precedence is
        // "previously deleted". (Owner deleted the last copy → tombstone; then a
        // copy exists again.)
        var owner = await SeedUserAsync("ev4@x");
        var first = await CreateAsync(owner, null, "first.txt", "both-content");
        var sha = await Sha256OfFileAsync(first.Id);
        await _files.SoftDeleteAsync(owner, first.Id, default, FileDeleteReason.UserDelete); // tombstone
        await CreateAsync(owner, null, "again.txt", "both-content"); // now present again

        var decisions = await _evaluator.EvaluateBatchAsync(
            owner, new[] { sha }, skipPreviouslyDeleted: true, skipExistingContent: true);

        Assert.Equal(ImportSkipReason.PreviouslyDeleted, decisions[sha]);
    }

    [Fact]
    public async Task Evaluator_BothOptionsOff_ReturnsEmpty()
    {
        var owner = await SeedUserAsync("ev5@x");
        var f = await CreateAsync(owner, null, "f.txt", "content");
        var sha = await Sha256OfFileAsync(f.Id);

        var decisions = await _evaluator.EvaluateBatchAsync(
            owner, new[] { sha }, skipPreviouslyDeleted: false, skipExistingContent: false);

        Assert.Empty(decisions);
    }
}

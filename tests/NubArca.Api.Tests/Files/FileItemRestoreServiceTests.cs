using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Storage;

namespace NubArca.Api.Tests.Files;

// Service-level unit tests for IFileItemService.RestoreAsync. Mirrors the
// SQLite fixture shape used by FileItemServiceTests.
public sealed class FileItemRestoreServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly FileItemService _service;
    private readonly FolderService _folderService;

    public FileItemRestoreServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-file-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _service = new FileItemService(_db, _blobService, _thumbnails, TimeProvider.System);
        _folderService = new FolderService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<User> SeedUserAsync(string email = "owner@example.com")
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u;
    }

    private static MemoryStream BytesOf(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task RestoreAsync_SoftDeleted_File_Clears_DeletedAt_And_Sets_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v1"));
        await _service.SoftDeleteAsync(owner.Id, file.Id);

        var restored = await _service.RestoreAsync(owner.Id, file.Id);

        Assert.NotNull(restored);
        Assert.Null(restored!.DeletedAt);
        Assert.NotNull(restored.UpdatedAt);
    }

    [Fact]
    public async Task RestoreAsync_Reappears_In_ListChildren_GetById_OpenContent_Search()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("payload"));
        await _service.SoftDeleteAsync(owner.Id, file.Id);

        // Before restore: hidden.
        Assert.Empty(await _service.ListChildrenAsync(owner.Id, null));
        Assert.Null(await _service.GetByIdAsync(file.Id, owner.Id));
        Assert.Null(await _service.OpenContentAsync(file.Id, owner.Id));
        Assert.Empty(await _service.SearchAsync(owner.Id, "doc"));

        await _service.RestoreAsync(owner.Id, file.Id);

        Assert.Single(await _service.ListChildrenAsync(owner.Id, null));
        var fetched = await _service.GetByIdAsync(file.Id, owner.Id);
        Assert.NotNull(fetched);

        await using var content = await _service.OpenContentAsync(file.Id, owner.Id);
        Assert.NotNull(content);
        using var ms = new MemoryStream();
        await content!.Content.CopyToAsync(ms);
        Assert.Equal("payload", Encoding.UTF8.GetString(ms.ToArray()));

        Assert.Single(await _service.SearchAsync(owner.Id, "doc"));
    }

    [Fact]
    public async Task RestoreAsync_Increments_BlobObject_ReferenceCount_Exactly_Once()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v1"));
        Assert.Equal(1, (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount);

        await _service.SoftDeleteAsync(owner.Id, file.Id);
        Assert.Equal(0, (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount);

        var restored = await _service.RestoreAsync(owner.Id, file.Id);
        Assert.NotNull(restored);
        Assert.Equal(1, (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount);
    }

    [Fact]
    public async Task RestoreAsync_Called_Twice_Does_Not_Double_Increment()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v1"));
        await _service.SoftDeleteAsync(owner.Id, file.Id);

        await _service.RestoreAsync(owner.Id, file.Id);
        await _service.RestoreAsync(owner.Id, file.Id); // idempotent no-op

        Assert.Equal(1, (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount);
    }

    [Fact]
    public async Task RestoreAsync_Already_Active_File_Is_Idempotent_NoOp()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v1"));
        var initial = (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount;

        var restored = await _service.RestoreAsync(owner.Id, file.Id);

        Assert.NotNull(restored);
        Assert.Equal(file.Id, restored!.Id);
        Assert.Null(restored.DeletedAt);
        Assert.Equal(initial, (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount);
    }

    [Fact]
    public async Task RestoreAsync_Foreign_File_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "alice.txt", "text/plain", BytesOf("a"));
        await _service.SoftDeleteAsync(alice.Id, aliceFile.Id);

        var restored = await _service.RestoreAsync(bob.Id, aliceFile.Id);

        Assert.Null(restored);
        // Alice's file remains soft-deleted; Bob's foreign attempt did nothing.
        var row = await _db.FileItems.AsNoTracking().FirstAsync(f => f.Id == aliceFile.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_Missing_File_Returns_Null()
    {
        var owner = await SeedUserAsync();

        var restored = await _service.RestoreAsync(owner.Id, Guid.NewGuid());

        Assert.Null(restored);
    }

    [Fact]
    public async Task RestoreAsync_With_Active_Sibling_Same_Name_Throws_Duplicate()
    {
        var owner = await SeedUserAsync();
        var first = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v1"));
        await _service.SoftDeleteAsync(owner.Id, first.Id);

        // Create a new active file with the same name, occupying the slot.
        var occupant = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("v2"));

        await Assert.ThrowsAsync<DuplicateFileNameException>(
            () => _service.RestoreAsync(owner.Id, first.Id));

        // First file stays soft-deleted; occupant is untouched.
        var firstRow = await _db.FileItems.AsNoTracking().FirstAsync(f => f.Id == first.Id);
        Assert.NotNull(firstRow.DeletedAt);
        var occupantRow = await _db.FileItems.AsNoTracking().FirstAsync(f => f.Id == occupant.Id);
        Assert.Null(occupantRow.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_With_SoftDeleted_Parent_Throws_RestoreParentDeleted()
    {
        var owner = await SeedUserAsync();
        var folder = await _folderService.CreateAsync(owner.Id, null, "Photos");
        var file = await _service.CreateAsync(owner.Id, folder.Id, "snap.jpg", "image/jpeg", BytesOf("img"));

        await _service.SoftDeleteAsync(owner.Id, file.Id);
        await _folderService.SoftDeleteAsync(owner.Id, folder.Id);

        var ex = await Assert.ThrowsAsync<RestoreParentDeletedException>(
            () => _service.RestoreAsync(owner.Id, file.Id));
        Assert.Equal(folder.Id, ex.ParentFolderId);

        var row = await _db.FileItems.AsNoTracking().FirstAsync(f => f.Id == file.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_Then_Restore_Parent_Folder_And_Restore_File_Works()
    {
        var owner = await SeedUserAsync();
        var folder = await _folderService.CreateAsync(owner.Id, null, "Photos");
        var file = await _service.CreateAsync(owner.Id, folder.Id, "snap.jpg", "image/jpeg", BytesOf("img"));

        await _service.SoftDeleteAsync(owner.Id, file.Id);
        await _folderService.SoftDeleteAsync(owner.Id, folder.Id);

        // Restoring file before parent: rejected.
        await Assert.ThrowsAsync<RestoreParentDeletedException>(
            () => _service.RestoreAsync(owner.Id, file.Id));

        // Restore the parent.
        var restoredFolder = await _folderService.RestoreAsync(owner.Id, folder.Id);
        Assert.NotNull(restoredFolder);

        // Now file restore succeeds.
        var restoredFile = await _service.RestoreAsync(owner.Id, file.Id);
        Assert.NotNull(restoredFile);
        Assert.Null(restoredFile!.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_Root_Level_File_With_Null_Parent_Works()
    {
        // Null parent means root; no parent-deleted check applies.
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("x"));
        await _service.SoftDeleteAsync(owner.Id, file.Id);

        var restored = await _service.RestoreAsync(owner.Id, file.Id);

        Assert.NotNull(restored);
        Assert.Null(restored!.ParentFolderId);
        Assert.Null(restored.DeletedAt);
    }
}

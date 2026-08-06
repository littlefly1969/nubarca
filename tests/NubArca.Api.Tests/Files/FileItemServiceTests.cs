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

// SQLite in-memory unit tests, plus a real LocalFileSystemBlobStorage backed
// by a per-test temp directory.
//
// SQLite enforces the new ux_file_items_active_sibling_name unique constraint
// for non-null parents (sequential pre-check covers all cases here anyway).
// For null parents, SQLite treats multiple NULLs as distinct — the pre-check
// is still authoritative in that case. The PostgreSQL integration tests cover
// the constraint-level race recovery for both shapes.
public sealed class FileItemServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly FileItemService _service;

    public FileItemServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-file-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));

        _service = new FileItemService(_db, _blobService, _thumbnails, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private async Task<User> SeedUserAsync(string email = "owner@example.com")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<Folder> SeedFolderAsync(Guid ownerId, Guid? parentId, string name, bool deleted = false)
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            ParentFolderId = parentId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        _db.Folders.Add(folder);
        await _db.SaveChangesAsync();
        return folder;
    }

    private static MemoryStream BytesOf(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task CreateAsync_Creates_Root_File()
    {
        var owner = await SeedUserAsync();

        var before = DateTime.UtcNow;
        var file = await _service.CreateAsync(
            owner.Id, parentFolderId: null, "report.txt", "text/plain", BytesOf("hello"));
        var after = DateTime.UtcNow;

        Assert.NotEqual(Guid.Empty, file.Id);
        Assert.Equal(owner.Id, file.OwnerUserId);
        Assert.Null(file.ParentFolderId);
        Assert.Equal("report.txt", file.Name);
        Assert.Equal("text/plain", file.MimeType);
        Assert.Equal(5, file.SizeBytes);
        Assert.NotEqual(Guid.Empty, file.BlobObjectId);
        Assert.Null(file.UpdatedAt);
        Assert.Null(file.DeletedAt);
        Assert.InRange(file.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));

        Assert.Equal(1, await _db.FileItems.CountAsync());
        Assert.Equal(1, await _db.BlobObjects.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Creates_File_Inside_Valid_Folder()
    {
        var owner = await SeedUserAsync();
        var folder = await SeedFolderAsync(owner.Id, null, "Photos");

        var file = await _service.CreateAsync(
            owner.Id, folder.Id, "snap.jpg", "image/jpeg", BytesOf("img"));

        Assert.Equal(folder.Id, file.ParentFolderId);
    }

    [Fact]
    public async Task CreateAsync_Trims_Name_And_MimeType()
    {
        var owner = await SeedUserAsync();

        var file = await _service.CreateAsync(
            owner.Id, null, "   spaced.txt   ", "  text/plain  ", BytesOf("x"));

        Assert.Equal("spaced.txt", file.Name);
        Assert.Equal("text/plain", file.MimeType);
    }

    [Fact]
    public async Task CreateAsync_Uses_Default_MimeType_When_Null_Or_Whitespace()
    {
        var owner = await SeedUserAsync();

        var fromNull = await _service.CreateAsync(owner.Id, null, "a.bin", null, BytesOf("a"));
        var fromBlank = await _service.CreateAsync(owner.Id, null, "b.bin", "   ", BytesOf("b"));

        Assert.Equal("application/octet-stream", fromNull.MimeType);
        Assert.Equal("application/octet-stream", fromBlank.MimeType);
    }

    [Fact]
    public async Task CreateAsync_Persists_SizeBytes_From_BlobWriteResult_Not_User_Input()
    {
        var owner = await SeedUserAsync();
        var content = Encoding.UTF8.GetBytes("0123456789");

        var file = await _service.CreateAsync(
            owner.Id, null, "ten.bin", "application/octet-stream", new MemoryStream(content));

        Assert.Equal(10, file.SizeBytes);
        var blob = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(10, blob.SizeBytes);
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Missing_Parent()
    {
        var owner = await SeedUserAsync();
        var bogus = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(owner.Id, bogus, "x.txt", "text/plain", BytesOf("x")));

        Assert.Equal(bogus, ex.FolderId);
        Assert.Equal(0, await _db.FileItems.CountAsync());
        Assert.Equal(0, await _db.BlobObjects.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Foreign_Owner_Parent()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await SeedFolderAsync(alice.Id, null, "Photos");

        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(bob.Id, aliceFolder.Id, "stolen.txt", "text/plain", BytesOf("x")));

        Assert.Equal(0, await _db.FileItems.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Soft_Deleted_Parent()
    {
        var owner = await SeedUserAsync();
        var deleted = await SeedFolderAsync(owner.Id, null, "Trash", deleted: true);

        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(owner.Id, deleted.Id, "x.txt", "text/plain", BytesOf("x")));

        Assert.Equal(0, await _db.FileItems.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_DuplicateFileName_For_Active_Sibling_Name()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("first"));

        var ex = await Assert.ThrowsAsync<DuplicateFileNameException>(
            () => _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("second")));

        Assert.Equal(owner.Id, ex.OwnerUserId);
        Assert.Null(ex.ParentFolderId);
        Assert.Equal("report.txt", ex.Name);
        Assert.Equal(1, await _db.FileItems.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Allows_Same_Name_Under_Different_Folders()
    {
        var owner = await SeedUserAsync();
        var a = await SeedFolderAsync(owner.Id, null, "A");
        var b = await SeedFolderAsync(owner.Id, null, "B");

        await _service.CreateAsync(owner.Id, a.Id, "shared.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(owner.Id, b.Id, "shared.txt", "text/plain", BytesOf("b"));

        Assert.Equal(2, await _db.FileItems.CountAsync(f => f.Name == "shared.txt"));
    }

    [Fact]
    public async Task CreateAsync_Allows_Same_Name_For_Different_Owners()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");

        await _service.CreateAsync(alice.Id, null, "shared.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(bob.Id, null, "shared.txt", "text/plain", BytesOf("b"));

        Assert.Equal(2, await _db.FileItems.CountAsync(f => f.Name == "shared.txt"));
    }

    [Fact]
    public async Task CreateAsync_Allows_Reuse_Of_Name_After_Sibling_Is_Soft_Deleted()
    {
        var owner = await SeedUserAsync();
        var first = await _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("v1"));

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == first.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var fresh = await _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("v2"));
        Assert.Equal("report.txt", fresh.Name);
    }

    [Fact]
    public async Task CreateAsync_Same_Content_Different_Names_Reuses_Blob_With_RefCount_2()
    {
        var owner = await SeedUserAsync();
        var content = Encoding.UTF8.GetBytes("identical-content");

        var f1 = await _service.CreateAsync(owner.Id, null, "first.txt", "text/plain", new MemoryStream(content));
        var f2 = await _service.CreateAsync(owner.Id, null, "second.txt", "text/plain", new MemoryStream(content));

        Assert.Equal(f1.BlobObjectId, f2.BlobObjectId);
        Assert.Equal(2, await _db.FileItems.CountAsync());
        Assert.Equal(1, await _db.BlobObjects.CountAsync());

        var blob = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(2, blob.ReferenceCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("/")]
    [InlineData("a\\b")]
    [InlineData("\\")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task CreateAsync_Rejects_Invalid_Name(string name)
    {
        var owner = await SeedUserAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, name, "text/plain", BytesOf("x")));
    }

    [Fact]
    public async Task CreateAsync_Rejects_Null_Name()
    {
        var owner = await SeedUserAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, null!, "text/plain", BytesOf("x")));
    }

    [Fact]
    public async Task CreateAsync_Rejects_Name_Longer_Than_255_Chars()
    {
        var owner = await SeedUserAsync();
        var tooLong = new string('a', 256);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, tooLong, "text/plain", BytesOf("x")));
    }

    [Fact]
    public async Task CreateAsync_Accepts_Name_Of_255_Chars()
    {
        var owner = await SeedUserAsync();
        var atLimit = new string('a', 255);

        var file = await _service.CreateAsync(owner.Id, null, atLimit, "text/plain", BytesOf("x"));

        Assert.Equal(255, file.Name.Length);
    }

    [Fact]
    public async Task CreateAsync_Rejects_MimeType_Longer_Than_255_Chars()
    {
        var owner = await SeedUserAsync();
        var tooLong = "text/" + new string('a', 251);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, "x.txt", tooLong, BytesOf("x")));
    }

    [Fact]
    public async Task CreateAsync_Rejects_Null_Content_Stream()
    {
        var owner = await SeedUserAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", null!));
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Created_File_For_Owner()
    {
        var owner = await SeedUserAsync();
        var created = await _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var fetched = await _service.GetByIdAsync(created.Id, owner.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Foreign_Owner()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var fetched = await _service.GetByIdAsync(aliceFile.Id, bob.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Soft_Deleted_File()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == file.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var fetched = await _service.GetByIdAsync(file.Id, owner.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Missing_Id()
    {
        var owner = await SeedUserAsync();

        var fetched = await _service.GetByIdAsync(Guid.NewGuid(), owner.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task OpenContentAsync_Returns_Original_Bytes_With_Expected_Fields()
    {
        var owner = await SeedUserAsync();
        var payload = Encoding.UTF8.GetBytes("readback-content");
        var created = await _service.CreateAsync(
            owner.Id, null, "report.txt", "text/plain", new MemoryStream(payload));

        await using var content = await _service.OpenContentAsync(created.Id, owner.Id);

        Assert.NotNull(content);
        Assert.Equal("text/plain", content!.MimeType);
        Assert.Equal(payload.LongLength, content.SizeBytes);
        Assert.Equal("report.txt", content.FileName);

        using var ms = new MemoryStream();
        await content.Content.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task OpenContentAsync_Returns_Null_For_Foreign_Owner()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(
            alice.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var content = await _service.OpenContentAsync(aliceFile.Id, bob.Id);

        Assert.Null(content);
    }

    [Fact]
    public async Task OpenContentAsync_Returns_Null_For_Soft_Deleted_File()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == file.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var content = await _service.OpenContentAsync(file.Id, owner.Id);

        Assert.Null(content);
    }

    [Fact]
    public async Task OpenContentAsync_Returns_Null_For_Missing_Id()
    {
        var owner = await SeedUserAsync();

        var content = await _service.OpenContentAsync(Guid.NewGuid(), owner.Id);

        Assert.Null(content);
    }

    [Fact]
    public async Task OpenContentAsync_Stream_Can_Be_Disposed_Twice_Safely()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var content = await _service.OpenContentAsync(file.Id, owner.Id);
        Assert.NotNull(content);

        await content!.DisposeAsync();
        await content.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task ListChildrenAsync_Lists_Root_Files_For_Owner()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "b.txt", "text/plain", BytesOf("b"));
        await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("a"));

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Equal(new[] { "a.txt", "b.txt" }, children.Select(c => c.Name).ToArray());
        Assert.All(children, c => Assert.Equal("text/plain", c.MimeType));
        Assert.Equal(new[] { 1L, 1L }, children.Select(c => c.SizeBytes).ToArray());
    }

    [Fact]
    public async Task ListChildrenAsync_Lists_Files_Inside_A_Folder()
    {
        var owner = await SeedUserAsync();
        var folder = await SeedFolderAsync(owner.Id, null, "Photos");
        await _service.CreateAsync(owner.Id, folder.Id, "y.jpg", "image/jpeg", BytesOf("y"));
        await _service.CreateAsync(owner.Id, folder.Id, "x.jpg", "image/jpeg", BytesOf("x"));
        await _service.CreateAsync(owner.Id, null, "outside.txt", "text/plain", BytesOf("o")); // root file, not inside the folder

        var children = await _service.ListChildrenAsync(owner.Id, folder.Id);

        Assert.Equal(new[] { "x.jpg", "y.jpg" }, children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task ListChildrenAsync_Does_Not_Return_Foreign_Files()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        await _service.CreateAsync(alice.Id, null, "alice.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(bob.Id, null, "bob.txt", "text/plain", BytesOf("b"));

        var aliceRoot = await _service.ListChildrenAsync(alice.Id, null);

        Assert.Single(aliceRoot);
        Assert.Equal("alice.txt", aliceRoot[0].Name);
    }

    [Fact]
    public async Task ListChildrenAsync_Excludes_Soft_Deleted_Files()
    {
        var owner = await SeedUserAsync();
        var keep = await _service.CreateAsync(owner.Id, null, "keep.txt", "text/plain", BytesOf("k"));
        var drop = await _service.CreateAsync(owner.Id, null, "drop.txt", "text/plain", BytesOf("d"));

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == drop.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Single(children);
        Assert.Equal(keep.Id, children[0].Id);
    }

    [Fact]
    public async Task ListChildrenAsync_Returns_Empty_List_For_Owner_With_No_Files()
    {
        var owner = await SeedUserAsync();

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Empty(children);
    }

    [Fact]
    public async Task SearchAsync_Finds_Files_By_Name_Case_Insensitive()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "Report-2026.pdf", "application/pdf", BytesOf("a"));
        await _service.CreateAsync(owner.Id, null, "vacation.jpg", "image/jpeg", BytesOf("b"));

        var results = await _service.SearchAsync(owner.Id, "REPORT");

        Assert.Single(results);
        Assert.Equal("Report-2026.pdf", results[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Finds_Files_By_MimeType()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "snap-a.jpg", "image/jpeg", BytesOf("a"));
        await _service.CreateAsync(owner.Id, null, "snap-b.jpg", "image/jpeg", BytesOf("b"));
        await _service.CreateAsync(owner.Id, null, "notes.txt", "text/plain", BytesOf("c"));

        var results = await _service.SearchAsync(owner.Id, "jpeg");

        Assert.Equal(new[] { "snap-a.jpg", "snap-b.jpg" }, results.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task SearchAsync_Excludes_Foreign_Owned_Files()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        await _service.CreateAsync(alice.Id, null, "match-alice.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(bob.Id, null, "match-bob.txt", "text/plain", BytesOf("b"));

        var results = await _service.SearchAsync(alice.Id, "match");

        Assert.Single(results);
        Assert.Equal("match-alice.txt", results[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Excludes_Soft_Deleted_Files()
    {
        var owner = await SeedUserAsync();
        var keep = await _service.CreateAsync(owner.Id, null, "keep-x.txt", "text/plain", BytesOf("k"));
        var drop = await _service.CreateAsync(owner.Id, null, "drop-x.txt", "text/plain", BytesOf("d"));

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == drop.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var results = await _service.SearchAsync(owner.Id, "x");

        Assert.Single(results);
        Assert.Equal(keep.Id, results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_Orders_Results_By_Name()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "zeta.txt", "text/plain", BytesOf("z"));
        await _service.CreateAsync(owner.Id, null, "alpha.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(owner.Id, null, "mike.txt", "text/plain", BytesOf("m"));

        var results = await _service.SearchAsync(owner.Id, "txt");

        Assert.Equal(new[] { "alpha.txt", "mike.txt", "zeta.txt" }, results.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task SearchAsync_Trims_Query_And_Returns_Empty_For_No_Matches()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("x"));

        var results = await _service.SearchAsync(owner.Id, "  zzzz  ");

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_Rejects_Empty_Or_Whitespace_Query(string? query)
    {
        var owner = await SeedUserAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.SearchAsync(owner.Id, query!));
    }

    [Fact]
    public async Task RenameAsync_Updates_Name_And_Sets_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "old.txt", "text/plain", BytesOf("x"));

        var renamed = await _service.RenameAsync(owner.Id, file.Id, "new.txt");

        Assert.NotNull(renamed);
        Assert.Equal("new.txt", renamed!.Name);
        Assert.NotNull(renamed.UpdatedAt);
    }

    [Fact]
    public async Task RenameAsync_Duplicate_Sibling_Throws()
    {
        var owner = await SeedUserAsync();
        var a = await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("a"));
        await _service.CreateAsync(owner.Id, null, "b.txt", "text/plain", BytesOf("b"));

        await Assert.ThrowsAsync<DuplicateFileNameException>(
            () => _service.RenameAsync(owner.Id, a.Id, "b.txt"));
    }

    [Fact]
    public async Task RenameAsync_Missing_Or_Foreign_Or_Deleted_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "a.txt", "text/plain", BytesOf("a"));

        Assert.Null(await _service.RenameAsync(bob.Id, aliceFile.Id, "stolen.txt"));
        Assert.Null(await _service.RenameAsync(alice.Id, Guid.NewGuid(), "ghost.txt"));

        await _service.SoftDeleteAsync(alice.Id, aliceFile.Id);
        Assert.Null(await _service.RenameAsync(alice.Id, aliceFile.Id, "after.txt"));
    }

    [Fact]
    public async Task MoveAsync_Updates_Parent_And_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var folder = await SeedFolderAsync(owner.Id, null, "Photos");
        var file = await _service.CreateAsync(owner.Id, null, "x.txt", "text/plain", BytesOf("x"));

        var moved = await _service.MoveAsync(owner.Id, file.Id, folder.Id);

        Assert.NotNull(moved);
        Assert.Equal(folder.Id, moved!.ParentFolderId);
        Assert.NotNull(moved.UpdatedAt);
    }

    [Fact]
    public async Task MoveAsync_To_Root_Works()
    {
        var owner = await SeedUserAsync();
        var folder = await SeedFolderAsync(owner.Id, null, "Photos");
        var file = await _service.CreateAsync(owner.Id, folder.Id, "x.txt", "text/plain", BytesOf("x"));

        var moved = await _service.MoveAsync(owner.Id, file.Id, null);

        Assert.NotNull(moved);
        Assert.Null(moved!.ParentFolderId);
    }

    [Fact]
    public async Task MoveAsync_To_Missing_Or_Foreign_Or_Deleted_Parent_Throws()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "a.txt", "text/plain", BytesOf("a"));
        var bobFolder = await SeedFolderAsync(bob.Id, null, "BobFolder");
        var deletedFolder = await SeedFolderAsync(alice.Id, null, "Trash", deleted: true);

        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFile.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFile.Id, bobFolder.Id));
        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFile.Id, deletedFolder.Id));
    }

    [Fact]
    public async Task SoftDeleteAsync_Marks_File_Deleted_And_Hides_From_Reads()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("hello"));

        Assert.True(await _service.SoftDeleteAsync(owner.Id, file.Id));

        Assert.Null(await _service.GetByIdAsync(file.Id, owner.Id));
        Assert.Null(await _service.OpenContentAsync(file.Id, owner.Id));
        Assert.Empty(await _service.ListChildrenAsync(owner.Id, null));
        Assert.Empty(await _service.SearchAsync(owner.Id, "doc"));
    }

    [Fact]
    public async Task SoftDeleteAsync_Missing_Or_Foreign_Or_Already_Deleted_Returns_False()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "a.txt", "text/plain", BytesOf("a"));

        Assert.False(await _service.SoftDeleteAsync(alice.Id, Guid.NewGuid()));
        Assert.False(await _service.SoftDeleteAsync(bob.Id, aliceFile.Id));

        await _service.SoftDeleteAsync(alice.Id, aliceFile.Id);
        Assert.False(await _service.SoftDeleteAsync(alice.Id, aliceFile.Id));
    }

    [Fact]
    public async Task SoftDeleteAsync_Decrements_BlobObject_ReferenceCount()
    {
        var owner = await SeedUserAsync();
        var file = await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("once"));

        var beforeCount = (await _db.BlobObjects.AsNoTracking().SingleAsync()).ReferenceCount;
        Assert.Equal(1, beforeCount);

        Assert.True(await _service.SoftDeleteAsync(owner.Id, file.Id));

        var after = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(0, after.ReferenceCount);
        Assert.Null(after.PurgeEligibleAt);
    }

    [Fact]
    public async Task SoftDeleteAsync_Twice_Does_Not_Decrement_Twice()
    {
        var owner = await SeedUserAsync();
        // Two distinct files sharing one BlobObject (same content, different names).
        var f1 = await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("dedup"));
        await _service.CreateAsync(owner.Id, null, "b.txt", "text/plain", BytesOf("dedup"));

        var initial = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(2, initial.ReferenceCount);

        Assert.True(await _service.SoftDeleteAsync(owner.Id, f1.Id));
        Assert.False(await _service.SoftDeleteAsync(owner.Id, f1.Id)); // idempotent

        var after = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, after.ReferenceCount);
    }

    [Fact]
    public async Task SoftDeleteAsync_One_Of_Two_Dedup_Files_Leaves_RefCount_1()
    {
        var owner = await SeedUserAsync();
        var f1 = await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("shared"));
        await _service.CreateAsync(owner.Id, null, "b.txt", "text/plain", BytesOf("shared"));

        Assert.True(await _service.SoftDeleteAsync(owner.Id, f1.Id));

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.ReferenceCount);
    }

    [Fact]
    public async Task SoftDeleteAsync_Both_Dedup_Files_Brings_RefCount_To_Zero()
    {
        var owner = await SeedUserAsync();
        var f1 = await _service.CreateAsync(owner.Id, null, "a.txt", "text/plain", BytesOf("shared"));
        var f2 = await _service.CreateAsync(owner.Id, null, "b.txt", "text/plain", BytesOf("shared"));

        Assert.True(await _service.SoftDeleteAsync(owner.Id, f1.Id));
        Assert.True(await _service.SoftDeleteAsync(owner.Id, f2.Id));

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(0, row.ReferenceCount);
        Assert.Null(row.PurgeEligibleAt);
        Assert.Equal(1, await _db.BlobObjects.CountAsync()); // row not deleted
    }

    [Fact]
    public async Task CreateAsync_Sequential_Duplicate_Does_Not_Increment_Existing_Blob()
    {
        // The sequential duplicate is caught by the pre-check before StoreAsync
        // runs, so this is the no-leak guarantee for the common path: no extra
        // blob is created and the existing blob's ReferenceCount stays at 1.
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("first"));

        await Assert.ThrowsAsync<DuplicateFileNameException>(
            () => _service.CreateAsync(owner.Id, null, "report.txt", "text/plain", BytesOf("second")));

        Assert.Equal(1, await _db.FileItems.CountAsync());
        Assert.Equal(1, await _db.BlobObjects.CountAsync());
        var blob = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, blob.ReferenceCount);
    }

    [Fact]
    public async Task CreateAsync_Releases_Blob_Reference_When_FileItem_Save_Fails_On_Race()
    {
        // Simulates the SQL-level race that the pre-check cannot prevent: a
        // concurrent writer inserts the same sibling name between this call's
        // pre-check and SaveChangesAsync. Without the release fix, the new
        // BlobObject created by StoreAsync would keep ReferenceCount = 1 even
        // though no FileItem references it — blocking janitor reclamation.
        var owner = await SeedUserAsync();
        var folder = await SeedFolderAsync(owner.Id, null, "Photos");

        // Pre-seed a blob that the racing-winner FileItem will reference.
        var winnerBlob = await _blobService.StoreAsync(BytesOf("winner-payload"));

        var racingStub = new ConflictingSiblingInsertingBlobService(
            inner: _blobService,
            db: _db,
            ownerUserId: owner.Id,
            parentFolderId: folder.Id,
            conflictName: "race.jpg",
            conflictingBlobId: winnerBlob.Id);

        var racingThumbs = new FileThumbnailService(
            _db, racingStub, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        var service = new FileItemService(_db, racingStub, racingThumbs, TimeProvider.System);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.CreateAsync(
                owner.Id, folder.Id, "race.jpg", "image/jpeg", BytesOf("loser-payload")));

        // Exactly one FileItem with the contested name survives — the winner.
        Assert.Equal(1, await _db.FileItems.CountAsync(f => f.Name == "race.jpg"));

        // The racing-loser's blob (created by StoreAsync but never adopted) is
        // released back to ReferenceCount = 0 and thus eligible for the janitor.
        Assert.NotNull(racingStub.LastStoredBlobId);
        var loserBlob = await _db.BlobObjects.AsNoTracking()
            .SingleAsync(b => b.Id == racingStub.LastStoredBlobId!.Value);
        Assert.Equal(0, loserBlob.ReferenceCount);

        // ReleaseAsync was called exactly once, on the racing-loser's blob.
        Assert.Equal(1, racingStub.ReleaseCount);
        Assert.Equal(racingStub.LastStoredBlobId, racingStub.LastReleasedId);

        // The winner's blob is untouched (ReferenceCount = 1 from its own StoreAsync).
        var winner = await _db.BlobObjects.AsNoTracking()
            .SingleAsync(b => b.Id == winnerBlob.Id);
        Assert.Equal(1, winner.ReferenceCount);
    }

    [Fact]
    public async Task SoftDeleteAsync_For_Foreign_File_Does_Not_Decrement()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await _service.CreateAsync(alice.Id, null, "a.txt", "text/plain", BytesOf("alice"));

        Assert.False(await _service.SoftDeleteAsync(bob.Id, aliceFile.Id));

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.ReferenceCount);
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return ms.ToArray();
    }

    private static byte[] CreateJpegBytes(int width, int height)
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task CreateAsync_For_PNG_Stores_Width_And_Height()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(width: 120, height: 80);

        var file = await _service.CreateAsync(
            owner.Id, null, "image.png", "image/png", new MemoryStream(png));

        Assert.Equal(120, file.Width);
        Assert.Equal(80, file.Height);
    }

    [Fact]
    public async Task CreateAsync_For_JPEG_Stores_Width_And_Height()
    {
        var owner = await SeedUserAsync();
        var jpeg = CreateJpegBytes(width: 64, height: 48);

        var file = await _service.CreateAsync(
            owner.Id, null, "image.jpg", "image/jpeg", new MemoryStream(jpeg));

        Assert.Equal(64, file.Width);
        Assert.Equal(48, file.Height);
    }

    [Fact]
    public async Task CreateAsync_For_NonImage_Content_Leaves_Width_And_Height_Null()
    {
        var owner = await SeedUserAsync();

        var file = await _service.CreateAsync(
            owner.Id, null, "notes.txt", "text/plain", BytesOf("hello world"));

        Assert.Null(file.Width);
        Assert.Null(file.Height);
    }

    [Fact]
    public async Task CreateAsync_For_Corrupt_Image_Content_Does_Not_Fail_And_Leaves_Dimensions_Null()
    {
        var owner = await SeedUserAsync();
        // Begin with a valid PNG signature, then garbage that breaks parsing
        // after the magic number.
        var corruptBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xff, 0xff, 0xff, 0xff };

        var file = await _service.CreateAsync(
            owner.Id, null, "broken.png", "image/png", new MemoryStream(corruptBytes));

        Assert.Equal("broken.png", file.Name);
        Assert.Equal(corruptBytes.LongLength, file.SizeBytes);
        Assert.Null(file.Width);
        Assert.Null(file.Height);
    }

    [Fact]
    public async Task ListChildrenAsync_Includes_Width_And_Height_For_Images()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "doc.txt", "text/plain", BytesOf("notes"));
        await _service.CreateAsync(owner.Id, null, "pic.png", "image/png",
            new MemoryStream(CreatePngBytes(200, 100)));

        var children = await _service.ListChildrenAsync(owner.Id, null);

        var doc = Assert.Single(children, c => c.Name == "doc.txt");
        var pic = Assert.Single(children, c => c.Name == "pic.png");
        Assert.Null(doc.Width);
        Assert.Null(doc.Height);
        Assert.Equal(200, pic.Width);
        Assert.Equal(100, pic.Height);
    }

    [Fact]
    public async Task SearchAsync_Includes_Width_And_Height_For_Images()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "snap.png", "image/png",
            new MemoryStream(CreatePngBytes(640, 480)));

        var results = await _service.SearchAsync(owner.Id, "snap");

        var hit = Assert.Single(results);
        Assert.Equal(640, hit.Width);
        Assert.Equal(480, hit.Height);
    }
}

// Wraps a real IBlobService and, between StoreAsync's return and the caller's
// SaveChangesAsync, inserts a sibling FileItem with the target name to force
// the unique-constraint catch path. ReleaseAsync calls are counted so the
// test can assert the failed insert released its blob reference exactly once.
internal sealed class ConflictingSiblingInsertingBlobService : IBlobService
{
    private readonly IBlobService _inner;
    private readonly AppDbContext _db;
    private readonly Guid _ownerUserId;
    private readonly Guid? _parentFolderId;
    private readonly string _conflictName;
    private readonly Guid _conflictingBlobId;

    public Guid? LastStoredBlobId { get; private set; }
    public int ReleaseCount { get; private set; }
    public Guid? LastReleasedId { get; private set; }

    public ConflictingSiblingInsertingBlobService(
        IBlobService inner,
        AppDbContext db,
        Guid ownerUserId,
        Guid? parentFolderId,
        string conflictName,
        Guid conflictingBlobId)
    {
        _inner = inner;
        _db = db;
        _ownerUserId = ownerUserId;
        _parentFolderId = parentFolderId;
        _conflictName = conflictName;
        _conflictingBlobId = conflictingBlobId;
    }

    public async Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var blob = await _inner.StoreAsync(content, cancellationToken);
        LastStoredBlobId = blob.Id;

        _db.FileItems.Add(new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _ownerUserId,
            ParentFolderId = _parentFolderId,
            BlobObjectId = _conflictingBlobId,
            Name = _conflictName,
            MimeType = "application/octet-stream",
            SizeBytes = 0,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return blob;
    }

    public async Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default)
    {
        // Route through StoreAsync so the conflict-injection behaviour still
        // fires; timings are irrelevant to these tests.
        var blob = await StoreAsync(content, cancellationToken);
        return new BlobStoreResult(blob, new BlobIngestTimings(0, 0, 0, 0));
    }

    public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        => _inner.OpenContentAsync(blobObjectId, cancellationToken);

    public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default)
        => _inner.StoreDerivedAsync(content, cancellationToken);

    public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        => _inner.OpenDerivedContentAsync(blobObjectId, cancellationToken);

    public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        => _inner.TryRestoreDerivedFromOriginalAsync(blobObjectId, cancellationToken);

    public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        ReleaseCount++;
        LastReleasedId = blobObjectId;
        return _inner.ReleaseAsync(blobObjectId, cancellationToken);
    }

    public Task MarkPurgeEligibleIfUnreferencedAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken = default)
        => _inner.MarkPurgeEligibleIfUnreferencedAsync(blobObjectId, cancellationToken);

    public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        => _inner.AcquireExistingAsync(blobObjectId, cancellationToken);
}

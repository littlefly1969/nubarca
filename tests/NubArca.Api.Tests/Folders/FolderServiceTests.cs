using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;

namespace NubArca.Api.Tests.Folders;

// SQLite in-memory unit tests. Real transactions, real unique constraints, no Docker.
//
// Note: the filtered unique index ux_folders_active_sibling_name uses
// "NULLS NOT DISTINCT" on PostgreSQL, but SQLite treats multiple NULLs as distinct.
// For null-parent (root) folders, FolderService relies on its pre-check, not on the
// DB constraint, to detect duplicates here. The PostgreSQL integration test in
// FolderServicePostgresTests covers the constraint-level race recovery.
public sealed class FolderServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FolderService _service;

    public FolderServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _service = new FolderService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
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

    [Fact]
    public async Task CreateAsync_Creates_Root_Folder()
    {
        var owner = await SeedUserAsync();

        var before = DateTime.UtcNow;
        var folder = await _service.CreateAsync(owner.Id, parentFolderId: null, "Photos");
        var after = DateTime.UtcNow;

        Assert.NotEqual(Guid.Empty, folder.Id);
        Assert.Equal(owner.Id, folder.OwnerUserId);
        Assert.Null(folder.ParentFolderId);
        Assert.Equal("Photos", folder.Name);
        Assert.Null(folder.UpdatedAt);
        Assert.Null(folder.DeletedAt);
        Assert.InRange(folder.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));

        var row = await _db.Folders.AsNoTracking().SingleAsync();
        Assert.Equal(folder.Id, row.Id);
    }

    [Fact]
    public async Task CreateAsync_Creates_Child_Folder_Under_Valid_Parent()
    {
        var owner = await SeedUserAsync();
        var parent = await SeedFolderAsync(owner.Id, null, "Photos");

        var child = await _service.CreateAsync(owner.Id, parent.Id, "2026");

        Assert.Equal(parent.Id, child.ParentFolderId);
        Assert.Equal("2026", child.Name);
    }

    [Fact]
    public async Task CreateAsync_Trims_Name_Before_Storing()
    {
        var owner = await SeedUserAsync();

        var folder = await _service.CreateAsync(owner.Id, null, "   Trimmed   ");

        Assert.Equal("Trimmed", folder.Name);
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Missing_Parent()
    {
        var owner = await SeedUserAsync();
        var bogus = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(owner.Id, bogus, "Child"));

        Assert.Equal(bogus, ex.FolderId);
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Foreign_Owner_Parent()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await SeedFolderAsync(alice.Id, null, "Photos");

        var ex = await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(bob.Id, aliceFolder.Id, "Stolen"));

        Assert.Equal(aliceFolder.Id, ex.FolderId);
    }

    [Fact]
    public async Task CreateAsync_Throws_FolderNotFound_For_Soft_Deleted_Parent()
    {
        var owner = await SeedUserAsync();
        var deleted = await SeedFolderAsync(owner.Id, null, "Trash", deleted: true);

        var ex = await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.CreateAsync(owner.Id, deleted.Id, "Child"));

        Assert.Equal(deleted.Id, ex.FolderId);
    }

    [Fact]
    public async Task CreateAsync_Throws_DuplicateFolderName_For_Active_Sibling_Name()
    {
        var owner = await SeedUserAsync();
        await _service.CreateAsync(owner.Id, null, "Photos");

        var ex = await Assert.ThrowsAsync<DuplicateFolderNameException>(
            () => _service.CreateAsync(owner.Id, null, "Photos"));

        Assert.Equal(owner.Id, ex.OwnerUserId);
        Assert.Null(ex.ParentFolderId);
        Assert.Equal("Photos", ex.Name);
    }

    [Fact]
    public async Task CreateAsync_Allows_Same_Name_Under_Different_Parents()
    {
        var owner = await SeedUserAsync();
        var parentA = await SeedFolderAsync(owner.Id, null, "A");
        var parentB = await SeedFolderAsync(owner.Id, null, "B");

        await _service.CreateAsync(owner.Id, parentA.Id, "Shared");
        await _service.CreateAsync(owner.Id, parentB.Id, "Shared");

        Assert.Equal(2, await _db.Folders.CountAsync(f => f.Name == "Shared"));
    }

    [Fact]
    public async Task CreateAsync_Allows_Same_Name_For_Different_Owners()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");

        await _service.CreateAsync(alice.Id, null, "Photos");
        await _service.CreateAsync(bob.Id, null, "Photos");

        Assert.Equal(2, await _db.Folders.CountAsync(f => f.Name == "Photos"));
    }

    [Fact]
    public async Task CreateAsync_Allows_Reuse_Of_Name_After_Sibling_Is_Soft_Deleted()
    {
        var owner = await SeedUserAsync();
        await SeedFolderAsync(owner.Id, null, "Photos", deleted: true);

        var fresh = await _service.CreateAsync(owner.Id, null, "Photos");

        Assert.Equal("Photos", fresh.Name);
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
            () => _service.CreateAsync(owner.Id, null, name));
    }

    [Fact]
    public async Task CreateAsync_Rejects_Null_Name()
    {
        var owner = await SeedUserAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, null!));
    }

    [Fact]
    public async Task CreateAsync_Rejects_Name_Longer_Than_255_Chars()
    {
        var owner = await SeedUserAsync();
        var tooLong = new string('a', 256);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(owner.Id, null, tooLong));
    }

    [Fact]
    public async Task CreateAsync_Accepts_Name_Of_255_Chars()
    {
        var owner = await SeedUserAsync();
        var atLimit = new string('a', 255);

        var folder = await _service.CreateAsync(owner.Id, null, atLimit);

        Assert.Equal(255, folder.Name.Length);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Created_Folder_For_Owner()
    {
        var owner = await SeedUserAsync();
        var created = await _service.CreateAsync(owner.Id, null, "Photos");

        var fetched = await _service.GetByIdAsync(created.Id, owner.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Foreign_Owner()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await _service.CreateAsync(alice.Id, null, "Photos");

        var fetched = await _service.GetByIdAsync(aliceFolder.Id, bob.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Soft_Deleted_Folder()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Trash");

        var tracked = await _db.Folders.FirstAsync(f => f.Id == folder.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var fetched = await _service.GetByIdAsync(folder.Id, owner.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Missing_Folder_Id()
    {
        var owner = await SeedUserAsync();

        var fetched = await _service.GetByIdAsync(Guid.NewGuid(), owner.Id);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task ListChildrenAsync_Lists_Root_Folders_For_Owner()
    {
        var owner = await SeedUserAsync();
        await SeedFolderAsync(owner.Id, null, "Photos");
        await SeedFolderAsync(owner.Id, null, "Docs");

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Equal(new[] { "Docs", "Photos" }, children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task ListChildrenAsync_Lists_Children_Of_A_Specific_Folder()
    {
        var owner = await SeedUserAsync();
        var parent = await SeedFolderAsync(owner.Id, null, "Photos");
        await SeedFolderAsync(owner.Id, parent.Id, "2026");
        await SeedFolderAsync(owner.Id, parent.Id, "2025");
        await SeedFolderAsync(owner.Id, null, "Misc"); // sibling of parent, not a child

        var children = await _service.ListChildrenAsync(owner.Id, parent.Id);

        Assert.Equal(new[] { "2025", "2026" }, children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task ListChildrenAsync_Does_Not_Return_Foreign_Folders()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        await SeedFolderAsync(alice.Id, null, "Alice-Photos");
        await SeedFolderAsync(bob.Id, null, "Bob-Photos");

        var aliceRoot = await _service.ListChildrenAsync(alice.Id, null);

        Assert.Single(aliceRoot);
        Assert.Equal("Alice-Photos", aliceRoot[0].Name);
    }

    [Fact]
    public async Task ListChildrenAsync_Excludes_Soft_Deleted_Folders()
    {
        var owner = await SeedUserAsync();
        await SeedFolderAsync(owner.Id, null, "Live");
        await SeedFolderAsync(owner.Id, null, "Trash", deleted: true);

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Single(children);
        Assert.Equal("Live", children[0].Name);
    }

    [Fact]
    public async Task ListChildrenAsync_Returns_Empty_List_For_Owner_With_No_Folders()
    {
        var owner = await SeedUserAsync();

        var children = await _service.ListChildrenAsync(owner.Id, null);

        Assert.Empty(children);
    }

    [Fact]
    public async Task RenameAsync_Updates_Name_And_Sets_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Old");

        var renamed = await _service.RenameAsync(owner.Id, folder.Id, "New");

        Assert.NotNull(renamed);
        Assert.Equal("New", renamed!.Name);
        Assert.NotNull(renamed.UpdatedAt);
    }

    [Fact]
    public async Task RenameAsync_To_Same_Name_Is_NoOp()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Same");

        var renamed = await _service.RenameAsync(owner.Id, folder.Id, "Same");

        Assert.NotNull(renamed);
        Assert.Null(renamed!.UpdatedAt);
    }

    [Fact]
    public async Task RenameAsync_To_Existing_Sibling_Name_Throws_Duplicate()
    {
        var owner = await SeedUserAsync();
        var a = await _service.CreateAsync(owner.Id, null, "A");
        await _service.CreateAsync(owner.Id, null, "B");

        await Assert.ThrowsAsync<DuplicateFolderNameException>(
            () => _service.RenameAsync(owner.Id, a.Id, "B"));
    }

    [Fact]
    public async Task RenameAsync_Missing_Or_Foreign_Or_Deleted_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await _service.CreateAsync(alice.Id, null, "Photos");

        Assert.Null(await _service.RenameAsync(bob.Id, aliceFolder.Id, "Stolen"));
        Assert.Null(await _service.RenameAsync(alice.Id, Guid.NewGuid(), "Whatever"));

        var deletedFolder = await SeedFolderAsync(alice.Id, null, "Trash", deleted: true);
        Assert.Null(await _service.RenameAsync(alice.Id, deletedFolder.Id, "Anything"));
    }

    [Fact]
    public async Task RenameAsync_Invalid_Name_Throws_ArgumentException()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Photos");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.RenameAsync(owner.Id, folder.Id, "a/b"));
    }

    [Fact]
    public async Task MoveAsync_Updates_Parent_And_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var parent = await _service.CreateAsync(owner.Id, null, "Parent");
        var child = await _service.CreateAsync(owner.Id, null, "Child");

        var moved = await _service.MoveAsync(owner.Id, child.Id, parent.Id);

        Assert.NotNull(moved);
        Assert.Equal(parent.Id, moved!.ParentFolderId);
        Assert.NotNull(moved.UpdatedAt);
    }

    [Fact]
    public async Task MoveAsync_To_Root_Works()
    {
        var owner = await SeedUserAsync();
        var parent = await _service.CreateAsync(owner.Id, null, "Parent");
        var child = await _service.CreateAsync(owner.Id, parent.Id, "Child");

        var moved = await _service.MoveAsync(owner.Id, child.Id, null);

        Assert.NotNull(moved);
        Assert.Null(moved!.ParentFolderId);
    }

    [Fact]
    public async Task MoveAsync_To_Missing_Or_Foreign_Or_Deleted_Parent_Throws()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await _service.CreateAsync(alice.Id, null, "Photos");
        var bobFolder = await _service.CreateAsync(bob.Id, null, "BobFolder");
        var deletedFolder = await SeedFolderAsync(alice.Id, null, "Trash", deleted: true);

        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFolder.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFolder.Id, bobFolder.Id));
        await Assert.ThrowsAsync<FolderNotFoundException>(
            () => _service.MoveAsync(alice.Id, aliceFolder.Id, deletedFolder.Id));
    }

    [Fact]
    public async Task MoveAsync_Into_Self_Or_Descendant_Throws_ArgumentException()
    {
        var owner = await SeedUserAsync();
        var top = await _service.CreateAsync(owner.Id, null, "Top");
        var mid = await _service.CreateAsync(owner.Id, top.Id, "Mid");
        var leaf = await _service.CreateAsync(owner.Id, mid.Id, "Leaf");

        // Into self
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.MoveAsync(owner.Id, top.Id, top.Id));
        // Into direct child
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.MoveAsync(owner.Id, top.Id, mid.Id));
        // Into deeper descendant
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.MoveAsync(owner.Id, top.Id, leaf.Id));
    }

    [Fact]
    public async Task MoveAsync_Duplicate_Sibling_In_Target_Throws()
    {
        var owner = await SeedUserAsync();
        var dest = await _service.CreateAsync(owner.Id, null, "Dest");
        var src = await _service.CreateAsync(owner.Id, null, "Src");
        await _service.CreateAsync(owner.Id, dest.Id, "Src"); // collision

        await Assert.ThrowsAsync<DuplicateFolderNameException>(
            () => _service.MoveAsync(owner.Id, src.Id, dest.Id));
    }

    [Fact]
    public async Task SoftDeleteAsync_Marks_Empty_Folder_Deleted()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Bye");

        var deleted = await _service.SoftDeleteAsync(owner.Id, folder.Id);

        Assert.True(deleted);
        var row = await _db.Folders.AsNoTracking().FirstAsync(f => f.Id == folder.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task SoftDeleteAsync_NonEmpty_Throws()
    {
        var owner = await SeedUserAsync();
        var parent = await _service.CreateAsync(owner.Id, null, "Parent");
        await _service.CreateAsync(owner.Id, parent.Id, "Child");

        await Assert.ThrowsAsync<FolderNotEmptyException>(
            () => _service.SoftDeleteAsync(owner.Id, parent.Id));
    }

    [Fact]
    public async Task SoftDeleteAsync_Missing_Or_Foreign_Or_Already_Deleted_Returns_False()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await _service.CreateAsync(alice.Id, null, "AliceFolder");

        Assert.False(await _service.SoftDeleteAsync(alice.Id, Guid.NewGuid()));
        Assert.False(await _service.SoftDeleteAsync(bob.Id, aliceFolder.Id));

        await _service.SoftDeleteAsync(alice.Id, aliceFolder.Id);
        Assert.False(await _service.SoftDeleteAsync(alice.Id, aliceFolder.Id));
    }
}

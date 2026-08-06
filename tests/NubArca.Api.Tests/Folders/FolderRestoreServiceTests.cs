using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;

namespace NubArca.Api.Tests.Folders;

// Service-level unit tests for IFolderService.RestoreAsync.
public sealed class FolderRestoreServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FolderService _service;

    public FolderRestoreServiceTests()
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

    [Fact]
    public async Task RestoreAsync_SoftDeleted_Folder_Clears_DeletedAt_And_Sets_UpdatedAt()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Photos");
        await _service.SoftDeleteAsync(owner.Id, folder.Id);

        var restored = await _service.RestoreAsync(owner.Id, folder.Id);

        Assert.NotNull(restored);
        Assert.Null(restored!.DeletedAt);
        Assert.NotNull(restored.UpdatedAt);
    }

    [Fact]
    public async Task RestoreAsync_Reappears_In_ListChildren_And_GetById()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Photos");
        await _service.SoftDeleteAsync(owner.Id, folder.Id);

        Assert.Empty(await _service.ListChildrenAsync(owner.Id, null));
        Assert.Null(await _service.GetByIdAsync(folder.Id, owner.Id));

        await _service.RestoreAsync(owner.Id, folder.Id);

        Assert.Single(await _service.ListChildrenAsync(owner.Id, null));
        Assert.NotNull(await _service.GetByIdAsync(folder.Id, owner.Id));
    }

    [Fact]
    public async Task RestoreAsync_Foreign_Folder_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFolder = await _service.CreateAsync(alice.Id, null, "Alice");
        await _service.SoftDeleteAsync(alice.Id, aliceFolder.Id);

        var restored = await _service.RestoreAsync(bob.Id, aliceFolder.Id);

        Assert.Null(restored);
        var row = await _db.Folders.AsNoTracking().FirstAsync(f => f.Id == aliceFolder.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_Missing_Folder_Returns_Null()
    {
        var owner = await SeedUserAsync();

        var restored = await _service.RestoreAsync(owner.Id, Guid.NewGuid());

        Assert.Null(restored);
    }

    [Fact]
    public async Task RestoreAsync_Already_Active_Folder_Is_Idempotent_NoOp()
    {
        var owner = await SeedUserAsync();
        var folder = await _service.CreateAsync(owner.Id, null, "Photos");

        var restored = await _service.RestoreAsync(owner.Id, folder.Id);

        Assert.NotNull(restored);
        Assert.Equal(folder.Id, restored!.Id);
        Assert.Null(restored.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_With_Active_Sibling_Same_Name_Throws_Duplicate()
    {
        var owner = await SeedUserAsync();
        var first = await _service.CreateAsync(owner.Id, null, "Photos");
        await _service.SoftDeleteAsync(owner.Id, first.Id);

        var occupant = await _service.CreateAsync(owner.Id, null, "Photos");

        await Assert.ThrowsAsync<DuplicateFolderNameException>(
            () => _service.RestoreAsync(owner.Id, first.Id));

        var firstRow = await _db.Folders.AsNoTracking().FirstAsync(f => f.Id == first.Id);
        Assert.NotNull(firstRow.DeletedAt);
        var occupantRow = await _db.Folders.AsNoTracking().FirstAsync(f => f.Id == occupant.Id);
        Assert.Null(occupantRow.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_With_SoftDeleted_Parent_Throws_RestoreParentDeleted()
    {
        var owner = await SeedUserAsync();
        var parent = await _service.CreateAsync(owner.Id, null, "Outer");
        var child = await _service.CreateAsync(owner.Id, parent.Id, "Inner");

        await _service.SoftDeleteAsync(owner.Id, child.Id);
        await _service.SoftDeleteAsync(owner.Id, parent.Id);

        var ex = await Assert.ThrowsAsync<RestoreParentDeletedException>(
            () => _service.RestoreAsync(owner.Id, child.Id));
        Assert.Equal(parent.Id, ex.ParentFolderId);

        var row = await _db.Folders.AsNoTracking().FirstAsync(f => f.Id == child.Id);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task RestoreAsync_Restore_Parent_Then_Child_Succeeds()
    {
        var owner = await SeedUserAsync();
        var parent = await _service.CreateAsync(owner.Id, null, "Outer");
        var child = await _service.CreateAsync(owner.Id, parent.Id, "Inner");

        await _service.SoftDeleteAsync(owner.Id, child.Id);
        await _service.SoftDeleteAsync(owner.Id, parent.Id);

        await Assert.ThrowsAsync<RestoreParentDeletedException>(
            () => _service.RestoreAsync(owner.Id, child.Id));

        var restoredParent = await _service.RestoreAsync(owner.Id, parent.Id);
        Assert.NotNull(restoredParent);

        var restoredChild = await _service.RestoreAsync(owner.Id, child.Id);
        Assert.NotNull(restoredChild);
        Assert.Null(restoredChild!.DeletedAt);
    }
}

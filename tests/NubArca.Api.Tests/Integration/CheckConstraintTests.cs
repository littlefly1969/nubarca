using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Npgsql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// PostgreSQL-only verification that the numeric invariants protected by service
// code are also enforced by the database. Each test inserts a row that would
// silently break the invariant if the CHECK constraint were missing, then
// asserts the DB rejects it with a check-violation (SqlState 23514).
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class CheckConstraintTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    private DbContextOptions<AppDbContext>? _dbOptions;

    public CheckConstraintTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;

        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task BlobObject_Negative_ReferenceCount_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        db.BlobObjects.Add(NewBlob(referenceCount: -1));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_blob_objects_reference_count_non_negative");
    }

    [SkippableFact]
    public async Task BlobObject_Negative_SizeBytes_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        db.BlobObjects.Add(NewBlob(sizeBytes: -1));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_blob_objects_size_bytes_non_negative");
    }

    [SkippableFact]
    public async Task FileItem_Negative_SizeBytes_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var user = NewUser();
        var blob = NewBlob();
        db.Users.Add(user);
        db.BlobObjects.Add(blob);
        await db.SaveChangesAsync();

        db.FileItems.Add(new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            ParentFolderId = null,
            BlobObjectId = blob.Id,
            Name = "x.bin",
            MimeType = "application/octet-stream",
            SizeBytes = -1,
            CreatedAt = DateTime.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_file_items_size_bytes_non_negative");
    }

    [SkippableFact]
    public async Task ShareLink_Negative_DownloadCount_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var (user, file) = await SeedUserAndFileAsync(db);

        db.ShareLinks.Add(new ShareLink
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            FileItemId = file.Id,
            TokenHash = new string('a', 64),
            CreatedAt = DateTime.UtcNow,
            DownloadCount = -1,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_share_links_download_count_non_negative");
    }

    [SkippableFact]
    public async Task ShareLink_Zero_MaxDownloads_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var (user, file) = await SeedUserAndFileAsync(db);

        db.ShareLinks.Add(new ShareLink
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            FileItemId = file.Id,
            TokenHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow,
            MaxDownloads = 0,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_share_links_max_downloads_positive_or_null");
    }

    [SkippableFact]
    public async Task ShareLink_Negative_MaxDownloads_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var (user, file) = await SeedUserAndFileAsync(db);

        db.ShareLinks.Add(new ShareLink
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            FileItemId = file.Id,
            TokenHash = new string('c', 64),
            CreatedAt = DateTime.UtcNow,
            MaxDownloads = -3,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "ck_share_links_max_downloads_positive_or_null");
    }

    [SkippableFact]
    public async Task ShareLink_Null_MaxDownloads_Is_Accepted()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var (user, file) = await SeedUserAndFileAsync(db);

        db.ShareLinks.Add(new ShareLink
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            FileItemId = file.Id,
            TokenHash = new string('d', 64),
            CreatedAt = DateTime.UtcNow,
            MaxDownloads = null,
        });

        await db.SaveChangesAsync();
    }

    private static void AssertCheckViolation(DbUpdateException ex, string constraintName)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
        Assert.Equal(constraintName, pg.ConstraintName);
    }

    private static User NewUser(string email = "owner@check.example")
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static BlobObject NewBlob(long sizeBytes = 1, long referenceCount = 1)
    {
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        return new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            SizeBytes = sizeBytes,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            ReferenceCount = referenceCount,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static async Task<(User User, FileItem File)> SeedUserAndFileAsync(AppDbContext db)
    {
        var user = NewUser();
        var blob = NewBlob();
        db.Users.Add(user);
        db.BlobObjects.Add(blob);
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            ParentFolderId = null,
            BlobObjectId = blob.Id,
            Name = "shared.bin",
            MimeType = "application/octet-stream",
            SizeBytes = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.FileItems.Add(file);
        await db.SaveChangesAsync();
        return (user, file);
    }
}

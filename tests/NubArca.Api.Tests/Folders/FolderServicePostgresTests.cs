using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Folders;

// Real PostgreSQL via Testcontainers. Skipped when Docker is unavailable.
//
// Covers the path the SQLite unit tests cannot:
//   - concurrent CreateAsync calls racing on ux_folders_active_sibling_name
//     (filtered unique with NULLS NOT DISTINCT — PostgreSQL 15+ specific).
//     The pre-check in FolderService is provider-agnostic; the catch-on-
//     PostgresException 23505 / ux_folders_active_sibling_name path is only
//     reachable on real PG and only under concurrent contention.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class FolderServicePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public FolderServicePostgresTests(PostgresContainerFixture fixture)
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

    private FolderService NewService()
    {
        var db = new AppDbContext(_dbOptions!);
        return new FolderService(db, TimeProvider.System);
    }

    private async Task<Guid> SeedOwnerAsync(string email = "owner@example.com")
    {
        var id = Guid.NewGuid();
        await using var db = new AppDbContext(_dbOptions!);
        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [SkippableFact]
    public async Task CreateAsync_Concurrent_Same_Root_Folder_Name_Allows_Exactly_One_Success()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = await SeedOwnerAsync();

        const int N = 10;
        const string name = "race-folder";

        var tasks = Enumerable.Range(0, N)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await NewService().CreateAsync(ownerId, parentFolderId: null, name);
                    return (Success: true, Exception: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Success: false, Exception: ex);
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var successes = results.Count(r => r.Success);
        var duplicates = results.Count(r => r.Exception is DuplicateFolderNameException);

        Assert.Equal(1, successes);
        Assert.Equal(N - 1, duplicates);
        Assert.Equal(0, results.Length - successes - duplicates);

        await using var verify = new AppDbContext(_dbOptions!);
        var rows = await verify.Folders.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(name, rows[0].Name);
        Assert.Null(rows[0].ParentFolderId);
    }
}

using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Users;

// Real PostgreSQL via Testcontainers. Skipped when Docker is unavailable.
//
// Covers the path the SQLite unit tests cannot:
//   - concurrent CreateAsync calls with the same email racing on the
//     ux_users_email unique constraint (Npgsql throws PostgresException with
//     SqlState 23505, UserService catches it and throws UserAlreadyExistsException)
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class UserServicePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public UserServicePostgresTests(PostgresContainerFixture fixture)
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

    private UserService NewService()
    {
        var db = new AppDbContext(_dbOptions!);
        return new UserService(db, TimeProvider.System);
    }

    [SkippableFact]
    public async Task CreateAsync_Persists_User_And_GetByIdAsync_Returns_It()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var created = await NewService().CreateAsync("alice@example.com", "Alice");

        var fetched = await NewService().GetByIdAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("alice@example.com", fetched.Email);
        Assert.Equal("Alice", fetched.DisplayName);
        Assert.Null(fetched.PasswordHash);
        Assert.Null(fetched.DisabledAt);
    }

    [SkippableFact]
    public async Task CreateAsync_Sequential_Duplicate_Email_Throws()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await NewService().CreateAsync("dup@example.com", "First");

        var ex = await Assert.ThrowsAsync<UserAlreadyExistsException>(
            () => NewService().CreateAsync("DUP@example.com", "Second"));

        Assert.Equal("dup@example.com", ex.Email);

        await using var verify = new AppDbContext(_dbOptions!);
        Assert.Equal(1, await verify.Users.CountAsync());
    }

    [SkippableFact]
    public async Task CreateAsync_Concurrent_Same_Email_Allows_Exactly_One_Success()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        const int N = 10;
        const string email = "race@example.com";

        var tasks = Enumerable.Range(0, N)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    var service = NewService();
                    await service.CreateAsync(email, $"Racer-{i}");
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
        var duplicates = results.Count(r => r.Exception is UserAlreadyExistsException);

        Assert.Equal(1, successes);
        Assert.Equal(N - 1, duplicates);
        Assert.Equal(0, results.Length - successes - duplicates);

        await using var verify = new AppDbContext(_dbOptions!);
        var rows = await verify.Users.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(email, rows[0].Email);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;

namespace NubArca.Api.Tests.Endpoints;

public sealed class SqliteWebApplicationFactoryTests
{
    [Fact]
    public async Task Schema_Clone_Preserves_Per_Factory_Data_Isolation()
    {
        using var first = new SqliteWebApplicationFactory();
        first.EnsureDatabaseCreated();
        await first.SeedUserAsync("only-in-first@example.com");

        using var second = new SqliteWebApplicationFactory();
        second.EnsureDatabaseCreated();

        using var secondScope = second.Services.CreateScope();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await secondDb.Users.AnyAsync());

        await second.SeedUserAsync("only-in-second@example.com");

        using var firstScope = first.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(["only-in-first@example.com"], await firstDb.Users.Select(u => u.Email).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_Factories_Receive_Complete_Independent_Schemas()
    {
        var tasks = Enumerable.Range(0, 4).Select(async index =>
        {
            using var factory = new SqliteWebApplicationFactory();
            factory.EnsureDatabaseCreated();
            await factory.SeedUserAsync($"parallel-{index}@example.com");

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Users.Select(u => u.Email).SingleAsync();
        });

        var emails = await Task.WhenAll(tasks);

        Assert.Equal(4, emails.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Opted_In_Custom_Configuration_Reuses_A_Clean_Isolated_Host()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Testing:PoolVariant"] = "custom-clean",
        };

        string firstRoot;
        using (var first = new SqliteWebApplicationFactory(settings, poolHost: true))
        {
            first.EnsureDatabaseCreated();
            firstRoot = first.StorageRoot;
            await first.SeedUserAsync("must-be-reset@example.com");
        }

        using var second = new SqliteWebApplicationFactory(settings, poolHost: true);
        second.EnsureDatabaseCreated();
        Assert.Equal(firstRoot, second.StorageRoot);

        using var scope = second.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task Host_Without_A_Schema_Is_Discarded_Instead_Of_Poisoning_Custom_Pool()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Testing:PoolVariant"] = "uninitialized",
        };

        string uninitializedRoot;
        using (var uninitialized = new SqliteWebApplicationFactory(settings, poolHost: true))
        {
            _ = uninitialized.CreateClient();
            uninitializedRoot = uninitialized.StorageRoot;
        }

        using var initialized = new SqliteWebApplicationFactory(settings, poolHost: true);
        initialized.EnsureDatabaseCreated();
        Assert.NotEqual(uninitializedRoot, initialized.StorageRoot);

        await initialized.SeedUserAsync("schema-exists@example.com");
    }
}

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

public sealed class AiStatusServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AiProfileRegistry _registry;
    private readonly IReadOnlyList<IAiBackend> _backends;

    public AiStatusServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _registry = new AiProfileRegistry(_db, TimeProvider.System);
        _backends = new IAiBackend[] { new NoneAiBackend(), new DeterministicAiBackend() };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private AiStatusService Status(bool enabled, string provider = "none")
    {
        var options = Options.Create(new AiOptions { Enabled = enabled, Provider = provider });
        var resolver = new AiBackendResolver(options, _registry, _backends);
        return new AiStatusService(options, _registry, resolver);
    }

    [Fact]
    public async Task Status_When_Disabled_Reports_All_Capabilities_Unavailable()
    {
        var status = await Status(enabled: false).GetStatusAsync();

        Assert.False(status.Enabled);
        Assert.Equal("none", status.DefaultProvider);
        Assert.NotEmpty(status.Capabilities);
        Assert.All(status.Capabilities, c =>
        {
            Assert.False(c.Available);
            Assert.Equal(AiUnavailableReasons.Disabled, c.UnavailableReason);
        });
    }

    [Fact]
    public async Task Status_With_Deterministic_Profiles_Reports_Availability_And_Counts()
    {
        await _registry.SeedDeterministicProfilesAsync();

        var status = await Status(enabled: true, provider: "deterministic").GetStatusAsync();

        Assert.True(status.Enabled);
        Assert.Equal(1, status.ModelCount);
        Assert.True(status.ProfileCount >= 7);

        var image = status.Capabilities.Single(c => c.Capability == AiCapabilities.ImageEmbedding);
        Assert.True(image.Available);
        Assert.Equal("det-image-embedding-v1", image.DefaultProfileKey);
        Assert.Equal(32, image.Dimension);
    }

    [Fact]
    public void Status_Dtos_Expose_No_Raw_Vectors_Or_Internal_Ids()
    {
        // Defensive contract: status DTOs must never surface raw vectors or
        // internal GUID identifiers (profile is referenced by stable KEY only).
        foreach (var type in new[] { typeof(AiStatus), typeof(AiCapabilityStatus) })
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.NotEqual(typeof(float[]), prop.PropertyType);
                Assert.NotEqual(typeof(byte[]), prop.PropertyType);
                Assert.NotEqual(typeof(Guid), prop.PropertyType);
                Assert.NotEqual(typeof(Guid?), prop.PropertyType);

                var name = prop.Name.ToLowerInvariant();
                Assert.DoesNotContain("vector", name);
                Assert.DoesNotContain("embedding", name);
            }
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

// Profile-driven backend resolution. Provider is decided by the profile's model
// (AiModel.Provider), AI is disabled by default, and unavailable conditions are
// returned (never thrown) and never write per-blob status rows.
public sealed class AiBackendResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AiProfileRegistry _registry;
    private readonly IReadOnlyList<IAiBackend> _backends;

    public AiBackendResolverTests()
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

    private AiBackendResolver Resolver(bool enabled, string provider = "none")
    {
        var options = Options.Create(new AiOptions { Enabled = enabled, Provider = provider });
        return new AiBackendResolver(options, _registry, _backends);
    }

    [Fact]
    public async Task Ai_Disabled_By_Default_Resolves_Unavailable()
    {
        await _registry.SeedDeterministicProfilesAsync();
        var resolver = Resolver(enabled: false);

        var result = await resolver.ResolveForCapabilityAsync<IImageEmbedder>(AiCapabilities.ImageEmbedding);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Backend);
        Assert.Equal(AiUnavailableReasons.Disabled, result.Resolution.UnavailableReason);
    }

    [Fact]
    public async Task Enabled_Without_Profiles_Reports_No_Default_Profile()
    {
        var resolver = Resolver(enabled: true);

        var result = await resolver.ResolveForCapabilityAsync<IImageEmbedder>(AiCapabilities.ImageEmbedding);

        Assert.False(result.IsAvailable);
        Assert.Equal(AiUnavailableReasons.NoDefaultProfile, result.Resolution.UnavailableReason);
    }

    [Fact]
    public async Task Deterministic_Profile_Resolves_To_Deterministic_Backend()
    {
        await _registry.SeedDeterministicProfilesAsync();
        var resolver = Resolver(enabled: true);

        var result = await resolver.ResolveForCapabilityAsync<IImageEmbedder>(AiCapabilities.ImageEmbedding);

        Assert.True(result.IsAvailable);
        Assert.IsType<DeterministicAiBackend>(result.Backend);
        Assert.Equal(AiProviders.Deterministic, result.Resolution.Provider);
        Assert.Equal(32, result.Resolution.Dimension);
        Assert.Equal(AiDistanceMetrics.Cosine, result.Resolution.DistanceMetric);
        Assert.Equal("det-image-embedding-v1", result.Resolution.ProfileKey);
    }

    [Fact]
    public async Task Resolve_By_Profile_Key_Works_And_Reports_Missing_Key()
    {
        await _registry.SeedDeterministicProfilesAsync();
        var resolver = Resolver(enabled: true);

        var ok = await resolver.ResolveForProfileKeyAsync<IFaceEmbedder>("det-face-embedding-v1");
        Assert.True(ok.IsAvailable);
        Assert.IsType<DeterministicAiBackend>(ok.Backend);

        var missing = await resolver.ResolveForProfileKeyAsync<IFaceEmbedder>("does-not-exist");
        Assert.False(missing.IsAvailable);
        Assert.Equal(AiUnavailableReasons.ProfileNotFound, missing.Resolution.UnavailableReason);
    }

    [Fact]
    public async Task None_Provider_Resolves_Unavailable_Without_Throwing_Or_Writing_Status_Rows()
    {
        // A profile bound to a none-provider model.
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "none-model-v1",
            Provider = AiProviders.None,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Version = 1,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "none-image-embedding-v1",
            AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 16,
            DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = true,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();

        var resolver = Resolver(enabled: true);

        var result = await resolver.ResolveForCapabilityAsync<IImageEmbedder>(AiCapabilities.ImageEmbedding);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Backend);
        Assert.Equal(AiUnavailableReasons.ProviderNone, result.Resolution.UnavailableReason);

        // The no-op contract: an unavailable provider must NOT create per-blob
        // skipped/failed status rows.
        Assert.Equal(0, await _db.BlobAiArtifactStatuses.CountAsync());
    }

    [Fact]
    public async Task Capability_Without_A_Backend_Reports_Unsupported_When_Enabled()
    {
        await _registry.SeedDeterministicProfilesAsync();
        var resolver = Resolver(enabled: true);

        // Face clustering is a derived job, not a backend capability.
        var resolution = await resolver.GetCapabilityAvailabilityAsync(AiCapabilities.FaceClustering);

        Assert.False(resolution.IsAvailable);
        Assert.Equal(AiUnavailableReasons.CapabilityUnsupported, resolution.UnavailableReason);
    }
}

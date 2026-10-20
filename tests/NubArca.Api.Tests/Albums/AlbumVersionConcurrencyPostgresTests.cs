using Microsoft.EntityFrameworkCore;
using NubArca.Api.Albums;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// The owner turning the album's order round and a curator's conditional edit,
/// released together on real PostgreSQL connections. Every mutation that took
/// effect must leave the album one version further on: a sort change that read
/// the version, lost the race and then wrote its stale value back would hide
/// the curator's edit from every client comparing versions.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class AlbumVersionConcurrencyPostgresTests : IAsyncLifetime
{
    private const int Rounds = 25;

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();

    public AlbumVersionConcurrencyPostgresTests(PostgresContainerFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com",
            DisplayName = "Owner", CreatedAt = DateTime.UtcNow,
        });
        db.Albums.Add(new Album
        {
            Id = _albumId, OwnerUserId = _ownerId, Name = "Album",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_sort_change_racing_a_curator_edit_never_hides_either_version_step()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        for (var round = 0; round < Rounds; round++)
        {
            var before = await VersionAsync();
            var oldestFirst = round % 2 == 0;
            using var barrier = new Barrier(2);

            async Task<bool> Sort()
            {
                await using var db = NewContext();
                var albums = new AlbumService(db, TimeProvider.System);
                barrier.SignalAndWait();
                var detail = await albums.SetSortDirectionAsync(_albumId, _ownerId, oldestFirst);
                Assert.NotNull(detail);
                return true;
            }

            async Task<bool> Edit()
            {
                await using var db = NewContext();
                var editing = new AlbumEditingService(
                    db, TimeProvider.System, new AlbumAccessResolver(db), new NoopAuditLogger());
                barrier.SignalAndWait();
                var result = await editing.UpdateDetailsAsync(
                    _ownerId, _albumId, before, name: null, description: $"round {round}", ipAddress: null);
                // The curator's edit may lose the race to the sort change and be
                // refused as a conflict; it is never applied over a moved version.
                Assert.Contains(result.Outcome, new[] { AlbumEditOutcome.Ok, AlbumEditOutcome.VersionConflict });
                return result.Outcome == AlbumEditOutcome.Ok;
            }

            var results = await Task.WhenAll(Task.Run(Sort), Task.Run(Edit));
            var edited = results[1];

            // The direction changes every round, so the sort always moves the
            // version once; an applied edit moves it once more.
            Assert.Equal(before + 1 + (edited ? 1 : 0), await VersionAsync());
            await using var check = NewContext();
            var album = await check.Albums.AsNoTracking().SingleAsync(a => a.Id == _albumId);
            Assert.Equal(oldestFirst, album.SortOldestFirst);
        }
    }

    [SkippableFact]
    public async Task Setting_the_same_order_again_does_not_move_the_version()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        await using var db = NewContext();
        var albums = new AlbumService(db, TimeProvider.System);
        var before = await VersionAsync();
        var detail = await albums.SetSortDirectionAsync(_albumId, _ownerId, oldestFirst: false);
        Assert.NotNull(detail);
        Assert.Equal(before, detail!.Version);
        Assert.Equal(before, await VersionAsync());
        Assert.Null(await albums.SetSortDirectionAsync(_albumId, Guid.NewGuid(), oldestFirst: true));
        Assert.Equal(before, await VersionAsync());
    }

    private async Task<int> VersionAsync()
    {
        await using var db = NewContext();
        return await db.Albums.AsNoTracking().Where(a => a.Id == _albumId).Select(a => a.Version).SingleAsync();
    }

    private AppDbContext NewContext() => new(_dbOptions!);

    private sealed class NoopAuditLogger : IAuditLogger
    {
        public Task LogAsync(
            AuditActor actor, string action, string entityType, Guid? entityId,
            string? ipAddress, object? metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteAsync(
            AuditActor actor, string action, string entityType, Guid? entityId,
            string? ipAddress, object? metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

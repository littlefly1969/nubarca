using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.MediaLists;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.MediaLists;

// Two devices of one account writing the same list at the same instant, on real
// PostgreSQL connections (the SQLite host serialises every request on one
// connection, so it cannot race). Pins: one current selection per account, and
// overlapping adds never duplicate an entry nor fail the request.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class MediaListConcurrencyPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private DbContextOptions<AppDbContext>? _options;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly List<Guid> _files = new();

    public async Task InitializeAsync()
    {
        if (!fixture.Available) return;
        _options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString!).Options;
        await fixture.ResetDatabaseAsync();
        await using var db = new AppDbContext(_options);
        db.Users.Add(new User
        {
            Id = _owner, Email = $"owner-{_owner:N}@example.com", DisplayName = "Owner", CreatedAt = DateTime.UtcNow,
        });
        var blob = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blob, Sha256 = new string('c', 64), StorageKey = "fixture-only", SizeBytes = 100,
            ReferenceCount = 40, CreatedAt = DateTime.UtcNow,
        });
        for (var i = 0; i < 40; i++)
        {
            var id = Guid.NewGuid();
            _files.Add(id);
            db.FileItems.Add(new FileItem
            {
                Id = id, OwnerUserId = _owner, BlobObjectId = blob, Name = $"p{i}.jpg", MimeType = "image/jpeg",
                SizeBytes = 100, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> OnOwnConnectionAsync<T>(Barrier barrier, Func<MediaListService, Task<T>> work)
    {
        await using var db = new AppDbContext(_options!);
        var service = new MediaListService(db, new FileItemService(db, null!, null!, TimeProvider.System), TimeProvider.System);
        await Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        return await work(service);
    }

    [SkippableFact]
    public async Task Two_devices_opening_the_selection_at_once_share_one()
    {
        Skip.IfNot(fixture.Available, "PostgreSQL container is not available.");
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(
            OnOwnConnectionAsync(barrier, s => s.GetSelectionAsync(_owner, default)),
            OnOwnConnectionAsync(barrier, s => s.GetSelectionAsync(_owner, default)));
        Assert.Equal(results[0].Id, results[1].Id);
        await using var db = new AppDbContext(_options!);
        Assert.Equal(1, await db.MediaLists.CountAsync(l => l.OwnerUserId == _owner && l.Kind == MediaListKind.CurrentSelection));
    }

    [SkippableFact]
    public async Task Overlapping_adds_from_two_devices_stay_deduplicated()
    {
        Skip.IfNot(fixture.Available, "PostgreSQL container is not available.");
        Guid listId;
        await using (var db = new AppDbContext(_options!))
        {
            var service = new MediaListService(db, new FileItemService(db, null!, null!, TimeProvider.System), TimeProvider.System);
            listId = (await service.GetSelectionAsync(_owner, default)).Id;
        }

        using var barrier = new Barrier(2);
        var reversed = _files.AsEnumerable().Reverse().ToList();
        var results = await Task.WhenAll(
            OnOwnConnectionAsync(barrier, s => s.AddItemsAsync(_owner, listId, _files, default)),
            OnOwnConnectionAsync(barrier, s => s.AddItemsAsync(_owner, listId, reversed, default)));

        Assert.All(results, r => Assert.Equal(MediaListOutcome.Ok, r.Outcome));
        await using var check = new AppDbContext(_options!);
        var entries = await check.MediaListItems.Where(i => i.ListId == listId).Select(i => i.FileItemId).ToListAsync();
        Assert.Equal(_files.Count, entries.Count);
        Assert.Equal(_files.OrderBy(x => x), entries.OrderBy(x => x));
    }
}

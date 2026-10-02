using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Tv;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The party ending while the regia puts the guest book on the television, on
/// real PostgreSQL.
///
/// The invariant: a party that is not LIVE never keeps the book on the screen
/// (`GuestbookTvActive`). Both requests start from a book that is NOT on the
/// television — the case where a clear filtered on the flag matches nothing,
/// takes no lock, and would let a concurrent `show_on_tv` outlive the evening.
/// Leaving LIVE must therefore join the same boundary as every guest-book
/// command — the party link's row — whatever the flag holds.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyGuestbookEndLiveRacePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();

    public PartyGuestbookEndLiveRacePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com", DisplayName = "Owner", CreatedAt = now,
        });
        db.Albums.Add(new Album
        {
            Id = _albumId, OwnerUserId = _ownerId, Name = "Festa", CreatedAt = now, UpdatedAt = now,
        });
        PartySeed.Party(db, _partyId, _ownerId, _albumId, status: PartyStatuses.Live);
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId, PartyId = _partyId, OwnerUserId = _ownerId, AlbumId = _albumId,
            TokenHash = new string('a', 64), Enabled = true, GuestbookEnabled = true,
            CreatedAt = now, UpdatedAt = now,
        });
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = new string('d', 64), SizeBytes = 1,
            StorageKey = "objects/dd/dd/test", ReferenceCount = 1, CreatedAt = now,
        });
        db.PartyGuestbookEntries.Add(new PartyGuestbookEntry
        {
            Id = Guid.NewGuid(), PartyId = _partyId, OwnerUserId = _ownerId, PartyAlbumLinkId = _linkId,
            AuthorDisplayName = "Anna", Body = "Auguri", BlobObjectId = blobId,
            PhotoWidth = 1200, PhotoHeight = 800,
            Status = PartyMessageStatuses.Visible, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_show_on_tv_that_wins_the_row_is_cleared_by_the_end_of_the_party()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        Assert.False(await TvActiveAsync());

        // The regia's "show on TV", holding the party link's row, not yet committed.
        await using var regia = NewContext();
        await using var regiaTx = await regia.Database.BeginTransactionAsync();
        var shown = await Live(regia).ExecuteAsync(
            _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, 0, PartyGuestbookLiveRights.Owner);
        Assert.Null(shown.Error);

        // The host ends the party. Its claim on the link must WAIT for the
        // regia — even though the committed flag is still false.
        await using var host = NewContext();
        var end = Parties(host).TransitionAsync(_ownerId, _partyId, PartyLifecycleAction.EndLive, 1);
        await WaitForALockWaiterAsync();
        Assert.False(end.IsCompleted, "leaving LIVE must wait for the guest book's transaction");

        await regiaTx.CommitAsync();

        // It reads what the regia committed, after the lock, and takes it back.
        Assert.Equal(PartyMutationOutcome.Ok, (await end).Outcome);
        await AssertOverAndOffAsync();
        Assert.Equal(2, await ControlVersionAsync()); // once for the show, once for the clear
    }

    [SkippableFact]
    public async Task A_show_on_tv_that_arrives_after_the_end_is_claimed_is_refused_as_not_live()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // The host's end-live, holding the party's links, not yet committed.
        await using var host = NewContext();
        await using var hostTx = await host.Database.BeginTransactionAsync();
        Assert.Equal(PartyMutationOutcome.Ok,
            (await Parties(host).TransitionAsync(_ownerId, _partyId, PartyLifecycleAction.EndLive, 1)).Outcome);

        await using var regia = NewContext();
        var show = Live(regia).ExecuteAsync(
            _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, 0, PartyGuestbookLiveRights.Owner);
        await WaitForALockWaiterAsync();
        Assert.False(show.IsCompleted, "show_on_tv must wait for the party's transition");

        await hostTx.CommitAsync();

        // Read after the lock: the party is over.
        var refused = await show;
        Assert.Equal(PartyGuestbookLiveError.PartyNotLive, refused.Error);
        Assert.False(refused.Control!.TvActive);
        Assert.False(refused.Control.PartyLive);
        Assert.DoesNotContain(PartyGuestbookLiveCommands.ShowOnTv, refused.Control.AvailableCommands);
        await AssertOverAndOffAsync();
        Assert.Equal(0, await ControlVersionAsync()); // nothing on the TV, nothing to bump
    }

    [SkippableFact]
    public async Task Released_together_again_and_again_an_ended_party_never_keeps_the_book_on_the_tv()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        for (var round = 0; round < 8; round++)
        {
            // Back to a live party with the book off the screen.
            await using (var reset = NewContext())
            {
                await reset.Parties.Where(p => p.Id == _partyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PartyStatuses.Live));
                await reset.PartyAlbumLinks.Where(l => l.Id == _linkId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.GuestbookTvActive, false));
            }
            var partyVersion = await PartyVersionAsync();
            var bookVersion = await ControlVersionAsync();

            await using var host = NewContext();
            await using var regia = NewContext();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var end = Task.Run(async () =>
            {
                await gate.Task;
                return await Parties(host).TransitionAsync(
                    _ownerId, _partyId, PartyLifecycleAction.EndLive, partyVersion);
            });
            var show = Task.Run(async () =>
            {
                await gate.Task;
                return await Live(regia).ExecuteAsync(
                    _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, bookVersion,
                    PartyGuestbookLiveRights.Owner);
            });
            gate.SetResult();
            var (ended, shown) = (await end, await show);

            Assert.Equal(PartyMutationOutcome.Ok, ended.Outcome);
            // Either the book went up first and the end took it down, or the
            // end came first and the book was refused — never on after the party.
            Assert.True(shown.Error is null || shown.Error == PartyGuestbookLiveError.PartyNotLive,
                $"round {round}: {shown.Error}");
            await AssertOverAndOffAsync();
        }
    }

    // --- helpers -----------------------------------------------------------

    private async Task AssertOverAndOffAsync()
    {
        await using var db = NewContext();
        var status = await db.Parties.AsNoTracking().Where(p => p.Id == _partyId).Select(p => p.Status).SingleAsync();
        Assert.NotEqual(PartyStatuses.Live, status);
        Assert.False(await TvActiveAsync(), "an ended party keeps the guest book on the television");
        Assert.NotEqual(TvPartyPresentations.Guestbook, (await Projection(db).ProjectAsync(_linkId)).Presentation);
    }

    private async Task<bool> TvActiveAsync()
    {
        await using var db = NewContext();
        return await db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.Id == _linkId).Select(l => l.GuestbookTvActive).SingleAsync();
    }

    private async Task<int> ControlVersionAsync()
    {
        await using var db = NewContext();
        return await db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.Id == _linkId).Select(l => l.GuestbookControlVersion).SingleAsync();
    }

    private async Task<int> PartyVersionAsync()
    {
        await using var db = NewContext();
        return await db.Parties.AsNoTracking().Where(p => p.Id == _partyId).Select(p => p.Version).SingleAsync();
    }

    private static PartyService Parties(AppDbContext db) =>
        new(db, TimeProvider.System, new PartyStateEraser(db), null!);

    private static PartyLinkService Links(AppDbContext db) =>
        new(db, TimeProvider.System, Parties(db), new FixedPartyCapabilityPolicy(), new ConfigurationBuilder().Build());

    private static TvPartyPresentationService Projection(AppDbContext db) =>
        new(db, TimeProvider.System, Links(db));

    private static PartyGuestbookLiveService Live(AppDbContext db) =>
        new(db, TimeProvider.System, Projection(db), NullLogger<PartyGuestbookLiveService>.Instance);

    private AppDbContext NewContext() => new(_dbOptions!);

    /// Waits until some backend is blocked on a lock — the second claim.
    private async Task WaitForALockWaiterAsync()
    {
        await using var probe = new NpgsqlConnection(_fixture.ConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()",
                probe);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("the second request never reached the party link's row lock");
    }
}

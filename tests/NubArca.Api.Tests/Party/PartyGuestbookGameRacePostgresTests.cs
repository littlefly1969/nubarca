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
/// The game and the guest book asking for the party's television at the same
/// moment, on real PostgreSQL.
///
/// SQLite serialises every writer on one file lock, so it would show only that
/// two writers do not overlap — not that the second one READS what the first
/// left. Here each racer has its own connection, and the order is decided by
/// the party link's row lock and READ COMMITTED re-evaluation, as in
/// production. The property: one winner, the loser refused with the state the
/// winner committed, and never both on the screen.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyGuestbookGameRacePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();

    public PartyGuestbookGameRacePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        await using (var db = NewContext())
        {
            db.Users.Add(new User
            {
                Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com",
                DisplayName = "Owner", CreatedAt = now,
            });
            db.Albums.Add(new Album
            {
                Id = _albumId, OwnerUserId = _ownerId, Name = "Festa", CreatedAt = now, UpdatedAt = now,
            });
            PartySeed.Party(db, _partyId, _ownerId, _albumId, status: PartyStatuses.Live);
            db.PartyAlbumLinks.Add(new PartyAlbumLink
            {
                Id = _linkId, PartyId = _partyId, OwnerUserId = _ownerId, AlbumId = _albumId,
                TokenHash = new string('a', 64), Enabled = true, GameEnabled = true,
                GuestbookEnabled = true, CreatedAt = now, UpdatedAt = now,
            });
            // Enough challenges for every round of the repeated race.
            for (var i = 0; i < 12; i++)
                db.PartyChallenges.Add(new PartyChallenge
                {
                    Id = Guid.NewGuid(), AlbumId = _albumId, Title = $"Sfida {i}", Body = "Descrizione",
                    Kind = PartyChallengeKinds.Dare, IsEnabled = true, SortOrder = i,
                    CreatedAt = now, UpdatedAt = now,
                });
            // One visible memory, so the guest book has something to show.
            var blobId = Guid.NewGuid();
            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId, Sha256 = new string('c', 64), SizeBytes = 1,
                StorageKey = "objects/cc/cc/test", ReferenceCount = 1, CreatedAt = now,
            });
            db.PartyGuestbookEntries.Add(new PartyGuestbookEntry
            {
                Id = Guid.NewGuid(), PartyId = _partyId, OwnerUserId = _ownerId,
                PartyAlbumLinkId = _linkId, AuthorDisplayName = "Anna", Body = "Auguri",
                BlobObjectId = blobId, PhotoWidth = 1200, PhotoHeight = 800,
                Status = PartyMessageStatuses.Visible, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // A game that has played one challenge and handed the room back: the
        // screen is free, and the next challenge would take it.
        await using (var db = NewContext())
        {
            var game = Game(db);
            foreach (var command in new[]
            {
                PartyGameCommands.Start, PartyGameCommands.StartChallenge, PartyGameCommands.OpenVoting,
                PartyGameCommands.CloseVoting, PartyGameCommands.RevealResult, PartyGameCommands.ReturnToParty,
            })
            {
                var result = await game.ExecuteAsync(_ownerId, _albumId, command, await GameVersionAsync(db));
                Assert.Null(result.Error);
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_guest_book_that_commits_first_turns_the_next_challenge_away()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var gameVersion = await GameVersionAsync();

        // The regia's "show on TV", holding the party link's row.
        await using var regia = NewContext();
        await using var regiaTx = await regia.Database.BeginTransactionAsync();
        var shown = await Live(regia).ExecuteAsync(
            _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, 0, PartyGuestbookLiveRights.Owner);
        Assert.Null(shown.Error);

        // The next challenge has already read an intermission (reads do not
        // wait) and now blocks on the same row.
        await using var host = NewContext();
        var next = Game(host).ExecuteAsync(_ownerId, _albumId, PartyGameCommands.NextChallenge, gameVersion);
        await WaitForALockWaiterAsync();
        Assert.False(next.IsCompleted, "the game's claim must wait for the guest book's transaction");

        await regiaTx.CommitAsync();

        // Re-evaluated against the row the guest book left: refused, nothing
        // written, and the refusal already says why.
        var refused = await next;
        Assert.Equal(PartyGameCommandError.GuestbookActive, refused.Error);
        Assert.True(refused.Snapshot!.GuestbookOnTv);
        Assert.Equal(PartyGamePhases.Intermission, refused.Snapshot.Phase);
        Assert.Equal(gameVersion, refused.Snapshot.Version);
        Assert.DoesNotContain(PartyGameCommands.NextChallenge, refused.Snapshot.AvailableCommands);
        await AssertOneHolderAsync(expectGuestbook: true);
    }

    [SkippableFact]
    public async Task A_challenge_that_commits_first_turns_the_guest_book_away()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var gameVersion = await GameVersionAsync();

        // The next challenge, holding the party link's row.
        await using var host = NewContext();
        await using var hostTx = await host.Database.BeginTransactionAsync();
        var started = await Game(host).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.NextChallenge, gameVersion);
        Assert.Null(started.Error);

        await using var regia = NewContext();
        var show = Live(regia).ExecuteAsync(
            _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, 0, PartyGuestbookLiveRights.Owner);
        await WaitForALockWaiterAsync();
        Assert.False(show.IsCompleted, "the guest book's claim must wait for the game's transaction");

        await hostTx.CommitAsync();

        // The guest book read the game AFTER the lock: it sees the challenge
        // on the screen and answers with the regia's current picture.
        var refused = await show;
        Assert.Equal(PartyGuestbookLiveError.GameActive, refused.Error);
        Assert.False(refused.Control!.TvActive);
        Assert.Equal(TvPartyPresentations.Game, refused.Control.TvPresentation);
        Assert.Equal("game_active", refused.Control.TvUnavailableReason);
        Assert.DoesNotContain(PartyGuestbookLiveCommands.ShowOnTv, refused.Control.AvailableCommands);
        await AssertOneHolderAsync(expectGuestbook: false);
    }

    [SkippableFact]
    public async Task Released_together_again_and_again_there_is_always_exactly_one_winner()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var winners = new List<string>();
        for (var round = 0; round < 6; round++)
        {
            var gameVersion = await GameVersionAsync();
            var bookVersion = await BookVersionAsync();
            await using var host = NewContext();
            await using var regia = NewContext();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = Task.Run(async () =>
            {
                await gate.Task;
                return await Game(host).ExecuteAsync(
                    _ownerId, _albumId, PartyGameCommands.NextChallenge, gameVersion);
            });
            var show = Task.Run(async () =>
            {
                await gate.Task;
                return await Live(regia).ExecuteAsync(
                    _ownerId, _albumId, PartyGuestbookLiveCommands.ShowOnTv, bookVersion,
                    PartyGuestbookLiveRights.Owner);
            });
            gate.SetResult();
            var (game, book) = (await next, await show);

            // Exactly one of the two, and the other told who has the screen.
            Assert.True(game.Error is null ^ book.Error is null,
                $"round {round}: game={game.Error?.ToString() ?? "ok"} book={book.Error?.ToString() ?? "ok"}");
            if (game.Error is null)
            {
                Assert.Equal(PartyGuestbookLiveError.GameActive, book.Error);
                Assert.Equal(TvPartyPresentations.Game, book.Control!.TvPresentation);
                await AssertOneHolderAsync(expectGuestbook: false);
                winners.Add("game");
                await PlayBackToIntermissionAsync();
            }
            else
            {
                Assert.Equal(PartyGameCommandError.GuestbookActive, game.Error);
                Assert.True(game.Snapshot!.GuestbookOnTv);
                await AssertOneHolderAsync(expectGuestbook: true);
                winners.Add("guestbook");
                await using var db = NewContext();
                Assert.Null((await Live(db).ExecuteAsync(
                    _ownerId, _albumId, PartyGuestbookLiveCommands.ReturnToSlideshow,
                    await BookVersionAsync(), PartyGuestbookLiveRights.Owner)).Error);
            }
        }
        Assert.Equal(6, winners.Count);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// The invariant itself, read from what is committed: the guest book's
    /// request and a screen-holding game are never both true, and the
    /// projection names the one that won.
    /// </summary>
    private async Task AssertOneHolderAsync(bool expectGuestbook)
    {
        await using var db = NewContext();
        var link = await db.PartyAlbumLinks.AsNoTracking().SingleAsync(l => l.Id == _linkId);
        var session = await db.PartyGameSessions.AsNoTracking().SingleAsync(s => s.PartyAlbumLinkId == _linkId);
        var gameHolds = PartyGamePhases.HoldsTheScreen(session.Phase);
        Assert.False(link.GuestbookTvActive && gameHolds, "the guest book and the game both hold the screen");
        Assert.Equal(expectGuestbook, link.GuestbookTvActive);
        Assert.Equal(!expectGuestbook, gameHolds);
        var presentation = (await Projection(db).ProjectAsync(_linkId)).Presentation;
        Assert.Equal(expectGuestbook ? TvPartyPresentations.Guestbook : TvPartyPresentations.Game, presentation);
    }

    private async Task PlayBackToIntermissionAsync()
    {
        await using var db = NewContext();
        foreach (var command in new[]
        {
            PartyGameCommands.OpenVoting, PartyGameCommands.CloseVoting,
            PartyGameCommands.RevealResult, PartyGameCommands.ReturnToParty,
        })
            Assert.Null((await Game(db).ExecuteAsync(_ownerId, _albumId, command, await GameVersionAsync(db))).Error);
    }

    private async Task<int> GameVersionAsync(AppDbContext? db = null)
    {
        if (db is not null) return await ReadGameVersionAsync(db);
        await using var fresh = NewContext();
        return await ReadGameVersionAsync(fresh);
    }

    private async Task<int> ReadGameVersionAsync(AppDbContext db) =>
        await db.PartyGameSessions.AsNoTracking()
            .Where(s => s.PartyAlbumLinkId == _linkId)
            .Select(s => (int?)s.Version)
            .FirstOrDefaultAsync() ?? 0;

    private async Task<int> BookVersionAsync()
    {
        await using var db = NewContext();
        return await db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.Id == _linkId).Select(l => l.GuestbookControlVersion).SingleAsync();
    }

    // Real services on one context each, as one request's scope would wire them.
    private static PartyLinkService Links(AppDbContext db) =>
        new(db, TimeProvider.System,
            new PartyService(db, TimeProvider.System, new PartyStateEraser(db), null!),
            new FixedPartyCapabilityPolicy(), new ConfigurationBuilder().Build());

    private static TvPartyPresentationService Projection(AppDbContext db) =>
        new(db, TimeProvider.System, Links(db));

    private static PartyGameService Game(AppDbContext db) =>
        new(db, TimeProvider.System, Links(db),
            new PartyParticipantService(
                db, TimeProvider.System, new PartyGuestIdentity(new ConfigurationBuilder().Build())),
            NullLogger<PartyGameService>.Instance);

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
        Assert.Fail("the second command never reached the party link's row lock");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Two owner surfaces, one game, one instant.
///
/// The in-memory <c>expectedVersion</c> check cannot decide these: both callers
/// read the same version and both believe they are current. What elects a winner
/// is the version column being a concurrency token, and the unique index on the
/// party link being the only way a session can be created. These tests use two
/// independent connections so the race is real rather than simulated.
/// </summary>
public sealed class PartyGameConcurrencyTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"nubarca-party-game-{Guid.NewGuid():N}.db");

    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        // Real rows for the two tables the session references: the service's own
        // connections keep foreign keys ON, so a stand-in id would fail the
        // insert and look exactly like the conflict under test.
        db.Albums.Add(new Album
        {
            Id = _albumId,
            OwnerUserId = _ownerId,
            Name = "Festa",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId,
            OwnerUserId = _ownerId,
            AlbumId = _albumId,
            TokenHash = new string('a', 64),
            Enabled = true,
            GameEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        for (var i = 0; i < 3; i++)
            db.PartyChallenges.Add(new PartyChallenge
            {
                Id = Guid.NewGuid(),
                AlbumId = _albumId,
                Title = $"Sfida {i}",
                Body = "Descrizione",
                Kind = PartyChallengeKinds.Dare,
                IsEnabled = true,
                SortOrder = i,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Two_simultaneous_starts_create_exactly_one_game()
    {
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            CommandAfterStartAsync(firstDb, PartyGameCommands.Start, 0, start.Task),
            CommandAfterStartAsync(secondDb, PartyGameCommands.Start, 0, start.Task, start));

        Assert.Single(results, x => x.Error is null);
        Assert.Single(results, x => x.Error == PartyGameCommandError.VersionConflict);

        await using var verify = CreateContext();
        var session = await verify.PartyGameSessions.SingleAsync();
        Assert.Equal(PartyGamePhases.ChallengeReveal, session.Phase);
        Assert.Equal(1, session.Version);
        Assert.Equal(1, await verify.PartyGameRounds.CountAsync());

        // The loser was handed the winner's state, not an empty error.
        var loser = results.Single(x => x.Error is not null);
        Assert.Equal(PartyGamePhases.ChallengeReveal, loser.Snapshot!.Phase);
        Assert.Equal(1, loser.Snapshot.Version);
    }

    [Fact]
    public async Task Two_simultaneous_advances_move_the_game_exactly_one_phase()
    {
        await using (var starter = CreateContext())
            await Service(starter).ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            CommandAfterStartAsync(firstDb, PartyGameCommands.StartChallenge, 1, start.Task),
            CommandAfterStartAsync(secondDb, PartyGameCommands.StartChallenge, 1, start.Task, start));

        Assert.Single(results, x => x.Error is null);
        await using var verify = CreateContext();
        var session = await verify.PartyGameSessions.SingleAsync();
        Assert.Equal(PartyGamePhases.ChallengeActive, session.Phase);
        Assert.Equal(2, session.Version);
        Assert.Equal(1, session.CurrentRoundNumber);
    }

    [Fact]
    public async Task A_simultaneous_skip_and_next_start_only_one_round()
    {
        await using (var starter = CreateContext())
        {
            var service = Service(starter);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.StartChallenge, 1);
        }

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            CommandAfterStartAsync(firstDb, PartyGameCommands.SkipChallenge, 2, start.Task),
            CommandAfterStartAsync(secondDb, PartyGameCommands.OpenVoting, 2, start.Task, start));

        Assert.Single(results, x => x.Error is null);
        await using var verify = CreateContext();
        Assert.Equal(3, (await verify.PartyGameSessions.SingleAsync()).Version);

        // Whichever won, the deck advanced by at most one activity.
        Assert.InRange(await verify.PartyGameRounds.CountAsync(), 1, 2);
        Assert.Equal(await verify.PartyGameRounds.CountAsync(),
            await verify.PartyGameRounds.Select(x => x.PartyChallengeId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Two_taps_from_one_guest_leave_exactly_one_answer()
    {
        // Reach a live vote through the real commands, so the row shapes are the
        // ones the runtime actually writes.
        await using (var host = CreateContext())
        {
            var service = Service(host);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.StartChallenge, 1);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.OpenVoting, 2);
        }

        var participantId = Guid.NewGuid();
        Guid roundId;
        await using (var seed = CreateContext())
        {
            await seed.Database.OpenConnectionAsync();
            await seed.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            seed.PartyParticipants.Add(new PartyParticipant
            {
                Id = participantId,
                PartyAlbumLinkId = _linkId,
                TokenHash = new string('b', 64),
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            roundId = (await seed.PartyGameSessions.AsNoTracking().SingleAsync()).CurrentRoundId!.Value;
        }

        var access = new PartyAccess(_ownerId, _albumId, _linkId);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Task.WhenAll(
            VoteAfterStartAsync(firstDb, access, participantId, roundId, PartyGameVoteValues.Yes, start.Task),
            VoteAfterStartAsync(secondDb, access, participantId, roundId, PartyGameVoteValues.No, start.Task, start));

        // The unique index is the authority: one guest, one round, one answer —
        // whichever of the two taps the database elected.
        await using var verify = CreateContext();
        var vote = await verify.PartyGameVotes.SingleAsync();
        Assert.Equal(roundId, vote.PartyGameRoundId);
        Assert.Contains(vote.Value, PartyGameVoteValues.All);
    }

    // --- playing the same party again ------------------------------------

    [Fact]
    public async Task Two_simultaneous_restarts_reset_the_game_exactly_once()
    {
        var finishedVersion = await FinishAsync();

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            CommandAfterStartAsync(firstDb, PartyGameCommands.RestartGame, finishedVersion, start.Task),
            CommandAfterStartAsync(secondDb, PartyGameCommands.RestartGame, finishedVersion, start.Task, start));

        Assert.Single(results, x => x.Error is null);
        Assert.Single(results, x => x.Error == PartyGameCommandError.VersionConflict);

        await using var verify = CreateContext();
        var session = await verify.PartyGameSessions.SingleAsync();
        // One reset, not two. The version moved by exactly one, so the loser's
        // command was refused rather than applied to the winner's fresh lobby.
        Assert.Equal(PartyGamePhases.Lobby, session.Phase);
        Assert.Equal(PartyGameStatuses.Lobby, session.Status);
        Assert.Equal(finishedVersion + 1, session.Version);
        Assert.Null(session.CurrentRoundId);
        Assert.Equal(0, session.CurrentRoundNumber);
        Assert.Empty(await verify.PartyGameRounds.ToListAsync());
        Assert.Empty(await verify.PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_restart_that_loses_the_race_deletes_nothing()
    {
        var finishedVersion = await FinishAsync();

        await using var winnerDb = CreateContext();
        await using var loserDb = CreateContext();

        // The winner is IN FLIGHT: it has taken the session row's write lock and
        // deleted the finished match, and its transaction is still open.
        await using var winnerTx = await winnerDb.Database.BeginTransactionAsync();
        Assert.Null((await Service(winnerDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.RestartGame, finishedVersion)).Error);

        // The loser starts from the same snapshot — the stale read the boundary
        // exists for — and blocks on that row rather than deleting behind it.
        var loserTask = Task.Run(() => Service(loserDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.RestartGame, finishedVersion));
        await WaitUntilBlockedAsync(loserTask);

        await winnerTx.CommitAsync();
        var loser = await loserTask;

        Assert.Equal(PartyGameCommandError.VersionConflict, loser.Error);
        // And it is handed the lobby the winner created, not an empty error.
        Assert.Equal(PartyGamePhases.Lobby, loser.Snapshot!.Phase);
        Assert.Equal(finishedVersion + 1, loser.Snapshot.Version);

        await using var verify = CreateContext();
        Assert.Equal(finishedVersion + 1, (await verify.PartyGameSessions.SingleAsync()).Version);
    }

    [Fact]
    public async Task A_restart_never_hands_a_spent_version_back_to_the_previous_game()
    {
        var finishedVersion = await FinishAsync();
        await using (var restartDb = CreateContext())
            Assert.Null((await Service(restartDb).ExecuteAsync(
                _ownerId, _albumId, PartyGameCommands.RestartGame, finishedVersion)).Error);

        // The whole point of keeping the session row. A `start` quoting the
        // version the previous game ended on must stay refused for ever, and
        // version 0 — the number a game that never existed would quote — must
        // never become current again either.
        await using (var staleDb = CreateContext())
            Assert.Equal(PartyGameCommandError.VersionConflict, (await Service(staleDb)
                .ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, finishedVersion)).Error);
        await using (var coldDb = CreateContext())
            Assert.Equal(PartyGameCommandError.VersionConflict, (await Service(coldDb)
                .ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0)).Error);

        // The new game starts from the version the restart left behind.
        await using var db = CreateContext();
        var started = await Service(db).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.Start, finishedVersion + 1);
        Assert.Null(started.Error);
        Assert.Equal(PartyGamePhases.ChallengeReveal, started.Snapshot!.Phase);
        Assert.Equal(finishedVersion + 2, started.Snapshot.Version);
    }

    /// Play one activity to a recorded vote and then end the game, so a restart
    /// has both a round and a vote to discard. Returns the finished version.
    private async Task<int> FinishAsync()
    {
        var (participantId, roundId) = await OpenVotingAsync();
        await using (var voteDb = CreateContext())
            await Service(voteDb).VoteAsync(
                new PartyAccess(_ownerId, _albumId, _linkId), participantId, roundId,
                PartyGameVoteValues.Yes);

        await using var host = CreateContext();
        var service = Service(host);
        var version = 3;
        foreach (var command in new[]
            { PartyGameCommands.CloseVoting, PartyGameCommands.RevealResult, PartyGameCommands.Finish })
            version = (await service.ExecuteAsync(_ownerId, _albumId, command, version)).Snapshot!.Version;
        return version;
    }

    private static async Task VoteAfterStartAsync(
        AppDbContext db, PartyAccess access, Guid participantId, Guid roundId, string value,
        Task start, TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        try
        {
            await Service(db).VoteAsync(access, participantId, roundId, value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // SQLite serialises writers with a whole-database lock. A loser that
            // surfaces as "database is locked" still wrote nothing, which is the
            // property under test.
        }
    }

    // --- the vote/close boundary ------------------------------------------
    //
    // These are the tests the boundary exists for, and they are races rather
    // than sequences: in each one an operation is genuinely in flight, holding
    // the session row, while the other blocks on it. The interleaving is forced
    // by holding a real transaction open, so the outcome is deterministic while
    // the contention is not simulated.

    [Fact]
    public async Task A_vote_that_arrives_while_the_close_is_committing_is_refused_and_writes_nothing()
    {
        var (participantId, roundId) = await OpenVotingAsync();
        var access = new PartyAccess(_ownerId, _albumId, _linkId);

        await using var closeDb = CreateContext();
        await using var voteDb = CreateContext();

        // The close is IN FLIGHT: its update to the session row is written and
        // its transaction is still open, so the row's write lock is held.
        await using var closeTx = await closeDb.Database.BeginTransactionAsync();
        var closed = await Service(closeDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.CloseVoting, 3);
        Assert.Null(closed.Error);

        // The vote starts from a snapshot that still says voting_open — the
        // stale read the whole fix is about — and blocks on the session row.
        var voteTask = Task.Run(() =>
            Service(voteDb).VoteAsync(access, participantId, roundId, PartyGameVoteValues.Yes));
        await WaitUntilBlockedAsync(voteTask);

        await closeTx.CommitAsync();
        var vote = await voteTask;

        // It re-evaluated against the row the close left behind.
        Assert.Equal(PartyGameVoteError.VotingClosed, vote.Error);
        await using var verify = CreateContext();
        Assert.Empty(await verify.PartyGameVotes.ToListAsync());
        Assert.Equal(PartyGamePhases.VotingClosed,
            (await verify.PartyGameSessions.SingleAsync()).Phase);
    }

    [Fact]
    public async Task A_vote_that_is_committing_holds_the_close_and_then_both_stand()
    {
        var (participantId, roundId) = await OpenVotingAsync();
        var access = new PartyAccess(_ownerId, _albumId, _linkId);

        await using var voteDb = CreateContext();
        await using var closeDb = CreateContext();

        // The other ordering, with the same shape: the VOTE is in flight and
        // holding the row, and the close blocks on it.
        await using var voteTx = await voteDb.Database.BeginTransactionAsync();
        var vote = await Service(voteDb).VoteAsync(
            access, participantId, roundId, PartyGameVoteValues.Yes);
        Assert.Null(vote.Error);

        var closeTask = Task.Run(() => Service(closeDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.CloseVoting, 3));
        await WaitUntilBlockedAsync(closeTask);

        await voteTx.CommitAsync();
        var closed = await closeTask;

        // The close still succeeds, because the vote did not touch Version —
        // if it had, every vote in a round would defeat the host's next command.
        Assert.Null(closed.Error);
        Assert.Equal(PartyGamePhases.VotingClosed, closed.Snapshot!.Phase);
        Assert.Equal(1, closed.Snapshot.Voting!.Yes);

        await using var verify = CreateContext();
        var stored = await verify.PartyGameVotes.SingleAsync();
        Assert.Equal(roundId, stored.PartyGameRoundId);
        Assert.Equal(4, (await verify.PartyGameSessions.SingleAsync()).Version);
    }

    [Fact]
    public async Task A_vote_never_moves_the_owner_command_version()
    {
        var (participantId, roundId) = await OpenVotingAsync();
        var access = new PartyAccess(_ownerId, _albumId, _linkId);

        await using (var voteDb = CreateContext())
        {
            var service = Service(voteDb);
            await service.VoteAsync(access, participantId, roundId, PartyGameVoteValues.Yes);
            await service.VoteAsync(access, participantId, roundId, PartyGameVoteValues.No);
            await service.VoteAsync(access, participantId, roundId, PartyGameVoteValues.No);
        }

        await using var verify = CreateContext();
        // Three taps, one answer, and a command token the host can still quote.
        Assert.Equal(3, (await verify.PartyGameSessions.SingleAsync()).Version);
        Assert.Single(await verify.PartyGameVotes.ToListAsync());

        await using var closeDb = CreateContext();
        Assert.Null((await Service(closeDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.CloseVoting, 3)).Error);
    }

    /// Reach voting_open through the real commands, with one seeded guest.
    private async Task<(Guid ParticipantId, Guid RoundId)> OpenVotingAsync()
    {
        await using (var host = CreateContext())
        {
            var service = Service(host);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.StartChallenge, 1);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.OpenVoting, 2);
        }

        var participantId = Guid.NewGuid();
        await using var seed = CreateContext();
        await seed.Database.OpenConnectionAsync();
        await seed.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        seed.PartyParticipants.Add(new PartyParticipant
        {
            Id = participantId,
            PartyAlbumLinkId = _linkId,
            TokenHash = new string('c', 64),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
        var roundId = (await seed.PartyGameSessions.AsNoTracking().SingleAsync()).CurrentRoundId!.Value;
        return (participantId, roundId);
    }

    /// <summary>
    /// Give the racing operation time to actually reach the lock it is going to
    /// block on. It must NOT have completed — a task that finished before the
    /// other side committed would mean the two never contended, and the test
    /// would be a sequence wearing a race's clothes.
    /// </summary>
    private static async Task WaitUntilBlockedAsync(Task task)
    {
        var settled = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(task, settled);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=30;Pooling=False")
            .Options;
        return new AppDbContext(options);
    }

    private static PartyGameService Service(AppDbContext db) =>
        new(db, TimeProvider.System,
            new PartyLinkService(db, TimeProvider.System, new ConfigurationBuilder().Build()),
            NullLogger<PartyGameService>.Instance);

    private async Task<PartyGameCommandResult> CommandAfterStartAsync(
        AppDbContext db, string command, int expectedVersion, Task start,
        TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        try
        {
            return await Service(db).ExecuteAsync(_ownerId, _albumId, command, expectedVersion);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // SQLite serialises writers with a whole-database lock, so a loser
            // can surface as "database is locked" rather than as the row-level
            // conflict PostgreSQL reports. Either way it did not write, which is
            // the property under test.
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict);
        }
    }
}

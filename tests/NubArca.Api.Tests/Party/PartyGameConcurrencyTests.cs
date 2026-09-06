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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Two restarts, one game, one instant — on real PostgreSQL.
///
/// The SQLite suite already races these, but it cannot prove the property that
/// matters here. SQLite serialises writers with a whole-database lock, so a
/// loser surfaces as "database is locked" and the test has to accept that as
/// evidence it wrote nothing. PostgreSQL takes a ROW lock instead, which is what
/// production does: both callers genuinely reach the session row, the conditional
/// update's predicate is genuinely re-evaluated under the winner's commit, and
/// the loser is refused for the right reason rather than because the whole file
/// was busy.
///
/// A restart is also the one owner command that DELETES. A losing restart that
/// deleted before checking would destroy the fresh lobby the winner had just
/// created — and no amount of SQLite serialisation would show that, because
/// SQLite never lets the two overlap in the first place.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyGameRestartPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _participantId = Guid.NewGuid();

    public PartyGameRestartPostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId,
            Email = $"owner-{_ownerId:N}@example.com",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        db.Albums.Add(new Album
        {
            Id = _albumId, OwnerUserId = _ownerId, Name = "Festa",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // The party the link is a capability OF: the root, and the `main`
        // media source that resolves back to this album.
        PartySeed.Party(db, _partyId, _ownerId, _albumId);
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId,
            PartyId = _partyId, OwnerUserId = _ownerId, AlbumId = _albumId,
            TokenHash = new string('a', 64), Enabled = true, GameEnabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = _participantId, PartyAlbumLinkId = _linkId, TokenHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });
        for (var i = 0; i < 3; i++)
            db.PartyChallenges.Add(new PartyChallenge
            {
                Id = Guid.NewGuid(), AlbumId = _albumId, Title = $"Sfida {i}", Body = "Descrizione",
                Kind = PartyChallengeKinds.Dare, IsEnabled = true, SortOrder = i,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Two_simultaneous_restarts_reset_the_game_exactly_once()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var finished = await FinishAsync();

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            RestartAfterAsync(firstDb, finished, gate.Task),
            RestartAfterAsync(secondDb, finished, gate.Task, gate));

        // Exactly one winner, and the loser is told the truth rather than
        // getting a bare error.
        Assert.Single(results, x => x.Error is null);
        var loser = Assert.Single(results, x => x.Error == PartyGameCommandError.VersionConflict);
        Assert.Equal(PartyGamePhases.Lobby, loser.Snapshot!.Phase);
        Assert.Equal(finished + 1, loser.Snapshot.Version);

        await using var verify = NewContext();
        var session = await verify.PartyGameSessions.SingleAsync();
        // ONE reset. The version moved by exactly one, so the second command was
        // refused rather than applied on top of the first one's fresh lobby.
        Assert.Equal(PartyGamePhases.Lobby, session.Phase);
        Assert.Equal(PartyGameStatuses.Lobby, session.Status);
        Assert.Equal(finished + 1, session.Version);
        Assert.Null(session.CurrentRoundId);
        Assert.Equal(0, session.CurrentRoundNumber);
        Assert.Null(session.StartedAt);
        Assert.Null(session.FinishedAt);
        // And no partial state: the finished match's rows are gone, all of them.
        Assert.Empty(await verify.PartyGameRounds.ToListAsync());
        Assert.Empty(await verify.PartyGameVotes.ToListAsync());
    }

    [SkippableFact]
    public async Task A_restart_that_loses_the_race_deletes_nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var finished = await FinishAsync();

        await using var winnerDb = NewContext();
        await using var loserDb = NewContext();

        // The winner is genuinely IN FLIGHT: it holds the session row's write
        // lock and its transaction is still open. On PostgreSQL this is a row
        // lock, so the loser reaches the same row rather than the whole database
        // being closed to it.
        await using var winnerTx = await winnerDb.Database.BeginTransactionAsync();
        Assert.Null((await Service(winnerDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.RestartGame, finished)).Error);

        var loserTask = Task.Run(() => Service(loserDb).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.RestartGame, finished));
        // It must genuinely BLOCK. A task that completed here would mean the two
        // never contended and the test would be a sequence in a race's clothes.
        var settled = await Task.WhenAny(loserTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.NotSame(loserTask, settled);

        await winnerTx.CommitAsync();
        var loser = await loserTask;

        // Re-evaluated against the row the winner left behind: refused, and it
        // did not delete the lobby the winner had just created.
        Assert.Equal(PartyGameCommandError.VersionConflict, loser.Error);
        await using var verify = NewContext();
        var session = await verify.PartyGameSessions.SingleAsync();
        Assert.Equal(PartyGamePhases.Lobby, session.Phase);
        Assert.Equal(finished + 1, session.Version);
    }

    [SkippableFact]
    public async Task A_spent_version_never_becomes_quotable_again()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var finished = await FinishAsync();

        await using (var db = NewContext())
            Assert.Null((await Service(db).ExecuteAsync(
                _ownerId, _albumId, PartyGameCommands.RestartGame, finished)).Error);

        // The whole reason the session row survives a restart. A command written
        // during the game that just ended quotes a number the server has spent —
        // and so does the 0 a game that never existed would quote.
        foreach (var command in new[]
            { PartyGameCommands.Start, PartyGameCommands.Finish, PartyGameCommands.RestartGame })
        {
            await using var stale = NewContext();
            Assert.Equal(PartyGameCommandError.VersionConflict, (await Service(stale)
                .ExecuteAsync(_ownerId, _albumId, command, finished)).Error);
        }
        await using (var cold = NewContext())
            Assert.Equal(PartyGameCommandError.VersionConflict, (await Service(cold)
                .ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0)).Error);

        // And the new game starts from where the restart left the counter.
        await using var db2 = NewContext();
        var started = await Service(db2).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.Start, finished + 1);
        Assert.Null(started.Error);
        Assert.Equal(PartyGamePhases.ChallengeReveal, started.Snapshot!.Phase);
        Assert.Equal(finished + 2, started.Snapshot.Version);
        Assert.Equal(1, started.Snapshot.RoundNumber);
    }

    // --- helpers -----------------------------------------------------------

    /// Play one activity to a recorded vote and end the game, so a restart has
    /// both a round and a vote to discard. Returns the finished version.
    private async Task<int> FinishAsync()
    {
        await using (var host = NewContext())
        {
            var service = Service(host);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.StartChallenge, 1);
            await service.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.OpenVoting, 2);
        }

        Guid roundId;
        await using (var read = NewContext())
            roundId = (await read.PartyGameSessions.AsNoTracking().SingleAsync()).CurrentRoundId!.Value;

        await using (var voteDb = NewContext())
            await Service(voteDb).VoteAsync(
                new PartyAccess(_partyId, _ownerId, _albumId, _linkId, PartyTestCapabilities.All, PartyTestExperience.Live), _participantId, roundId,
                PartyGameVoteValues.Yes);

        await using var finisher = NewContext();
        var finish = Service(finisher);
        var version = 3;
        foreach (var command in new[]
            { PartyGameCommands.CloseVoting, PartyGameCommands.RevealResult, PartyGameCommands.Finish })
            version = (await finish.ExecuteAsync(_ownerId, _albumId, command, version)).Snapshot!.Version;

        await using var check = NewContext();
        Assert.Single(await check.PartyGameVotes.ToListAsync());
        Assert.Single(await check.PartyGameRounds.ToListAsync());
        return version;
    }

    private async Task<PartyGameCommandResult> RestartAfterAsync(
        AppDbContext db, int expectedVersion, Task gate, TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await gate;
        return await Service(db).ExecuteAsync(
            _ownerId, _albumId, PartyGameCommands.RestartGame, expectedVersion);
    }

    private AppDbContext NewContext() => new(_dbOptions!);

    private static PartyGameService Service(AppDbContext db) =>
        new(db, TimeProvider.System,
            new PartyLinkService(
                db, TimeProvider.System, new PartyService(db, TimeProvider.System, new PartyStateEraser(db), null!),
                new FixedPartyCapabilityPolicy(), new ConfigurationBuilder().Build()),
            NullLogger<PartyGameService>.Instance);
}

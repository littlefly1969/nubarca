using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Tv;
using Xunit;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// Deleting an album that played a Party Game — on real PostgreSQL.
///
/// This test cannot be replaced by the SQLite one, because the bug it guards is
/// a FOREIGN KEY ORDERING bug and SQLite does not enforce the constraints the
/// same way. The application's SQLite test host runs with foreign keys on, but
/// the restricting edges this delete has to respect are declared for a real
/// relational database and are worth proving against one: a vote names a
/// PARTICIPANT, a round names a CHALLENGE, and the session names both the LINK
/// and the ALBUM. Get the order wrong on PostgreSQL and it raises 23503.
///
/// It also proves the delete is ONE unit of work. Half a delete is worse than a
/// failed one here, because the half that lands first moves a television.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class AlbumDeletePartyGamePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();
    private readonly Guid _participantId = Guid.NewGuid();
    private readonly Guid _tvSessionId = Guid.NewGuid();

    public AlbumDeletePartyGamePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
            Id = _albumId, OwnerUserId = _ownerId, Name = "Festa",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId, OwnerUserId = _ownerId, AlbumId = _albumId,
            TokenHash = new string('a', 64), Enabled = true, GameEnabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = _participantId, PartyAlbumLinkId = _linkId, TokenHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });
        for (var i = 0; i < 2; i++)
            db.PartyChallenges.Add(new PartyChallenge
            {
                Id = Guid.NewGuid(), AlbumId = _albumId, Title = $"Sfida {i}", Body = "Descrizione",
                Kind = PartyChallengeKinds.Dare, IsEnabled = true, SortOrder = i,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        // A television pointed at this party — the restricting edge added when
        // display assignments arrived.
        db.TvSessions.Add(new TvSession
        {
            Id = _tvSessionId, OwnerUserId = _ownerId, SessionTokenHash = new string('c', 64),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            DisplayAssignment = TvDisplayAssignments.Party,
            AssignedPartyAlbumLinkId = _linkId,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task An_album_that_played_a_game_deletes_without_a_foreign_key_violation()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await PlayAndFinishAsync();

        // The state the bug needed: a session naming the link AND the album, a
        // round naming a challenge, a vote naming a participant, and a
        // television naming the link. Four restricting edges, four steps of the
        // delete that used to fail.
        await using (var check = NewContext())
        {
            Assert.Single(await check.PartyGameSessions.ToListAsync());
            Assert.NotEmpty(await check.PartyGameRounds.ToListAsync());
            Assert.Single(await check.PartyGameVotes.ToListAsync());
        }

        await using (var db = NewContext())
            Assert.True(await Service(db).DeleteAsync(_albumId, _ownerId));

        await using var verify = NewContext();
        Assert.Empty(await verify.Albums.ToListAsync());
        Assert.Empty(await verify.PartyGameSessions.ToListAsync());
        Assert.Empty(await verify.PartyGameRounds.ToListAsync());
        Assert.Empty(await verify.PartyGameVotes.ToListAsync());
        Assert.Empty(await verify.PartyAlbumLinks.ToListAsync());
        Assert.Empty(await verify.PartyParticipants.ToListAsync());
        Assert.Empty(await verify.PartyChallenges.ToListAsync());

        // The television survives the party: still paired, still this owner's,
        // returned to the general experience with no dangling pointer.
        var tv = await verify.TvSessions.SingleAsync();
        Assert.Null(tv.RevokedAt);
        Assert.Equal(TvDisplayAssignments.General, tv.DisplayAssignment);
        Assert.Null(tv.AssignedPartyAlbumLinkId);
    }

    [SkippableFact]
    public async Task An_album_deletes_after_the_game_was_restarted()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var finished = await PlayAndFinishAsync();

        await using (var db = NewContext())
            Assert.Null((await Game(db).ExecuteAsync(
                _ownerId, _albumId, PartyGameCommands.RestartGame, finished)).Error);

        // A restart leaves the SESSION alive with no rounds under it, which is a
        // different shape from both "never played" and "played".
        await using (var check = NewContext())
        {
            Assert.Single(await check.PartyGameSessions.ToListAsync());
            Assert.Empty(await check.PartyGameRounds.ToListAsync());
        }

        await using (var db = NewContext())
            Assert.True(await Service(db).DeleteAsync(_albumId, _ownerId));

        await using var verify = NewContext();
        Assert.Empty(await verify.Albums.ToListAsync());
        Assert.Empty(await verify.PartyGameSessions.ToListAsync());
    }

    [SkippableFact]
    public async Task A_failed_delete_commits_nothing_at_all()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await PlayAndFinishAsync();

        // Run the delete inside a transaction the TEST owns and then roll it
        // back. The service participates rather than committing on its own, so
        // rolling back must undo every one of its ExecuteDelete statements —
        // which, before this fix, each committed independently.
        await using (var db = NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.True(await Service(db).DeleteAsync(_albumId, _ownerId));
            await tx.RollbackAsync();
        }

        await using var verify = NewContext();
        // Everything is exactly where it was. In particular the TELEVISION was
        // not left on the general experience by a delete that never happened.
        Assert.Single(await verify.Albums.ToListAsync());
        Assert.Single(await verify.PartyGameSessions.ToListAsync());
        Assert.NotEmpty(await verify.PartyGameRounds.ToListAsync());
        Assert.Single(await verify.PartyGameVotes.ToListAsync());
        Assert.Single(await verify.PartyAlbumLinks.ToListAsync());
        var tv = await verify.TvSessions.SingleAsync();
        Assert.Equal(TvDisplayAssignments.Party, tv.DisplayAssignment);
        Assert.Equal(_linkId, tv.AssignedPartyAlbumLinkId);
    }

    // --- helpers -----------------------------------------------------------

    /// Play one activity to a recorded vote and end the game. Returns the
    /// finished version.
    private async Task<int> PlayAndFinishAsync()
    {
        await using (var host = NewContext())
        {
            var game = Game(host);
            await game.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.Start, 0);
            await game.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.StartChallenge, 1);
            await game.ExecuteAsync(_ownerId, _albumId, PartyGameCommands.OpenVoting, 2);
        }

        Guid roundId;
        await using (var read = NewContext())
            roundId = (await read.PartyGameSessions.AsNoTracking().SingleAsync()).CurrentRoundId!.Value;

        await using (var voteDb = NewContext())
            await Game(voteDb).VoteAsync(
                new PartyAccess(_ownerId, _albumId, _linkId), _participantId, roundId,
                PartyGameVoteValues.Yes);

        await using var finisher = NewContext();
        var service = Game(finisher);
        var version = 3;
        foreach (var command in new[]
            { PartyGameCommands.CloseVoting, PartyGameCommands.RevealResult, PartyGameCommands.Finish })
            version = (await service.ExecuteAsync(_ownerId, _albumId, command, version)).Snapshot!.Version;
        return version;
    }

    private AppDbContext NewContext() => new(_dbOptions!);

    private static AlbumService Service(AppDbContext db) => new(db, TimeProvider.System);

    private static PartyGameService Game(AppDbContext db) =>
        new(db, TimeProvider.System,
            new PartyLinkService(db, TimeProvider.System, new ConfigurationBuilder().Build()),
            NullLogger<PartyGameService>.Instance);
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

public sealed class PartyChallengeConcurrencyTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"nubarca-party-concurrency-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Parallel_vote_claims_allow_only_one_budget_slot()
    {
        var participantId = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            await seed.Database.OpenConnectionAsync();
            await seed.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            seed.PartyParticipants.Add(new PartyParticipant
            {
                Id = participantId,
                PartyAlbumLinkId = Guid.NewGuid(),
                TokenHash = new string('a', 64),
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = new PartyParticipantService(firstDb, TimeProvider.System);
        var second = new PartyParticipantService(secondDb, TimeProvider.System);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claims = await Task.WhenAll(
            ClaimAfterStartAsync(first, participantId, start.Task),
            ClaimAfterStartAsync(second, participantId, start.Task, start));

        Assert.Single(claims, claimed => claimed);
        await using var verify = CreateContext();
        Assert.Equal(1, await verify.PartyParticipants
            .Where(x => x.Id == participantId)
            .Select(x => x.ChallengeVoteCount)
            .SingleAsync());
    }

    [Fact]
    public async Task Parallel_versioned_hold_transitions_complete_only_once()
    {
        var sessionId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            await seed.Database.OpenConnectionAsync();
            await seed.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            seed.PartyChallengeSessions.Add(new PartyChallengeSession
            {
                Id = sessionId,
                PartyAlbumLinkId = linkId,
                Mode = PartyPlaybackModes.ChallengeHold,
                ActiveChallengeId = challengeId,
                Version = 7,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = await Task.WhenAll(
            CompleteAfterStartAsync(firstDb, sessionId, challengeId, start.Task),
            CompleteAfterStartAsync(secondDb, sessionId, challengeId, start.Task, start));

        Assert.Equal(1, transitions.Sum());
        await using var verify = CreateContext();
        var session = await verify.PartyChallengeSessions.SingleAsync(x => x.Id == sessionId);
        Assert.Equal(PartyPlaybackModes.Media, session.Mode);
        Assert.Null(session.ActiveChallengeId);
        Assert.Equal(1, session.CompletedCount);
        Assert.Equal(8, session.Version);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=30;Pooling=False")
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<bool> ClaimAfterStartAsync(
        PartyParticipantService service,
        Guid participantId,
        Task start,
        TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        return await service.TryClaimChallengeVoteAsync(participantId, max: 1);
    }

    private static async Task<int> CompleteAfterStartAsync(
        AppDbContext db,
        Guid sessionId,
        Guid challengeId,
        Task start,
        TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        return await db.PartyChallengeSessions
            .Where(x => x.Id == sessionId && x.Version == 7
                && x.Mode == PartyPlaybackModes.ChallengeHold
                && x.ActiveChallengeId == challengeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Mode, PartyPlaybackModes.Media)
                .SetProperty(x => x.ActiveChallengeId, (Guid?)null)
                .SetProperty(x => x.CompletedCount, x => x.CompletedCount + 1)
                .SetProperty(x => x.Version, x => x.Version + 1));
    }
}

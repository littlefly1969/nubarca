using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The two places a guest's identity and their budget are decided under
/// contention, raced on independent connections.
///
/// <para>The integration host shares one SQLite connection, so simultaneity
/// there tests the harness rather than the product. These open their own, which
/// is the only way "exactly one accepted" means anything.</para>
/// </summary>
public sealed class PartyGuestIdentityRaceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"nubarca-guest-identity-{Guid.NewGuid():N}.db");

    private readonly Guid _linkId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _challengeOne = Guid.NewGuid();
    private readonly Guid _challengeTwo = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        // Real rows for everything a message points at: the racing connections
        // keep foreign keys ON, so a stand-in id would fail the insert and look
        // exactly like the contention under test.
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"{_ownerId:N}@example.com", DisplayName = "Host",
            CreatedAt = DateTime.UtcNow,
        });
        db.Albums.Add(new Album
        {
            Id = _albumId, OwnerUserId = _ownerId, Name = "Festa",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId, OwnerUserId = _ownerId, AlbumId = _albumId,
            TokenHash = new string('a', 64), Enabled = true, UploadEnabled = true,
            MaxMessagesPerParticipant = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // Real challenges too: a legacy guest's votes are seeded as ROWS, not as
        // a bare counter, because the fold now recomputes the vote budget from
        // the rows that actually exist. A counter with no rows behind it is not
        // a state the product can produce — the claim and the insert share one
        // transaction — so seeding one would test a fiction.
        db.PartyChallenges.AddRange(
            NewChallenge(_challengeOne, "Uno"), NewChallenge(_challengeTwo, "Due"));
        await db.SaveChangesAsync();
    }

    private PartyChallenge NewChallenge(Guid id, string title) => new()
    {
        Id = id, AlbumId = _albumId, Title = title, Body = "…",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Two_greetings_racing_for_the_last_slot_leave_exactly_one()
    {
        var guest = await SeedGuestAsync();
        var access = new PartyAccess(
            _ownerId, _albumId, _linkId, MaxMessagesPerParticipant: 1);

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            SubmitAfterStartAsync(firstDb, access, guest, "Primo", start.Task),
            SubmitAfterStartAsync(secondDb, access, guest, "Secondo", start.Task, start));

        // Never two. The claim is a conditional UPDATE under the row's own lock,
        // so the loser sees zero rows affected rather than a stale count.
        Assert.Single(results, r => r?.Error is null);
        Assert.Single(results, r => r?.Error == PartyMessageSubmissionError.LimitReached);

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.PartyMessages.CountAsync());
        Assert.Equal(1, (await verify.PartyParticipants.SingleAsync()).SubmittedMessageCount);
    }

    [Fact]
    public async Task Two_capabilities_arriving_together_do_not_make_two_guests()
    {
        // The same browser opening two party surfaces at once — a phone with the
        // hub and the print studio both loading. The unique (link, key) index is
        // what elects one guest; the loser adopts it instead of becoming a
        // second one with a second allowance.
        var identity = Identity();
        var browserToken = identity.NewBrowserToken();

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            ResolveAfterStartAsync(firstDb, browserToken, start.Task),
            ResolveAfterStartAsync(secondDb, browserToken, start.Task, start));

        await using var verify = CreateContext();
        Assert.Single(await verify.PartyParticipants.ToListAsync());
        var settled = results.Where(r => r is not null).Select(r => r!.ParticipantId).Distinct();
        Assert.Single(settled);
    }

    [Fact]
    public async Task Two_legacy_capabilities_of_one_browser_arriving_together_fold_exactly_once()
    {
        // THE case adoption could not survive. Before, each request rewrote its
        // OWN old row's key to the derived value, so two capabilities of one
        // browser arriving together both tried to claim the same key and the
        // unique index surfaced an ordinary race to a guest as a 500.
        //
        // Now the canonical row is reached by find-or-create and the old rows
        // are folded into it by atomic increment, with the retirement as the
        // exactly-once claim. Both requests complete, and the counters land once.
        var identity = Identity();
        var browserToken = identity.NewBrowserToken();
        var legacyA = FakeToken();
        var legacyB = FakeToken();
        var idA = await SeedLegacyAsync(legacyA, photos: 4, messages: 2);
        var idB = await SeedLegacyAsync(
            legacyB, photoPrints: 1, votedOn: [_challengeOne, _challengeTwo]);

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await Task.WhenAll(
            ResolveAfterStartAsync(firstDb, browserToken, start.Task, legacyToken: legacyA),
            ResolveAfterStartAsync(secondDb, browserToken, start.Task, start, legacyToken: legacyB));

        // BOTH complete. Neither is allowed to be the price of the other.
        Assert.All(results, r => Assert.NotNull(r));

        await using var verify = CreateContext();
        var live = await verify.PartyParticipants.Where(p => p.RetiredAt == null).ToListAsync();
        var retired = await verify.PartyParticipants.Where(p => p.RetiredAt != null).ToListAsync();

        // One canonical guest, and both old rows retired as aliases of it.
        var canonical = Assert.Single(live);
        Assert.Equal(2, retired.Count);
        Assert.Equal(new[] { idA, idB }.Order(), retired.Select(p => p.Id).Order());
        Assert.All(results, r => Assert.Equal(canonical.Id, r!.ParticipantId));

        // Every counter landed EXACTLY once — not twice, and none lost.
        Assert.Equal(4, canonical.AcceptedPhotoCount);
        Assert.Equal(2, canonical.SubmittedMessageCount);
        Assert.Equal(1, canonical.AcceptedPhotoPrintCount);

        // And the votes came WITH their counter. Two rows, both now cast by the
        // canonical guest, and a budget that says two — which is the same fact
        // recorded twice and has to agree.
        var votes = await verify.PartyChallengeVotes.ToListAsync();
        Assert.Equal(2, votes.Count);
        Assert.All(votes, v => Assert.Equal(canonical.Id, v.PartyParticipantId));
        Assert.Equal(2, canonical.ChallengeVoteCount);

        // Nothing is left on either alias — neither activity nor allowance.
        Assert.All(retired, p =>
        {
            Assert.Equal(0, p.AcceptedPhotoCount);
            Assert.Equal(0, p.SubmittedMessageCount);
            Assert.Equal(0, p.ChallengeVoteCount);
            Assert.Equal(0, p.AcceptedPhotoPrintCount);
        });
    }

    [Fact]
    public async Task An_old_row_seen_again_and_again_is_folded_once()
    {
        var identity = Identity();
        var browserToken = identity.NewBrowserToken();
        var legacy = FakeToken();
        await SeedLegacyAsync(legacy, messages: 5);

        // Four requests all presenting the SAME old cookie, two of them at once.
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAll(
            ResolveAfterStartAsync(firstDb, browserToken, start.Task, legacyToken: legacy),
            ResolveAfterStartAsync(secondDb, browserToken, start.Task, start, legacyToken: legacy));
        await using (var third = CreateContext())
            await Service(third).ResolveOrCreateAsync(_linkId, browserToken, legacy);
        await using (var fourth = CreateContext())
            await Service(fourth).ResolveOrCreateAsync(_linkId, browserToken, legacy);

        await using var verify = CreateContext();
        var canonical = await verify.PartyParticipants.SingleAsync(p => p.RetiredAt == null);
        // Five, not ten or twenty: the retirement is the claim, and a retired
        // row is invisible to every lookup afterwards.
        Assert.Equal(5, canonical.SubmittedMessageCount);
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SeedGuestAsync()
    {
        var id = Guid.NewGuid();
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = id, PartyAlbumLinkId = _linkId, TokenHash = new string('b', 64),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static PartyGuestIdentity Identity() => new(new ConfigurationBuilder().Build());

    private static PartyMessageService MessageService(AppDbContext db)
    {
        var participants = new PartyParticipantService(db, TimeProvider.System, Identity());
        return new PartyMessageService(
            db, TimeProvider.System, new PartyMessageAccessResolver(db), participants);
    }

    private static async Task<PartyMessageSubmissionResult?> SubmitAfterStartAsync(
        AppDbContext db, PartyAccess access, Guid guest, string text,
        Task start, TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        try
        {
            return await MessageService(db).SubmitAsync(access, "Anna", text, guest);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // SQLite serialises writers with a whole-database lock, so a loser
            // can surface as "database is locked" rather than as the refusal.
            // It wrote nothing either way, which the row counts then prove.
            return null;
        }
    }

    private async Task<PartyParticipantResolution?> ResolveAfterStartAsync(
        AppDbContext db, string browserToken, Task start, TaskCompletionSource? release = null,
        string? legacyToken = null)
    {
        release?.SetResult();
        await start;
        try
        {
            return await Service(db).ResolveOrCreateAsync(_linkId, browserToken, legacyToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private static PartyParticipantService Service(AppDbContext db) =>
        new(db, TimeProvider.System, Identity());

    private static string FakeToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// A participant exactly as the pre-migration code wrote one: keyed by a
    /// plain hash of a capability-scoped token.
    private async Task<Guid> SeedLegacyAsync(
        string token, int photos = 0, int messages = 0, int photoPrints = 0,
        Guid[]? votedOn = null)
    {
        var id = Guid.NewGuid();
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = id,
            PartyAlbumLinkId = _linkId,
            TokenHash = Identity().LegacyIdentityHash(token),
            AcceptedPhotoCount = photos,
            SubmittedMessageCount = messages,
            // The budget is the number of votes it holds, because that is the
            // only state the vote path can leave behind.
            ChallengeVoteCount = votedOn?.Length ?? 0,
            AcceptedPhotoPrintCount = photoPrints,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        foreach (var challengeId in votedOn ?? [])
        {
            db.PartyChallengeVotes.Add(new PartyChallengeVote
            {
                Id = Guid.NewGuid(), PartyAlbumLinkId = _linkId,
                PartyParticipantId = id, PartyChallengeId = challengeId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=30;Pooling=False")
            .Options;
        return new AppDbContext(options);
    }
}

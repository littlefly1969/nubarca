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
        await db.SaveChangesAsync();
    }

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
        AppDbContext db, string browserToken, Task start, TaskCompletionSource? release = null)
    {
        release?.SetResult();
        await start;
        try
        {
            return await new PartyParticipantService(db, TimeProvider.System, Identity())
                .ResolveOrCreateAsync(_linkId, browserToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Default Timeout=30;Pooling=False")
            .Options;
        return new AppDbContext(options);
    }
}

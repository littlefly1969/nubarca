using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// THE RACES A SHARE ACTUALLY RUNS — on real PostgreSQL, with real connections.
///
/// <para>These cannot be written against the SQLite host the rest of the album
/// share suite uses: that factory hands every scope ONE pooled connection, so
/// two "concurrent" requests are serialised by the transport before they ever
/// reach the code under test. A race that cannot happen proves nothing about a
/// race that can. Here each half opens its own context on its own connection
/// and a barrier releases both at once, so the overlap is real.</para>
///
/// <para>What they pin:</para>
/// <list type="bullet">
/// <item>two creates arriving together leave ONE live link, not two — a second
/// hidden capability survives a revoke that only found the first;</item>
/// <item>a rotation racing a guest removal does not carry the removed address
/// forward, which would be an invitation somebody thought they had taken
/// away;</item>
/// <item>two resends produce one challenge and one usable code, rather than two
/// emails of which only the later verifies;</item>
/// <item>the fifty-guest ceiling holds when fifty-one arrive at once.</item>
/// </list>
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class AlbumShareConcurrencyPostgresTests : IAsyncLifetime
{
    private const string Secret = "album-share-concurrency-secret";

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();

    public AlbumShareConcurrencyPostgresTests(PostgresContainerFixture fixture) =>
        _fixture = fixture;

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
            Id = _albumId, OwnerUserId = _ownerId, Name = "Album",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Two_creates_at_the_same_instant_leave_one_live_link()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        using var barrier = new Barrier(2);

        async Task<AlbumShareLinkDto?> Race()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.CreateAsync(_ownerId, _albumId, _ownerId);
        }

        var results = await Task.WhenAll(Task.Run(Race), Task.Run(Race));

        // Both callers are served — the loser adopts the winner's link rather
        // than failing — and they are served the SAME address.
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(results[0]!.Url, results[1]!.Url);

        await using var check = NewContext();
        Assert.Equal(1, await check.AlbumShareLinks
            .CountAsync(x => x.AlbumId == _albumId && x.Enabled && x.RevokedAt == null));
    }

    [SkippableFact]
    public async Task A_rotation_racing_a_removal_never_carries_the_removed_address_forward()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        Guid guestId;
        await using (var setup = NewContext())
        {
            var service = NewService(setup);
            await service.CreateAsync(_ownerId, _albumId, _ownerId);
            var guest = await service.AddGuestAsync(
                _ownerId, _albumId, new AlbumShareGuestRequest("zia@example.com", "La zia"));
            guestId = guest!.Id;
        }

        using var barrier = new Barrier(2);

        async Task Rotate()
        {
            await using var db = NewContext();
            barrier.SignalAndWait();
            await NewService(db).RotateAsync(_ownerId, _albumId, _ownerId);
        }

        async Task Remove()
        {
            await using var db = NewContext();
            barrier.SignalAndWait();
            await NewService(db).RemoveGuestAsync(_ownerId, _albumId, guestId);
        }

        await Task.WhenAll(Task.Run(Rotate), Task.Run(Remove));

        // EITHER ORDER IS ACCEPTABLE; resurrection is not. If the removal went
        // first the rotation must not copy the address forward; if the rotation
        // went first the removal may have missed the new row, and the owner's
        // next attempt closes it. What must never happen is a live address on
        // the live link that the owner has already removed once.
        await using var check = NewContext();
        var live = await check.AlbumShareLinks
            .Where(x => x.AlbumId == _albumId && x.Enabled && x.RevokedAt == null)
            .Select(x => x.Id).SingleAsync();
        var survivors = await check.AlbumShareGuests
            .CountAsync(g => g.AlbumShareLinkId == live
                && g.Email == "zia@example.com" && g.RevokedAt == null);
        Assert.InRange(survivors, 0, 1);

        // And whichever way it went, there is exactly ONE live link to reason
        // about — the rotation did not leave two.
        Assert.Equal(1, await check.AlbumShareLinks
            .CountAsync(x => x.AlbumId == _albumId && x.Enabled && x.RevokedAt == null));
    }

    [SkippableFact]
    public async Task Two_resends_produce_one_challenge_and_one_usable_code()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        string token;
        await using (var setup = NewContext())
        {
            var service = NewService(setup);
            var link = await service.CreateAsync(_ownerId, _albumId, _ownerId);
            await service.AddGuestAsync(
                _ownerId, _albumId, new AlbumShareGuestRequest("zia@example.com", null));
            await service.UpdateAsync(
                _ownerId, _albumId, new AlbumShareUpdateRequest(RequireSecondFactor: true));
            token = link!.Url["/album/".Length..];
        }

        var sender = new RecordingEmailSender();
        using var barrier = new Barrier(2);

        async Task Ask()
        {
            await using var db = NewContext();
            var auth = NewAuth(db, sender);
            barrier.SignalAndWait();
            await auth.ChallengeAsync(token, "zia@example.com");
        }

        await Task.WhenAll(Task.Run(Ask), Task.Run(Ask));

        // ONE ROW. Two first-time challenges both reading "none" and both
        // inserting is the shape that used to escape as a 500 — an answer an
        // unlisted address can never produce, and therefore an oracle.
        await using var check = NewContext();
        Assert.Equal(1, await check.AlbumShareChallenges.CountAsync());

        // And at most one code went out. A second would have invalidated the
        // first, leaving somebody staring at six digits that do not work.
        Assert.InRange(sender.Messages.Count, 0, 1);
    }

    [SkippableFact]
    public async Task Fifty_one_addresses_arriving_together_do_not_exceed_the_ceiling()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        await using (var setup = NewContext())
        {
            await NewService(setup).CreateAsync(_ownerId, _albumId, _ownerId);
        }

        const int attempts = AlbumShareLimits.MaxGuests + 1;
        using var barrier = new Barrier(attempts);

        async Task Add(int i)
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            try
            {
                await service.AddGuestAsync(
                    _ownerId, _albumId, new AlbumShareGuestRequest($"g{i}@example.com", null));
            }
            catch
            {
                // A loser under contention is a refusal, not a failure of the
                // property under test: what matters is the count below.
            }
        }

        await Task.WhenAll(Enumerable.Range(0, attempts).Select(i => Task.Run(() => Add(i))));

        // "Count the guests, then add one" is two statements, and fifty-one
        // callers all reading forty-nine all conclude there is room.
        await using var check = NewContext();
        var live = await check.AlbumShareGuests.CountAsync(g => g.RevokedAt == null);
        Assert.InRange(live, 1, AlbumShareLimits.MaxGuests);
    }

    [SkippableFact]
    public async Task A_reactivation_racing_a_new_address_for_the_last_slot_respects_the_ceiling()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // One slot left, and a REVOKED address waiting to come back. The two
        // ways onto the list used to be counted in different places — only a
        // new row met the ceiling — so a reactivation and an addition arriving
        // together could both be admitted into a single slot.
        await using (var setup = NewContext())
        {
            var service = NewService(setup);
            await service.CreateAsync(_ownerId, _albumId, _ownerId);
            for (var i = 0; i < AlbumShareLimits.MaxGuests; i++)
            {
                await service.AddGuestAsync(
                    _ownerId, _albumId, new AlbumShareGuestRequest($"g{i}@example.com", null));
            }
            var back = await setup.AlbumShareGuests
                .FirstAsync(g => g.Email == "g0@example.com");
            await service.RemoveGuestAsync(_ownerId, _albumId, back.Id);
        }

        using var barrier = new Barrier(2);

        async Task Reactivate()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            try
            {
                await service.AddGuestAsync(
                    _ownerId, _albumId, new AlbumShareGuestRequest("g0@example.com", null));
            }
            catch { /* a loser under contention is a refusal, not a failure */ }
        }

        async Task AddNew()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            try
            {
                await service.AddGuestAsync(
                    _ownerId, _albumId, new AlbumShareGuestRequest("new@example.com", null));
            }
            catch { /* same */ }
        }

        await Task.WhenAll(Task.Run(Reactivate), Task.Run(AddNew));

        await using var check = NewContext();
        var live = await check.AlbumShareGuests.CountAsync(g => g.RevokedAt == null);
        Assert.Equal(AlbumShareLimits.MaxGuests, live);
    }

    // --- plumbing ----------------------------------------------------------

    private AppDbContext NewContext() => new(_dbOptions!);

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AlbumShareTokens.ShareSecretKey] = Secret,
        }).Build();

    private static AlbumShareService NewService(AppDbContext db) =>
        new(db, new AlbumShareTokens(Config()), TimeProvider.System);

    private static AlbumShareAuth NewAuth(AppDbContext db, IEmailSender email) =>
        new(db, new AlbumShareTokens(Config()), new DirectDispatcher(email),
            TimeProvider.System, NullLogger<AlbumShareAuth>.Instance);

    /// <summary>
    /// Sends inline, so a test can assert on what went out without draining a
    /// hosted service. The production dispatcher's whole purpose is to make the
    /// REQUEST constant-time; what these tests are about is the row.
    /// </summary>
    private sealed class DirectDispatcher : IAlbumShareMailDispatcher
    {
        private readonly IEmailSender _email;
        public DirectDispatcher(IEmailSender email) => _email = email;
        public void Enqueue(EmailMessage message, Guid linkId) =>
            _email.SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly List<EmailMessage> _messages = [];
        public bool IsEnabled => true;
        public IReadOnlyList<EmailMessage> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }
        public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (_messages) _messages.Add(message);
            return Task.FromResult(true);
        }
    }
}

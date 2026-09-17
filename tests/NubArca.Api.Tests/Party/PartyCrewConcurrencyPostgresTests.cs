using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Auth;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// TWO DEVICES, ONE LAST SLOT, THE SAME INSTANT — on real PostgreSQL.
///
/// <para>This is the test the two-device limit exists for, and it cannot be
/// written anywhere else. Counting live grants and then inserting is a
/// read-modify-write: two requests that both read "one" both believe they may
/// have the second, and an in-memory check cannot elect a winner. What does is
/// the transaction opening with a self-assigning update on the COLLABORATOR's
/// own row, which takes that row's write lock and serialises the pair — so the
/// second attempt reads the first one's result rather than the state before
/// it.</para>
///
/// <para>Two independent connections, two real transactions, and a barrier that
/// releases both at once: the race is run, not simulated. What it must never
/// produce is three grants.</para>
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyCrewConcurrencyPostgresTests : IAsyncLifetime
{
    private const string Secret = "party-crew-concurrency-secret";

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _collaboratorId = Guid.NewGuid();
    private readonly Guid _otherCollaboratorId = Guid.NewGuid();

    public PartyCrewConcurrencyPostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = _partyId, OwnerUserId = _ownerId, Title = "Festa",
            Status = PartyStatuses.Live,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyCollaborators.Add(new PartyCollaborator
        {
            Id = _collaboratorId, PartyId = _partyId,
            DisplayName = "Regista", Email = "regista@example.com",
            NormalizedEmail = "regista@example.com", RoleKey = PartyCrewRoles.Director,
            Version = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        // A SECOND person at the SAME party, for the identity race below.
        db.PartyCollaborators.Add(new PartyCollaborator
        {
            Id = _otherCollaboratorId, PartyId = _partyId,
            DisplayName = "Co", Email = "co@example.com",
            NormalizedEmail = "co@example.com", RoleKey = PartyCrewRoles.CoOrganizer,
            Version = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Two_devices_finishing_together_never_produce_a_third_grant()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // ONE slot already taken, so the two below are racing for the last.
        await SeedGrantAsync("already-paired-phone");

        var (firstChallenge, firstOtp) = await SeedChallengeAsync();
        var (secondChallenge, secondOtp) = await SeedChallengeAsync();

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewPairing>> Race(string challenge, string otp)
        {
            await using var db = NewContext();
            var service = NewService(db);
            // Both threads are inside the service's own transaction boundary
            // only after this point, which is what makes the overlap real.
            barrier.SignalAndWait();
            return await service.VerifyAsync(challenge, otp, "Mozilla/5.0 (Linux; Android 14)", null, null);
        }

        var results = await Task.WhenAll(
            Task.Run(() => Race(firstChallenge, firstOtp)),
            Task.Run(() => Race(secondChallenge, secondOtp)));

        // Exactly one device was admitted. The other was told the truth: the
        // code was right, and the collaborator already holds two.
        var outcomes = results
            .Select(r => r.Value?.Result.Outcome ?? PartyCrewVerifyOutcome.DeviceLimitReached)
            .ToArray();
        Assert.Equal(1, outcomes.Count(o => o == PartyCrewVerifyOutcome.Paired));
        Assert.Equal(1, outcomes.Count(o => o == PartyCrewVerifyOutcome.DeviceLimitReached));

        // And the database agrees. THIS is the assertion the slice exists for.
        await using var check = NewContext();
        var live = await check.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == _collaboratorId && g.RevokedAt == null);
        Assert.Equal(PartyCrewLimits.MaxDevicesPerCollaborator, live);

        // The refused one kept its verified challenge, so it can free a slot
        // and finish without typing a second code.
        var refused = await check.PartyCollaboratorAuthChallenges
            .Where(c => c.VerifiedAt != null && c.CompletedAt == null)
            .SingleAsync();
        Assert.Equal(_collaboratorId, refused.PartyCollaboratorId);
    }

    [SkippableFact]
    public async Task The_same_device_racing_itself_takes_one_slot_and_not_two()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        var raw = PartyCrewTokens.NewToken();
        await SeedDeviceAsync(raw, "one-phone");

        var (firstChallenge, firstOtp) = await SeedChallengeAsync();
        var (secondChallenge, secondOtp) = await SeedChallengeAsync();

        using var barrier = new Barrier(2);

        async Task Race(string challenge, string otp)
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            // The SAME device token on both: a double tap, or two tabs.
            await service.VerifyAsync(challenge, otp, "Mozilla/5.0 (Linux; Android 14)", raw, null);
        }

        await Task.WhenAll(
            Task.Run(() => Race(firstChallenge, firstOtp)),
            Task.Run(() => Race(secondChallenge, secondOtp)));

        await using var check = NewContext();
        Assert.Equal(1, await check.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == _collaboratorId && g.RevokedAt == null));
        Assert.Equal(1, await MyDevicesAsync(check));
    }

    [SkippableFact]
    public async Task One_challenge_never_produces_two_devices()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // NO DEVICES AT ALL, and one challenge verified twice at once. The
        // device limit cannot decide this: both requests see room for two, so
        // both would be admitted by a limit check alone. What has to stop the
        // second is the CHALLENGE being spendable exactly once.
        var (challenge, otp) = await SeedChallengeAsync();

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewPairing>> Race()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.VerifyAsync(challenge, otp, "Mozilla/5.0 (Linux; Android 14)", null, null);
        }

        var results = await Task.WhenAll(Task.Run(Race), Task.Run(Race));

        var paired = results.Count(r => r.Value?.Result.Outcome == PartyCrewVerifyOutcome.Paired);
        var refused = results.Count(r => r.Error == PartyCrewAuthError.Unavailable);
        Assert.Equal(1, paired);
        Assert.Equal(1, refused);

        await using var check = NewContext();
        Assert.Equal(1, await check.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == _collaboratorId && g.RevokedAt == null));
        Assert.Equal(1, await MyDevicesAsync(check));
        Assert.Equal(1, await check.PartyCollaboratorAuthChallenges
            .CountAsync(c => c.PartyCollaboratorId == _collaboratorId && c.CompletedAt != null));
        Assert.Equal(1, await check.PartyCollaboratorInvites
            .CountAsync(i => i.PartyCollaboratorId == _collaboratorId && i.ConsumedAt != null));
    }

    [SkippableFact]
    public async Task One_verified_challenge_completes_once_however_many_ask()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // The device-limit road back: a challenge already verified, a slot now
        // free, and two /complete calls at the same instant. It must produce
        // one grant, not two — the second has nothing left to spend.
        var (challenge, otp) = await SeedChallengeAsync();
        await using (var db = NewContext())
        {
            var service = NewService(db);
            var verified = await service.VerifyAsync(
                challenge, otp, "Mozilla/5.0 (Linux; Android 14)", null, null);
            // Verified, and deliberately NOT completed: the seed below leaves
            // the challenge in the state the limit screen leaves it in.
            Assert.NotNull(verified.Value);
        }

        // Undo the pairing that VerifyAsync just did, keeping the challenge
        // verified — exactly the state a DeviceLimitReached leaves behind.
        await using (var reset = NewContext())
        {
            await reset.PartyCollaboratorDeviceGrants.ExecuteDeleteAsync();
            await reset.PartyCrewDevices.ExecuteDeleteAsync();
            await reset.PartyCollaboratorAuthChallenges
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedAt, _ => (DateTime?)null));
            await reset.PartyCollaboratorInvites
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConsumedAt, _ => (DateTime?)null));
        }

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewPairing>> Race()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.CompleteAsync(challenge, "Mozilla/5.0 (Linux; Android 14)", null, null);
        }

        var results = await Task.WhenAll(Task.Run(Race), Task.Run(Race));

        Assert.Equal(1, results.Count(r => r.Value?.Result.Outcome == PartyCrewVerifyOutcome.Paired));
        Assert.Equal(1, results.Count(r => r.Error == PartyCrewAuthError.Unavailable));

        await using var check = NewContext();
        Assert.Equal(1, await check.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == _collaboratorId && g.RevokedAt == null));
        Assert.Equal(1, await check.PartyCollaboratorInvites
            .CountAsync(i => i.PartyCollaboratorId == _collaboratorId && i.ConsumedAt != null));
    }

    [SkippableFact]
    public async Task One_browser_never_becomes_two_people_at_one_party()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // TWO DIFFERENT COLLABORATORS, ONE BROWSER, ONE PARTY, ONE INSTANT.
        // The collaborator row lock cannot decide this: these two requests are
        // about different people and contend on no shared row. What orders them
        // is the DEVICE's own lock, and without it both would insert a grant —
        // leaving the resolver to pick which of two identities is acting.
        var raw = PartyCrewTokens.NewToken();
        await SeedDeviceAsync(raw, "one-browser");

        var (mine, myOtp) = await SeedChallengeAsync();
        var (theirs, theirOtp) = await SeedChallengeAsync(_otherCollaboratorId);

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewPairing>> Race(string challenge, string otp)
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.VerifyAsync(
                challenge, otp, "Mozilla/5.0 (Linux; Android 14)", raw, null);
        }

        var results = await Task.WhenAll(
            Task.Run(() => Race(mine, myOtp)),
            Task.Run(() => Race(theirs, theirOtp)));

        Assert.Equal(1, results.Count(r => r.Value?.Result.Outcome == PartyCrewVerifyOutcome.Paired));
        Assert.Equal(1, results.Count(r => r.Error == PartyCrewAuthError.DeviceAlreadyAssigned));

        // ONE identity on this browser at this party, whichever won.
        await using var check = NewContext();
        var identities = await check.PartyCollaboratorDeviceGrants
            .Where(g => g.RevokedAt == null)
            .Where(g => check.PartyCollaborators.Any(
                c => c.Id == g.PartyCollaboratorId && c.PartyId == _partyId && c.RevokedAt == null))
            .CountAsync();
        Assert.Equal(1, identities);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private AppDbContext NewContext() => new(_dbOptions!);

    /// <summary>
    /// Live devices holding a live grant for THIS test's collaborator.
    ///
    /// <para>Scoped deliberately. The collection's reset truncates six tables
    /// and leans on CASCADE; <c>parties</c> is not among them, so Party Crew
    /// rows outlive a test and a bare device count would read its siblings'
    /// work as its own.</para>
    /// </summary>
    private Task<int> MyDevicesAsync(AppDbContext db) =>
        db.PartyCrewDevices
            .Where(d => d.RevokedAt == null)
            .Where(d => db.PartyCollaboratorDeviceGrants.Any(
                g => g.PartyCrewDeviceId == d.Id
                    && g.PartyCollaboratorId == _collaboratorId
                    && g.RevokedAt == null))
            .CountAsync();

    private static PartyCrewAuthService NewService(AppDbContext db) => new(
        db,
        TimeProvider.System,
        NewTokens(),
        new RecordingEmailSender(),
        new NoopAuditLogger(),
        new StaticOptionsMonitor(new MailOptions { PublicOrigin = "https://cloud.example.com" }),
        NullLogger<PartyCrewAuthService>.Instance);

    private static PartyCrewTokens NewTokens() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PartyCrewTokens.CrewSecretKey] = Secret,
            })
            .Build());

    private async Task SeedGrantAsync(string label)
    {
        await SeedDeviceAsync(PartyCrewTokens.NewToken(), label, withGrant: true);
    }

    private async Task SeedDeviceAsync(string rawToken, string label, bool withGrant = false)
    {
        await using var db = NewContext();
        var device = new PartyCrewDevice
        {
            Id = Guid.NewGuid(),
            TokenHash = PartyCrewTokens.Hash(rawToken),
            DeviceLabel = label,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PartyCrewLimits.DeviceLifetime),
        };
        db.PartyCrewDevices.Add(device);
        if (withGrant)
        {
            db.PartyCollaboratorDeviceGrants.Add(new PartyCollaboratorDeviceGrant
            {
                Id = Guid.NewGuid(),
                PartyCrewDeviceId = device.Id,
                PartyCollaboratorId = _collaboratorId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>A live invite and a live challenge, ready for its code.</summary>
    private Task<(string RawChallengeToken, string Otp)> SeedChallengeAsync() =>
        SeedChallengeAsync(_collaboratorId);

    private async Task<(string RawChallengeToken, string Otp)> SeedChallengeAsync(Guid collaboratorId)
    {
        await using var db = NewContext();
        var inviteId = Guid.NewGuid();
        db.PartyCollaboratorInvites.Add(new PartyCollaboratorInvite
        {
            Id = inviteId,
            PartyCollaboratorId = collaboratorId,
            TokenHash = PartyCrewTokens.Hash(PartyCrewTokens.NewToken()),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PartyCrewLimits.InviteLifetime),
        });

        var challengeId = Guid.NewGuid();
        var raw = PartyCrewTokens.NewToken();
        var otp = PartyCrewTokens.NewOtp();
        db.PartyCollaboratorAuthChallenges.Add(new PartyCollaboratorAuthChallenge
        {
            Id = challengeId,
            PartyCollaboratorId = collaboratorId,
            PartyCollaboratorInviteId = inviteId,
            ChallengeTokenHash = PartyCrewTokens.Hash(raw),
            OtpProof = NewTokens().OtpProof(challengeId, 1, otp),
            OtpGeneration = 1,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PartyCrewLimits.ChallengeLifetime),
            OtpSentAt = DateTime.UtcNow,
            OtpSendCount = 1,
        });
        await db.SaveChangesAsync();
        return (raw, otp);
    }

    private sealed class NoopAuditLogger : IAuditLogger
    {
        public Task LogAsync(
            AuditActor actor, string action, string entityType, Guid? entityId,
            string? ipAddress, object? metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteAsync(
            AuditActor actor, string action, string entityType, Guid? entityId,
            string? ipAddress, object? metadata, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor(MailOptions value) : IOptionsMonitor<MailOptions>
    {
        public MailOptions CurrentValue => value;

        public MailOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MailOptions, string?> listener) => null;
    }
}

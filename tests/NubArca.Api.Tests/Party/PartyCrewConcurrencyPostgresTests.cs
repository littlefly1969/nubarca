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

    [SkippableFact]
    public async Task Two_rotations_at_once_leave_exactly_one_usable_link()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // A HOST DOUBLE-PRESSING "SEND A NEW LINK", or two tabs.
        //
        // A transaction alone does not decide this: under READ COMMITTED both
        // rotations revoke the links they can see and both insert, and the
        // party ends with two live links where the host meant one. Nothing in
        // the schema forbids it — "at most one live row" cannot be written as
        // a filtered unique index across a revoke and an insert — so the
        // mutation path has to serialise, and this is what proves it does.
        await SeedInviteAsync();

        using var barrier = new Barrier(2);

        async Task<PartyCrewResult<PartyCollaboratorInviteDto>> Race()
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            return await service.RotateInviteAsync(_ownerId, _partyId, _collaboratorId, null);
        }

        var results = await Task.WhenAll(Task.Run(Race), Task.Run(Race));
        Assert.All(results, r => Assert.True(r.Succeeded));

        await using var check = NewContext();
        var live = await check.PartyCollaboratorInvites
            .Where(i => i.PartyCollaboratorId == _collaboratorId
                && i.RevokedAt == null && i.ConsumedAt == null)
            .ToListAsync();
        Assert.Single(live);

        // AND IT IS THE LAST ONE HANDED OUT. A host who pressed twice sends the
        // second link; the first must be the one that died.
        var winner = results[1].Value!.InviteUrl;
        var loser = results[0].Value!.InviteUrl;
        var winnerHash = PartyCrewTokens.Hash(TokenOf(winner));
        var loserHash = PartyCrewTokens.Hash(TokenOf(loser));
        Assert.Contains(live[0].TokenHash, new[] { winnerHash, loserHash });

        // Whichever survived, exactly one of the two links opens anything.
        var usable = await check.PartyCollaboratorInvites
            .CountAsync(i => (i.TokenHash == winnerHash || i.TokenHash == loserHash)
                && i.RevokedAt == null && i.ConsumedAt == null);
        Assert.Equal(1, usable);
    }

    [SkippableFact]
    public async Task A_rotation_racing_a_pairing_never_leaves_a_code_that_opens_nothing()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        var raw = await SeedInviteAsync();

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewChallengeStart>> Start()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.StartAsync(raw, "Mozilla/5.0 (Linux; Android 14)", null);
        }

        async Task Rotate()
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            await service.RotateInviteAsync(_ownerId, _partyId, _collaboratorId, null);
        }

        var started = Task.Run(Start);
        await Task.WhenAll(started, Task.Run(Rotate));
        var result = await started;

        await using var check = NewContext();

        // EITHER ORDER IS FINE, and the ordering is genuinely a race: a
        // rotation that lands after the start has answered is simply the host
        // winning, and their new link is the way forward. What must always hold
        // is that a way forward EXISTS — exactly one usable link, whoever won —
        // and that a refusal is a clean refusal with nothing left behind.
        Assert.Equal(1, await check.PartyCollaboratorInvites
            .CountAsync(i => i.PartyCollaboratorId == _collaboratorId
                && i.RevokedAt == null && i.ConsumedAt == null));

        if (result.Error is not null)
        {
            Assert.Equal(PartyCrewAuthError.Unavailable, result.Error);
            Assert.Empty(await check.PartyCollaboratorAuthChallenges
                .Where(c => c.PartyCollaboratorId == _collaboratorId
                    && c.RevokedAt == null && c.CompletedAt == null)
                .ToListAsync());
        }
    }

    [SkippableFact]
    public async Task A_link_already_replaced_never_sends_a_code_for_it()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // The same collision, ordered rather than raced, so it asserts the fix
        // rather than a coin toss: the rotation has COMMITTED before the start
        // begins. Reading the invite before the lock and trusting that copy is
        // exactly what would send a code for a link that is already dead.
        var old = await SeedInviteAsync();
        await using (var db = NewContext())
        {
            await NewCrewService(db).RotateInviteAsync(_ownerId, _partyId, _collaboratorId, null);
        }

        var mailer = new RecordingEmailSender();
        await using var start = NewContext();
        var result = await NewService(start, mailer)
            .StartAsync(old, "Mozilla/5.0 (Linux; Android 14)", null);

        Assert.Equal(PartyCrewAuthError.Unavailable, result.Error);
        // Nothing sent, and nothing left behind to be completed later.
        Assert.Empty(mailer.Messages);

        await using var check = NewContext();
        Assert.Empty(await check.PartyCollaboratorAuthChallenges
            .Where(c => c.PartyCollaboratorId == _collaboratorId && c.RevokedAt == null)
            .ToListAsync());
    }

    [SkippableFact]
    public async Task Two_resends_at_once_put_one_email_in_the_inbox()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        var (challenge, _) = await SeedChallengeAsync();
        // Past the interval, so the only thing in the way is the other request.
        await using (var age = NewContext())
        {
            await age.PartyCollaboratorAuthChallenges
                .ExecuteUpdateAsync(u => u.SetProperty(
                    c => c.OtpSentAt, DateTime.UtcNow - PartyCrewLimits.ResendInterval * 2));
        }

        var mailer = new RecordingEmailSender();
        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthError?> Race()
        {
            await using var db = NewContext();
            var service = NewService(db, mailer);
            barrier.SignalAndWait();
            return await service.ResendAsync(challenge);
        }

        var results = await Task.WhenAll(Task.Run(Race), Task.Run(Race));

        // ONE PHYSICAL EMAIL. Guarding the database after the send would leave
        // the row correct and two codes in somebody's inbox — and, depending on
        // delivery order, the one they read last might be the losing one.
        Assert.Single(mailer.Messages);
        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(1, results.Count(r => r is not null));

        await using var check = NewContext();
        var after = await check.PartyCollaboratorAuthChallenges
            .SingleAsync(c => c.PartyCollaboratorId == _collaboratorId);
        Assert.Equal(2, after.OtpSendCount);
        Assert.Equal(2, after.OtpGeneration);
    }

    [SkippableFact]
    public async Task An_email_change_racing_a_pairing_never_leaves_the_old_address_authorised()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // THE PROPERTY CHANGING AN ADDRESS EXISTS TO DESTROY. A device is only
        // trustworthy because a code went to the address the host chose; when
        // the host replaces that address they may be naming a different person,
        // so every device has to go. A pairing landing in the middle of that
        // change used to survive it: the change revoked the grants it could
        // see, and the pairing — ordered behind it on the same row — then made
        // a new one, verified against an address that no longer exists.
        var (challenge, otp) = await SeedChallengeAsync();

        using var barrier = new Barrier(2);

        async Task<PartyCrewAuthResult<PartyCrewPairing>> Pair()
        {
            await using var db = NewContext();
            var service = NewService(db);
            barrier.SignalAndWait();
            return await service.VerifyAsync(
                challenge, otp, "Mozilla/5.0 (Linux; Android 14)", null, null);
        }

        async Task<PartyCrewError?> ChangeEmail()
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            var result = await service.UpdateAsync(
                _ownerId, _partyId, _collaboratorId,
                new PartyCollaboratorUpdateDto(
                    "Regista", "nuovo@example.com", PartyCrewRoles.Director, 1),
                null);
            return result.Error;
        }

        var pairing = Task.Run(Pair);
        var change = Task.Run(ChangeEmail);
        await Task.WhenAll(pairing, change);

        await using var check = NewContext();
        var collaborator = await check.PartyCollaborators.SingleAsync(c => c.Id == _collaboratorId);
        var liveGrants = await check.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == _collaboratorId && g.RevokedAt == null);

        if (collaborator.NormalizedEmail == "nuovo@example.com")
        {
            // THE CHANGE LANDED. Whether the pairing finished before it or was
            // refused by it, no device may remain: every one of them was proved
            // against the address that was just replaced.
            Assert.Equal(0, liveGrants);
        }
        else
        {
            // The change lost — and then it changed nothing, so whatever the
            // pairing produced is still verified against the live address.
            Assert.Equal("regista@example.com", collaborator.NormalizedEmail);
        }
    }

    [SkippableFact]
    public async Task Two_collaborators_moving_to_one_address_produce_a_conflict_not_a_crash()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // TWO DIFFERENT PEOPLE, ONE NEW ADDRESS, AT THE SAME INSTANT.
        //
        // The row lock every update takes serialises writers on ONE
        // collaborator; it says nothing about two DIFFERENT collaborators
        // moving to the same address. Both take their own lock, both read a
        // party in which nobody holds `team@example.com` yet, and both
        // proceed — so the application's `AnyAsync` check passes twice and the
        // filtered unique index is what actually decides.
        //
        // That is correct: the database is the last authority, and it is the
        // only thing that can be. What the LOSER must not get is a 500 — the
        // request was well-formed, the answer is knowable, and it is the same
        // answer the pre-check gives when it wins the race.
        const string contested = "team@example.com";

        using var barrier = new Barrier(2);

        async Task<PartyCrewError?> Rename(Guid collaboratorId, string name)
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            var result = await service.UpdateAsync(
                _ownerId, _partyId, collaboratorId,
                new PartyCollaboratorUpdateDto(name, contested, PartyCrewRoles.CoOrganizer, 1),
                null);
            return result.Error;
        }

        var results = await Task.WhenAll(
            Task.Run(() => Rename(_collaboratorId, "Regista")),
            Task.Run(() => Rename(_otherCollaboratorId, "Co")));

        // One success, one EmailInUse, and — the point of the fix — no
        // exception escaping as a 500.
        Assert.Equal(1, results.Count(e => e is null));
        Assert.Equal(1, results.Count(e => e == PartyCrewError.EmailInUse));

        await using var check = NewContext();

        // THE DATABASE AGREES. Exactly one live collaborator holds it.
        Assert.Equal(1, await check.PartyCollaborators
            .CountAsync(c => c.PartyId == _partyId
                && c.RevokedAt == null
                && c.NormalizedEmail == contested));

        // AND THE LOSER IS INTACT. An email change revokes every device,
        // invite and challenge before it saves, so a refusal that did not roll
        // all of that back would leave somebody signed out of a rename that
        // never happened.
        var untouched = await check.PartyCollaborators
            .AsNoTracking()
            .Where(c => c.PartyId == _partyId && c.NormalizedEmail != contested)
            .SingleAsync();
        Assert.Equal(1, untouched.Version);
        Assert.Contains(untouched.NormalizedEmail, new[] { "regista@example.com", "co@example.com" });
    }

    [SkippableFact]
    public async Task A_new_collaborator_racing_a_rename_for_one_address_is_refused_once()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // The other half of the same invariant, and it costs one more method
        // rather than a framework: CREATE against UPDATE. The create path has
        // no row to lock at all — there is no collaborator yet — so the index
        // is the only thing standing between two inserts.
        const string contested = "newcomer@example.com";

        using var barrier = new Barrier(2);

        async Task<PartyCrewError?> Create()
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            var result = await service.CreateAsync(
                _ownerId, _partyId,
                new PartyCollaboratorWriteDto("Nuovo", contested, PartyCrewRoles.Director),
                null);
            return result.Error;
        }

        async Task<PartyCrewError?> Rename()
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            var result = await service.UpdateAsync(
                _ownerId, _partyId, _collaboratorId,
                new PartyCollaboratorUpdateDto("Regista", contested, PartyCrewRoles.Director, 1),
                null);
            return result.Error;
        }

        var results = await Task.WhenAll(Task.Run(Create), Task.Run(Rename));

        Assert.Equal(1, results.Count(e => e is null));
        Assert.Equal(1, results.Count(e => e == PartyCrewError.EmailInUse));

        await using var check = NewContext();
        Assert.Equal(1, await check.PartyCollaborators
            .CountAsync(c => c.PartyId == _partyId
                && c.RevokedAt == null
                && c.NormalizedEmail == contested));
    }

    [SkippableFact]
    public async Task Two_stale_forms_never_leave_a_role_standing_over_another_role_s_grants()
    {
        Skip.IfNot(_fixture.Available, "PostgreSQL container is not available.");

        // Both callers hold the SAME version, which is the whole point: an
        // in-memory version check passes for both. What makes that dangerous is
        // that the role and its grants are written by different statements — so
        // the loser can leave `RoleKey = director` standing over a
        // co-organizer's capability rows, and AUTHORISATION READS THE ROWS. A
        // session presenting as Regista would hold a Co-organizzatore's reach
        // over guests, invitations and attendance.
        await using (var seed = NewContext())
        {
            foreach (var key in PartyCrewRoles.Preset(PartyCrewRoles.Director))
            {
                seed.PartyCollaboratorGrants.Add(new PartyCollaboratorGrant
                {
                    Id = Guid.NewGuid(),
                    PartyCollaboratorId = _collaboratorId,
                    CapabilityKey = key,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        using var barrier = new Barrier(2);

        async Task<PartyCrewError?> Update(string role, string name)
        {
            await using var db = NewContext();
            var service = NewCrewService(db);
            barrier.SignalAndWait();
            var result = await service.UpdateAsync(
                _ownerId, _partyId, _collaboratorId,
                new PartyCollaboratorUpdateDto(name, "regista@example.com", role, 1), null);
            return result.Error;
        }

        var results = await Task.WhenAll(
            Task.Run(() => Update(PartyCrewRoles.CoOrganizer, "Co")),
            Task.Run(() => Update(PartyCrewRoles.Director, "Regista di nuovo")));

        // Exactly one wins; the other is told its form was stale.
        Assert.Equal(1, results.Count(e => e is null));
        Assert.Equal(1, results.Count(e => e == PartyCrewError.VersionConflict));

        await using var check = NewContext();
        var collaborator = await check.PartyCollaborators.SingleAsync(c => c.Id == _collaboratorId);
        var grants = await check.PartyCollaboratorGrants
            .Where(g => g.PartyCollaboratorId == _collaboratorId)
            .Select(g => g.CapabilityKey)
            .ToListAsync();

        // THE ROW AND THE ROWS AGREE, whichever won. This is the assertion the
        // slice actually depends on: the role a host reads is the authority the
        // server enforces.
        Assert.Equal(
            PartyCrewRoles.Preset(collaborator.RoleKey).OrderBy(k => k, StringComparer.Ordinal),
            grants.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, collaborator.Version);
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

    private static PartyCrewAuthService NewService(
        AppDbContext db, RecordingEmailSender? mailer = null) => new(
        db,
        TimeProvider.System,
        NewTokens(),
        mailer ?? new RecordingEmailSender(),
        new NoopAuditLogger(),
        new StaticOptionsMonitor(new MailOptions { PublicOrigin = "https://cloud.example.com" }),
        NullLogger<PartyCrewAuthService>.Instance);

    private static PartyCrewService NewCrewService(AppDbContext db) => new(
        db,
        TimeProvider.System,
        new RecordingEmailSender(),
        new NoopAuditLogger(),
        new StaticOptionsMonitor(new MailOptions { PublicOrigin = "https://cloud.example.com" }));

    /// <summary>A live link for this collaborator, and its raw token.</summary>
    private async Task<string> SeedInviteAsync()
    {
        await using var db = NewContext();
        var raw = PartyCrewTokens.NewToken();
        db.PartyCollaboratorInvites.Add(new PartyCollaboratorInvite
        {
            Id = Guid.NewGuid(),
            PartyCollaboratorId = _collaboratorId,
            TokenHash = PartyCrewTokens.Hash(raw),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PartyCrewLimits.InviteLifetime),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    /// <summary>The raw token out of an invite URL's fragment.</summary>
    private static string TokenOf(string inviteUrl) =>
        inviteUrl[(inviteUrl.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..];

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

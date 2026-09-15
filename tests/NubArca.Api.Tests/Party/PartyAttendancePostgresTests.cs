using Microsoft.EntityFrameworkCore;
using Npgsql;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Attendance's races, on real PostgreSQL.
///
/// <para>SQLite serialises every writer behind one lock, so it can only show two
/// check-ins taking turns. PostgreSQL takes row and index locks, as production
/// does: here two check-ins of one guest — the host's and the guest's own "Sono
/// qui" among them — genuinely meet at the primary key, and two copies of one
/// "add" genuinely meet at the unique index. The database is the final
/// authority, and these tests make it prove it.</para>
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyAttendancePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _marioId = Guid.NewGuid();
    private readonly Guid _lauraId = Guid.NewGuid();

    public PartyAttendancePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com", DisplayName = "Owner", CreatedAt = now,
        });
        // Live, and deliberately with no album and no QR: arrivals need neither.
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = _partyId, OwnerUserId = _ownerId, Title = "Matrimonio di Marta",
            Status = PartyStatuses.Live, Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        db.PartyInvitationGroups.Add(new PartyInvitationGroup
        {
            Id = _groupId, PartyId = _partyId, Label = "Mario e Laura", RecipientEmail = "mario@example.com",
            CapabilityId = Guid.NewGuid(), TokenHash = new string('a', 64),
            CapabilityIssuedAt = now, Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        foreach (var (id, name, order) in new[] { (_marioId, "Mario", 0), (_lauraId, "Laura", 1) })
        {
            db.PartyGuests.Add(new PartyGuest
            {
                Id = id, PartyInvitationGroupId = _groupId, Name = name, SortOrder = order,
                CreatedAt = now, UpdatedAt = now,
            });
            db.PartyRsvps.Add(new PartyRsvp { PartyGuestId = id, Status = PartyRsvpStatuses.Attending, UpdatedAt = now });
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext NewContext() => new(_dbOptions!);

    private static PartyAttendanceService Attendance(AppDbContext db) => new(db, TimeProvider.System);

    /// <summary>What the group's personal invitation resolves to while the party is live.</summary>
    private PartyInvitationAccess Invitation() => new(
        _groupId, _partyId, _ownerId, PartyStatuses.Live,
        PartyGuestExperience.Resolve(PartyStatuses.Live, null, null, DateTime.UtcNow)!);

    private static async Task<T[]> RaceAsync<T>(params Func<Task<T>>[] contenders)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = contenders.Select(async contender =>
        {
            await gate.Task;
            return await contender();
        }).ToList();
        gate.SetResult();
        return await Task.WhenAll(running);
    }

    // --- One arrival per guest -------------------------------------------------------

    [SkippableFact]
    public async Task Two_host_check_ins_of_one_guest_meet_at_the_key_and_leave_one_row()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var firstDb = NewContext();
        await using var secondDb = NewContext();

        var results = await RaceAsync(
            () => Attendance(firstDb).CheckInGuestAsync(_ownerId, _partyId, _marioId),
            () => Attendance(secondDb).CheckInGuestAsync(_ownerId, _partyId, _marioId));

        Assert.All(results, r => Assert.Equal(PartyAttendanceOutcome.Ok, r.Outcome));
        Assert.Single(results, r => r.Changed);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PartyGuestAttendances.CountAsync());
    }

    [SkippableFact]
    public async Task The_host_and_the_guest_checking_in_together_leave_one_row_that_the_first_one_wrote()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var hostDb = NewContext();
        await using var guestDb = NewContext();

        var results = await RaceAsync(
            () => Attendance(hostDb).CheckInGuestAsync(_ownerId, _partyId, _marioId),
            () => Attendance(guestDb).CheckInFromInvitationAsync(Invitation(), _marioId));

        Assert.All(results, r => Assert.Equal(PartyAttendanceOutcome.Ok, r.Outcome));
        var winner = Assert.Single(results, r => r.Changed);
        await using var verify = NewContext();
        var row = await verify.PartyGuestAttendances.SingleAsync();
        // The row's source is the winner's: the host's call is the one that
        // carries an attendance projection, the guest's is not.
        Assert.Equal(
            winner.Attendance is null ? PartyAttendanceSources.Invitation : PartyAttendanceSources.Owner,
            row.Source);

        // Every later check-in, from either side, changes neither moment nor source.
        await using (var db = NewContext())
        {
            Assert.False((await Attendance(db).CheckInGuestAsync(_ownerId, _partyId, _marioId)).Changed);
            Assert.False((await Attendance(db).CheckInFromInvitationAsync(Invitation(), _marioId)).Changed);
        }
        await using var after = NewContext();
        var kept = await after.PartyGuestAttendances.SingleAsync();
        Assert.Equal(row.CheckedInAt, kept.CheckedInAt);
        Assert.Equal(row.Source, kept.Source);
        // And no RSVP moved.
        Assert.All(await after.PartyRsvps.ToListAsync(), r => Assert.Equal(PartyRsvpStatuses.Attending, r.Status));
    }

    [SkippableFact]
    public async Task A_check_in_that_meets_an_uncommitted_one_waits_and_then_changes_nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // The guest's "Sono qui" has written its row and not committed.
        await using var holder = NewContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var guestMoment = DateTime.UtcNow.AddMinutes(-1);
        holder.PartyGuestAttendances.Add(new PartyGuestAttendance
        {
            PartyGuestId = _marioId, CheckedInAt = guestMoment, Source = PartyAttendanceSources.Invitation,
            CreatedAt = guestMoment,
        });
        await holder.SaveChangesAsync();

        // The host cannot see it, so it tries to write its own — and the key
        // makes it WAIT for the first one's outcome.
        await using var hostDb = NewContext();
        var checkIn = Attendance(hostDb).CheckInGuestAsync(_ownerId, _partyId, _marioId);
        var first = await Task.WhenAny(checkIn, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(checkIn, first);

        await transaction.CommitAsync();
        var result = await checkIn;

        Assert.Equal(PartyAttendanceOutcome.Ok, result.Outcome);
        Assert.False(result.Changed);
        await using var verify = NewContext();
        var row = await verify.PartyGuestAttendances.SingleAsync();
        Assert.Equal(PartyAttendanceSources.Invitation, row.Source);
        Assert.Equal(guestMoment, row.CheckedInAt, TimeSpan.FromMilliseconds(1));
    }

    [SkippableFact]
    public async Task A_guest_taking_back_its_mark_cannot_take_back_the_hosts()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using (var db = NewContext())
        {
            Assert.True((await Attendance(db).CheckInGuestAsync(_ownerId, _partyId, _lauraId)).Changed);
            var refused = await Attendance(db).UndoFromInvitationAsync(Invitation(), _lauraId);
            Assert.Equal(PartyAttendanceOutcome.RecordedByHost, refused.Outcome);
        }
        await using var verify = NewContext();
        Assert.Equal(PartyAttendanceSources.Owner, (await verify.PartyGuestAttendances.SingleAsync()).Source);
    }

    // --- One add, one person -----------------------------------------------------------

    [SkippableFact]
    public async Task Two_copies_of_one_add_record_one_person()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var click = Guid.NewGuid();
        await using var firstDb = NewContext();
        await using var secondDb = NewContext();

        var results = await RaceAsync(
            () => Attendance(firstDb).CreateOtherGuestAsync(_ownerId, _partyId, "Walter", click),
            () => Attendance(secondDb).CreateOtherGuestAsync(_ownerId, _partyId, "Walter", click));

        Assert.All(results, r => Assert.Equal(PartyAttendanceOutcome.Ok, r.Outcome));
        Assert.Single(results, r => r.Changed);
        Assert.Equal(results[0].AttendanceGuestId, results[1].AttendanceGuestId);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PartyAttendanceGuests.CountAsync());
    }

    [SkippableFact]
    public async Task Two_renames_quoting_one_version_let_exactly_one_land()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        Guid id;
        await using (var db = NewContext())
        {
            id = (await Attendance(db).CreateOtherGuestAsync(_ownerId, _partyId, "Walt", Guid.NewGuid()))
                .AttendanceGuestId!.Value;
        }
        await using var firstDb = NewContext();
        await using var secondDb = NewContext();

        var results = await RaceAsync(
            () => Attendance(firstDb).UpdateOtherGuestAsync(_ownerId, _partyId, id, "Walter", 1),
            () => Attendance(secondDb).UpdateOtherGuestAsync(_ownerId, _partyId, id, "Walterino", 1));

        var landed = Assert.Single(results, r => r.Outcome == PartyAttendanceOutcome.Ok);
        Assert.Single(results, r => r.Outcome == PartyAttendanceOutcome.VersionConflict);
        await using var verify = NewContext();
        var row = await verify.PartyAttendanceGuests.SingleAsync();
        Assert.Equal(2, row.Version);
        Assert.Equal(landed.Attendance!.OtherGuests.Single().Name, row.Name);
    }

    // --- The schema is the authority ----------------------------------------------------

    [SkippableFact]
    public async Task The_migration_applied_and_the_database_holds_every_rule()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var db = NewContext();

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_AddPartyAttendance"));

        // Both foreign keys RESTRICT: what a party owns is erased out loud.
        var deleteRules = await db.Database
            .SqlQueryRaw<string>(
                "SELECT c.confdeltype::text AS \"Value\" FROM pg_constraint c "
                + "JOIN pg_class t ON t.oid = c.conrelid WHERE c.contype = 'f' AND t.relname IN "
                + "('party_guest_attendance', 'party_attendance_guests')")
            .ToListAsync();
        Assert.Equal(2, deleteRules.Count);
        Assert.All(deleteRules, rule => Assert.Equal("r", rule));

        var checks = await db.Database
            .SqlQueryRaw<string>("SELECT conname AS \"Value\" FROM pg_constraint WHERE contype = 'c'")
            .ToListAsync();
        foreach (var name in new[]
        {
            "ck_party_guest_attendance_source", "ck_party_attendance_guests_version", "ck_party_attendance_guests_name",
        })
        {
            Assert.Contains(name, checks);
        }

        // Enforced, not merely declared.
        const string insertArrival =
            "INSERT INTO party_guest_attendance (\"PartyGuestId\", \"CheckedInAt\", \"Source\", \"CreatedAt\") "
            + "VALUES ({0}, now(), {1}, now())";
        await AssertRefusedAsync(db, "23514", insertArrival, _marioId, "kiosk");
        await db.Database.ExecuteSqlRawAsync(insertArrival, _marioId, PartyAttendanceSources.Owner);
        await AssertRefusedAsync(db, "23505", insertArrival, _marioId, PartyAttendanceSources.Invitation);
        await AssertRefusedAsync(db, "23503", insertArrival, Guid.NewGuid(), PartyAttendanceSources.Owner);

        const string insertOther =
            "INSERT INTO party_attendance_guests "
            + "(\"Id\", \"PartyId\", \"Name\", \"ClientRequestId\", \"CheckedInAt\", \"Version\", \"CreatedAt\", \"UpdatedAt\") "
            + "VALUES ({0}, {1}, {2}, {3}, now(), {4}, now(), now())";
        var click = Guid.NewGuid();
        await AssertRefusedAsync(db, "23514", insertOther, Guid.NewGuid(), _partyId, "", Guid.NewGuid(), 1);
        await AssertRefusedAsync(db, "23514", insertOther, Guid.NewGuid(), _partyId, "Anna", Guid.NewGuid(), 0);
        await db.Database.ExecuteSqlRawAsync(insertOther, Guid.NewGuid(), _partyId, "Anna", click, 1);
        await AssertRefusedAsync(db, "23505", insertOther, Guid.NewGuid(), _partyId, "Anna bis", click, 1);

        // A name of 120 code points fits whole — the column counts what the
        // validator counts — and 121 does not.
        var longest = string.Concat(Enumerable.Repeat("🎉", PartyAttendanceLimits.MaxNameLength));
        var stored = await Attendance(db).CreateOtherGuestAsync(_ownerId, _partyId, longest, Guid.NewGuid());
        Assert.Equal(PartyAttendanceOutcome.Ok, stored.Outcome);
        await AssertRefusedAsync(db, "22001", insertOther, Guid.NewGuid(), _partyId, longest + "🎉", Guid.NewGuid(), 1);
    }

    [SkippableFact]
    public async Task Tearing_the_party_down_erases_both_kinds_of_arrival_through_the_restricting_keys()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using (var db = NewContext())
        {
            await Attendance(db).CheckInGuestAsync(_ownerId, _partyId, _marioId);
            await Attendance(db).CheckInFromInvitationAsync(Invitation(), _lauraId);
            await Attendance(db).CreateOtherGuestAsync(_ownerId, _partyId, "Walter", Guid.NewGuid());
        }

        await using (var db = NewContext())
        {
            await new PartyStateEraser(db).EraseAsync(_partyId, null);
        }

        await using var verify = NewContext();
        Assert.Equal(0, await verify.PartyGuestAttendances.CountAsync());
        Assert.Equal(0, await verify.PartyAttendanceGuests.CountAsync());
        Assert.Equal(0, await verify.PartyGuests.CountAsync());
        Assert.False(await verify.Parties.AnyAsync(p => p.Id == _partyId));
    }

    private static async Task AssertRefusedAsync(AppDbContext db, string sqlState, string sql, params object[] args)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, args));
        Assert.Equal(sqlState, refused.SqlState);
    }
}

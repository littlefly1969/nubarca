using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Auth;
using NubArca.Api.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The guest console at full size, and the share ledger's races, on real
/// PostgreSQL.
///
/// <para>A thousand groups is the domain's own ceiling, so it is the fixture:
/// every page of it is read and must come back complete, in the database's own
/// order, in the same handful of statements as a party of ten — the proof that
/// nothing loads the list to show a page and nothing asks per group. The counts
/// are checked against the fixture computed by hand. The timings and sizes are
/// printed for the record and asserted only loosely, because a CI runner is not
/// a benchmark.</para>
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyGuestDirectoryPostgresTests : IAsyncLifetime
{
    private const string Origin = "https://cloud.example.com";

    private readonly PostgresContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly CommandCounter _commands = new();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly RecordingEmailSender _email = new();
    private readonly PartyInvitationTokens _tokens = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PartyInvitationTokens.InvitationSecretKey] = "pg-test-directory-secret",
        })
        .Build());
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();

    public PartyGuestDirectoryPostgresTests(PostgresContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .AddInterceptors(_commands)
            .Options;
        await _fixture.ResetDatabaseAsync();
        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com", DisplayName = "Owner", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext NewContext() => new(_dbOptions!);

    private static StaticOptionsMonitor<MailOptions> Mail() =>
        new(new MailOptions { Enabled = true, FromAddress = "party@example.com", PublicOrigin = Origin });

    private PartyGuestDirectoryService Directory(AppDbContext db) => new(db, _email, Mail(), _protection);

    private PartyInvitationDeliveryService Delivery(AppDbContext db) =>
        new(db, TimeProvider.System,
            new PartyService(db, TimeProvider.System, new PartyStateEraser(db), null!),
            new PartyInvitationService(db, TimeProvider.System, _tokens, _email, Mail()),
            Directory(db), _tokens, _email, Mail(), NullLogger<PartyInvitationDeliveryService>.Instance);

    private async Task<Guid> SeedPartyAsync(AppDbContext db, string status)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = id, OwnerUserId = _ownerId, Title = "Festa grande", Status = status, Version = 1,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // --- A thousand groups ---------------------------------------------------------------

    private sealed record Fixture(
        int Groups, int Named, int Pending, int Attending, int Declined, int Unanswered,
        int ExpectedArrived, int UnexpectedKnown, int Others, int Accented, int ToArrive);

    /// <summary>
    /// 1000 groups of three, answers and arrivals spread across them, 50 other
    /// arrivals and a share for every fifth group — written as the application
    /// writes them, folded text included — and the counts they must produce.
    /// </summary>
    private async Task<Fixture> SeedThousandAsync(Guid partyId)
    {
        await using var db = NewContext();
        var now = DateTime.UtcNow;
        int named = 0, pending = 0, attending = 0, declined = 0, unanswered = 0;
        int expectedArrived = 0, unexpectedKnown = 0, accented = 0, toArrive = 0;
        for (var i = 0; i < 1000; i++)
        {
            var label = i % 7 == 0 ? $"Élodie {i:D4}" : $"Gruppo {i:D4}";
            if (i % 7 == 0) accented++;
            var (capabilityId, tokenHash) = _tokens.Mint();
            var groupId = Guid.NewGuid();
            db.PartyInvitationGroups.Add(new PartyInvitationGroup
            {
                Id = groupId, PartyId = partyId, Label = label, RecipientEmail = $"g{i}@example.com",
                Phone = i % 2 == 0 ? $"+39 333 {i:D7}" : null,
                SearchText = PartySearchText.ForGroup(label, $"g{i}@example.com", i % 2 == 0 ? $"+39 333 {i:D7}" : null),
                MaxAdditionalGuests = 1, CapabilityId = capabilityId, TokenHash = tokenHash,
                CapabilityIssuedAt = now, Version = 1, CreatedAt = now.AddSeconds(i), UpdatedAt = now,
            });
            var groupPending = false;
            var groupToArrive = false;
            for (var j = 0; j < 3; j++)
            {
                var guestId = Guid.NewGuid();
                var name = $"Ospite {i}-{j}";
                var status = ((i + j) % 3) switch
                {
                    0 => PartyRsvpStatuses.Pending,
                    1 => PartyRsvpStatuses.Attending,
                    _ => PartyRsvpStatuses.Declined,
                };
                db.PartyGuests.Add(new PartyGuest
                {
                    Id = guestId, PartyInvitationGroupId = groupId, Name = name, SortOrder = j,
                    SearchText = PartySearchText.ForGuest(name, null, null), CreatedAt = now, UpdatedAt = now,
                });
                db.PartyRsvps.Add(new PartyRsvp { PartyGuestId = guestId, Status = status, UpdatedAt = now });
                named++;
                if (status == PartyRsvpStatuses.Pending) { pending++; groupPending = true; }
                if (status == PartyRsvpStatuses.Attending) attending++;
                if (status == PartyRsvpStatuses.Declined) declined++;
                var arrived = i % 4 == 0 && j != 0;
                if (arrived)
                {
                    db.PartyGuestAttendances.Add(new PartyGuestAttendance
                    {
                        PartyGuestId = guestId, CheckedInAt = now, Source = PartyAttendanceSources.Owner, CreatedAt = now,
                    });
                    if (status == PartyRsvpStatuses.Attending) expectedArrived++;
                    else unexpectedKnown++;
                }
                else if (status == PartyRsvpStatuses.Attending)
                {
                    groupToArrive = true;
                }
            }
            if (groupPending) unanswered++;
            if (groupToArrive) toArrive++;
            if (i % 5 == 0)
            {
                db.PartyInvitationDeliveries.Add(new PartyInvitationDelivery
                {
                    Id = Guid.NewGuid(), PartyInvitationGroupId = groupId, ClientRequestId = Guid.NewGuid(),
                    CapabilityId = capabilityId, Channel = PartyInvitationDeliveryChannels.WhatsApp,
                    Kind = PartyInvitationDeliveryKinds.Initial, Status = PartyInvitationDeliveryStatuses.Shared,
                    CreatedAt = now, CompletedAt = now,
                });
            }
        }
        for (var k = 0; k < 50; k++)
        {
            var name = $"Imprevisto {k:D2}";
            db.PartyAttendanceGuests.Add(new PartyAttendanceGuest
            {
                Id = Guid.NewGuid(), PartyId = partyId, Name = name, SearchText = PartySearchText.ForName(name),
                ClientRequestId = Guid.NewGuid(), CheckedInAt = now.AddSeconds(k), Version = 1,
                CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
        return new Fixture(1000, named, pending, attending, declined, unanswered, expectedArrived, unexpectedKnown,
            50, accented, toArrive);
    }

    private sealed record Walk(List<PartyGuestDirectoryItemDto> Items, List<int> Statements, List<long> Milliseconds, int FirstPageBytes);

    private async Task<Walk> WalkAsync(Guid partyId, string? q, string? state, int take)
    {
        var items = new List<PartyGuestDirectoryItemDto>();
        var statements = new List<int>();
        var milliseconds = new List<long>();
        var firstPageBytes = 0;
        string? cursor = null;
        do
        {
            await using var db = NewContext();
            _commands.Reset();
            var clock = Stopwatch.StartNew();
            var result = await Directory(db).PageAsync(_ownerId, partyId, new PartyGuestDirectoryQuery(q, state, cursor, take));
            clock.Stop();
            Assert.Equal(PartyGuestDirectoryOutcome.Ok, result.Outcome);
            var page = result.Page!;
            if (cursor is null)
            {
                Assert.NotNull(page.Summary);
                firstPageBytes = JsonSerializer.SerializeToUtf8Bytes(page, JsonSerializerOptions.Web).Length;
            }
            else
            {
                Assert.Null(page.Summary);
            }
            statements.Add(_commands.Count);
            milliseconds.Add(clock.ElapsedMilliseconds);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(statements.Count < 500);
        }
        while (cursor is not null);
        return new Walk(items, statements, milliseconds, firstPageBytes);
    }

    [SkippableFact]
    public async Task A_thousand_groups_are_read_complete_in_order_and_in_constant_statements_per_page()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        Guid partyId;
        await using (var db = NewContext()) partyId = await SeedPartyAsync(db, PartyStatuses.Live);
        var expected = await SeedThousandAsync(partyId);

        var walk = await WalkAsync(partyId, null, null, PartyGuestDirectoryLimits.DefaultTake);

        var groups = walk.Items.OfType<PartyGuestDirectoryGroupItemDto>().ToList();
        var others = walk.Items.OfType<PartyGuestDirectoryOtherItemDto>().ToList();
        Assert.Equal(1000, groups.Select(g => g.GroupId).Distinct().Count());
        Assert.Equal(1000, groups.Count);
        Assert.Equal(50, others.Select(o => o.Id).Distinct().Count());
        // Other arrivals first, latest first; then the groups in the database's own order.
        Assert.All(walk.Items.Take(50), i => Assert.IsType<PartyGuestDirectoryOtherItemDto>(i));
        Assert.Equal(others.OrderByDescending(o => o.CheckedInAt).Select(o => o.Id), others.Select(o => o.Id));
        await using (var db = NewContext())
        {
            var order = await db.PartyInvitationGroups.AsNoTracking()
                .Where(g => g.PartyId == partyId)
                .OrderBy(g => g.Label.ToLower()).ThenBy(g => g.Id)
                .Select(g => g.Id)
                .ToListAsync();
            Assert.Equal(order, groups.Select(g => g.GroupId));
        }
        Assert.All(groups, g => Assert.Equal(3, g.People.Count));

        // The same handful of statements for every page, however many groups
        // the party has: nothing per group, nothing per person.
        var later = walk.Statements.Skip(1).ToList();
        _output.WriteLine($"pages={walk.Statements.Count} statements first={walk.Statements[0]} later=[{string.Join(',', later.Distinct())}]");
        _output.WriteLine($"ms per page: max={walk.Milliseconds.Max()} median={walk.Milliseconds.Order().ElementAt(walk.Milliseconds.Count / 2)}");
        _output.WriteLine($"first page payload: {walk.FirstPageBytes} bytes for {PartyGuestDirectoryLimits.DefaultTake} items");
        Assert.InRange(walk.Statements[0], 1, 10);
        Assert.All(later, count => Assert.InRange(count, 1, 5));
        Assert.True(walk.FirstPageBytes < 64 * 1024, $"a first page of forty is {walk.FirstPageBytes} bytes");
        Assert.True(walk.Milliseconds.Max() < 5_000, $"a page took {walk.Milliseconds.Max()} ms");

        // The counts, against the fixture computed by hand.
        await using (var db = NewContext())
        {
            var summary = (await Directory(db).PageAsync(_ownerId, partyId, new PartyGuestDirectoryQuery(null, null, null, 0)))
                .Page!.Summary!;
            Assert.Equal(expected.Groups, summary.Groups);
            Assert.Equal(expected.Others, summary.OtherArrivals);
            Assert.Equal(expected.Named, summary.Rsvp.Invited);
            Assert.Equal(expected.Pending, summary.Rsvp.MissingResponses);
            Assert.Equal(expected.Attending, summary.Rsvp.Attending);
            Assert.Equal(expected.Declined, summary.Rsvp.Declined);
            Assert.Equal(expected.Unanswered, summary.Rsvp.UnansweredGroups);
            Assert.Equal(expected.Attending, summary.Attendance.ExpectedPeople);
            Assert.Equal(expected.ExpectedArrived, summary.Attendance.ExpectedArrived);
            Assert.Equal(expected.Attending - expected.ExpectedArrived, summary.Attendance.ExpectedMissing);
            Assert.Equal(expected.UnexpectedKnown, summary.Attendance.UnexpectedKnownGuests);
            Assert.Equal(expected.ExpectedArrived + expected.UnexpectedKnown + expected.Others, summary.Attendance.TotalArrivals);
        }

        // Search and filters, over the whole thousand, in the database.
        var elodie = await WalkAsync(partyId, "elodie", null, PartyGuestDirectoryLimits.MaxTake);
        Assert.Equal(expected.Accented, elodie.Items.Count);
        Assert.All(elodie.Statements.Skip(1), count => Assert.InRange(count, 1, 5));
        var toArrive = await WalkAsync(partyId, null, PartyGuestDirectoryStates.ToArrive, PartyGuestDirectoryLimits.MaxTake);
        Assert.Equal(expected.ToArrive, toArrive.Items.Count);
        Assert.All(toArrive.Items, i => Assert.IsType<PartyGuestDirectoryGroupItemDto>(i));
        var notInvited = await WalkAsync(partyId, null, PartyGuestDirectoryStates.NotInvited, PartyGuestDirectoryLimits.MaxTake);
        Assert.Equal(800, notInvited.Items.Count);
        var byNumber = await WalkAsync(partyId, "333 0000998", null, PartyGuestDirectoryLimits.MaxTake);
        Assert.Equal("Gruppo 0998", Assert.IsType<PartyGuestDirectoryGroupItemDto>(Assert.Single(byNumber.Items)).Label);
        var byPerson = await WalkAsync(partyId, "ospite 998-2", null, PartyGuestDirectoryLimits.MaxTake);
        var hit = Assert.IsType<PartyGuestDirectoryGroupItemDto>(Assert.Single(byPerson.Items));
        Assert.Equal(new[] { false, false, true }, hit.People.Select(p => p.Matched));
        _output.WriteLine($"search 'elodie': {elodie.Items.Count} items, ms max={elodie.Milliseconds.Max()}");
    }

    // --- The share ledger's race ------------------------------------------------------------

    [SkippableFact]
    public async Task Two_copies_of_one_share_click_record_one_row_and_hand_back_one_link()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        Guid partyId, groupId;
        await using (var db = NewContext())
        {
            partyId = await SeedPartyAsync(db, PartyStatuses.Published);
            var (capabilityId, tokenHash) = _tokens.Mint();
            groupId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.PartyInvitationGroups.Add(new PartyInvitationGroup
            {
                Id = groupId, PartyId = partyId, Label = "Sara", RecipientEmail = "sara@example.com",
                Phone = "+39 333 123 4567", SearchText = PartySearchText.ForGroup("Sara", "sara@example.com", "+39 333 123 4567"),
                CapabilityId = capabilityId, TokenHash = tokenHash, CapabilityIssuedAt = now, Version = 1,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        var click = Guid.NewGuid();
        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PartyInvitationShareResult> Share(AppDbContext db)
        {
            await gate.Task;
            return await Delivery(db).ShareAsync(_ownerId, partyId, groupId, "whatsapp", click, null);
        }

        var first = Share(firstDb);
        var second = Share(secondDb);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(PartyInvitationOutcome.Ok, r.Outcome));
        Assert.Single(results, r => !r.Share!.Replayed);
        Assert.Equal(results[0].Share!.Url, results[1].Share!.Url);
        Assert.StartsWith("https://wa.me/393331234567?text=", results[0].Share!.WhatsappUrl);
        await using var verify = NewContext();
        var row = await verify.PartyInvitationDeliveries.SingleAsync();
        Assert.Equal(PartyInvitationDeliveryStatuses.Shared, row.Status);
        Assert.Equal(PartyInvitationDeliveryChannels.WhatsApp, row.Channel);
        Assert.Empty(_email.Messages);
    }

    // --- The schema is the authority -------------------------------------------------------

    [SkippableFact]
    public async Task The_migration_applied_and_the_database_holds_the_ledgers_new_rules()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var db = NewContext();
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_GeneralizePartyInvitationSharing"));

        var partyId = await SeedPartyAsync(db, PartyStatuses.Published);
        var groupId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        // Written as an application that knows neither new column writes it.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO party_invitation_groups (\"Id\", \"PartyId\", \"Label\", \"RecipientEmail\", \"MaxAdditionalGuests\", "
            + "\"CapabilityId\", \"TokenHash\", \"CapabilityIssuedAt\", \"Version\", \"CreatedAt\", \"UpdatedAt\") "
            + "VALUES ({0}, {1}, 'Famiglia Été', 'ete@example.com', 0, {2}, {3}, now(), 1, now(), now())",
            groupId, partyId, capabilityId, new string('a', 64));
        const string legacyInsert =
            "INSERT INTO party_invitation_deliveries "
            + "(\"Id\", \"PartyInvitationGroupId\", \"ClientRequestId\", \"CapabilityId\", \"Kind\", \"Status\", \"CreatedAt\", \"CompletedAt\") "
            + "VALUES ({0}, {1}, {2}, {3}, 'initial', 'sent', now(), now())";
        await db.Database.ExecuteSqlRawAsync(legacyInsert, Guid.NewGuid(), groupId, Guid.NewGuid(), capabilityId);
        Assert.Equal(PartyInvitationDeliveryChannels.Email, (await db.PartyInvitationDeliveries.SingleAsync()).Channel);
        Assert.Equal(string.Empty, (await db.PartyInvitationGroups.SingleAsync(g => g.Id == groupId)).SearchText);

        const string insert =
            "INSERT INTO party_invitation_deliveries "
            + "(\"Id\", \"PartyInvitationGroupId\", \"ClientRequestId\", \"CapabilityId\", \"Channel\", \"Kind\", \"Status\", \"CreatedAt\", \"CompletedAt\") "
            + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, now(), now())";
        async Task Refused(string channel, string kind, string status)
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                insert, Guid.NewGuid(), groupId, Guid.NewGuid(), capabilityId, channel, kind, status));
            Assert.Equal("23514", refused.SqlState);
        }
        await db.Database.ExecuteSqlRawAsync(
            insert, Guid.NewGuid(), groupId, Guid.NewGuid(), capabilityId, "whatsapp", "initial", "shared");
        await db.Database.ExecuteSqlRawAsync(
            insert, Guid.NewGuid(), groupId, Guid.NewGuid(), capabilityId, "copy", "resend", "shared");
        await Refused("sms", "initial", "shared");
        // A share is never "sent", and an email is never "shared".
        await Refused("whatsapp", "initial", "sent");
        await Refused("email", "initial", "shared");
        // A reminder is an email.
        await Refused("whatsapp", "reminder", "shared");

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'party_attendance_guests'")
            .ToListAsync();
        Assert.Contains("ix_party_attendance_guests_party_checked_in", indexes);

        // The reconciler folds the row the old writer left empty, exactly as the application would.
        Assert.True(await PartySearchTextReconciler.RunAsync(db) >= 1);
        Assert.Equal(
            PartySearchText.ForGroup("Famiglia Été", "ete@example.com", null),
            (await db.PartyInvitationGroups.AsNoTracking().SingleAsync(g => g.Id == groupId)).SearchText);
        var found = await Directory(db).PageAsync(_ownerId, partyId, new PartyGuestDirectoryQuery("famiglia ete", null, null, null));
        Assert.Equal(groupId, Assert.IsType<PartyGuestDirectoryGroupItemDto>(Assert.Single(found.Page!.Items)).GroupId);
    }

    // --- Plumbing ---------------------------------------------------------------------------

    /// <summary>Counts every statement a context sends, so "constant per page" is measured, not assumed.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

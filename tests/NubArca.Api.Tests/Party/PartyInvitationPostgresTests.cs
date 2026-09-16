using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The guest list's races, on real PostgreSQL.
///
/// <para>SQLite serialises every writer behind one database lock, so it can
/// only ever show two replies taking turns. PostgreSQL takes ROW locks, which is
/// what production does: here a reply genuinely reaches the group row while
/// another writer holds it, genuinely waits, and is refused for the right
/// reason — the version it quoted is gone — rather than because the whole file
/// was busy. The same for two copies of one send click meeting at the unique
/// index. The database is the final authority in both, and these tests make it
/// prove it.</para>
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyInvitationPostgresTests : IAsyncLifetime
{
    private const string Origin = "https://cloud.example.com";

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _marioId = Guid.NewGuid();
    private readonly Guid _lauraId = Guid.NewGuid();
    private readonly Guid _questionId = Guid.NewGuid();
    private readonly PartyInvitationTokens _tokens = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PartyInvitationTokens.InvitationSecretKey] = "pg-test-invitation-secret",
        })
        .Build());
    private readonly RecordingEmailSender _email = new();
    private Guid _capabilityId;

    public PartyInvitationPostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        var (capabilityId, tokenHash) = _tokens.Mint();
        _capabilityId = capabilityId;

        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com", DisplayName = "Owner", CreatedAt = now,
        });
        // Published, and deliberately with no album: a guest list needs none.
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = _partyId, OwnerUserId = _ownerId, Title = "Matrimonio di Marta",
            Status = PartyStatuses.Published, Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        db.PartyInvitationGroups.Add(new PartyInvitationGroup
        {
            Id = _groupId, PartyId = _partyId, Label = "Mario e Laura", RecipientEmail = "mario@example.com",
            MaxAdditionalGuests = 1, CapabilityId = capabilityId, TokenHash = tokenHash,
            CapabilityIssuedAt = now, Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        foreach (var (id, name, order) in new[] { (_marioId, "Mario", 0), (_lauraId, "Laura", 1) })
        {
            db.PartyGuests.Add(new PartyGuest
            {
                Id = id, PartyInvitationGroupId = _groupId, Name = name, SortOrder = order,
                CreatedAt = now, UpdatedAt = now,
            });
            db.PartyRsvps.Add(new PartyRsvp { PartyGuestId = id, Status = PartyRsvpStatuses.Pending, UpdatedAt = now });
        }
        db.PartyRsvpQuestions.Add(new PartyRsvpQuestion
        {
            Id = _questionId, PartyId = _partyId, Prompt = "Carne o pesce?",
            Kind = PartyRsvpQuestionKinds.SingleChoice, Required = true,
            OptionsJson = PartyRsvpQuestionRules.SerializeOptions(PartyRsvpQuestionKinds.SingleChoice, ["Carne", "Pesce"]),
            IsActive = true, SortOrder = 0, Version = 1, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext NewContext() => new(_dbOptions!);

    private string Token => _tokens.Derive(_capabilityId);

    private static PartyRsvpService Rsvp(AppDbContext db) =>
        new(db, TimeProvider.System, new FixedPartyCapabilityPolicy(),
            new PartyGuestContentService(db, TimeProvider.System), new PartyMediaService(db),
            new PartyLinkService(
                db, TimeProvider.System,
                new PartyService(db, TimeProvider.System, new PartyStateEraser(db), null!),
                new FixedPartyCapabilityPolicy(), new ConfigurationBuilder().Build()));

    private PartyInvitationService Invitations(AppDbContext db) =>
        new(db, TimeProvider.System, _tokens, _email, Mail());

    private PartyInvitationDeliveryService Delivery(AppDbContext db) =>
        new(db, TimeProvider.System,
            // Teardown is not exercised here, so the file lifecycle it would use is absent.
            new PartyService(db, TimeProvider.System, new PartyStateEraser(db), null!),
            Invitations(db),
            new PartyGuestDirectoryService(
                db, _email, Mail(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
            _tokens, _email, Mail(), NullLogger<PartyInvitationDeliveryService>.Instance);

    private static StaticOptionsMonitor<MailOptions> Mail() =>
        new(new MailOptions { Enabled = true, FromAddress = "party@example.com", PublicOrigin = Origin });

    private PartyRsvpWrite Everyone(string status, string? menu) => new(
        1,
        [new PartyRsvpGuestWrite(_marioId, status, null), new PartyRsvpGuestWrite(_lauraId, status, null)],
        status == PartyRsvpStatuses.Attending ? [new PartyRsvpAdditionalGuestWrite(null, "Giulia", null)] : [],
        menu is null ? [] : [new PartyRsvpAnswerWrite(_questionId, JsonDocument.Parse($"\"{menu}\"").RootElement)]);

    // --- Concurrent replies -----------------------------------------------------

    [SkippableFact]
    public async Task Two_replies_quoting_one_version_leave_exactly_one_whole_graph()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PartyRsvpResult> Reply(AppDbContext db, PartyRsvpWrite body)
        {
            await gate.Task;
            return await Rsvp(db).SubmitAsync(Token, body);
        }

        var coming = Reply(firstDb, Everyone(PartyRsvpStatuses.Attending, "Carne"));
        var notComing = Reply(secondDb, Everyone(PartyRsvpStatuses.Declined, null));
        gate.SetResult();
        var results = await Task.WhenAll(coming, notComing);

        var winner = Assert.Single(results, r => r.Outcome == PartyRsvpOutcome.Ok);
        var loser = Assert.Single(results, r => r.Outcome == PartyRsvpOutcome.VersionConflict);
        // The loser is shown the winner's reply, at the version it produced.
        Assert.Equal(2, loser.View!.Invitation.Version);

        await using var verify = NewContext();
        Assert.Equal(2, (await verify.PartyInvitationGroups.SingleAsync()).Version);
        var statuses = await verify.PartyRsvps.Select(r => r.Status).Distinct().ToListAsync();
        var winnerCame = winner.View!.Invitation.Guests.Any(g => g.Status == PartyRsvpStatuses.Attending);
        // ONE graph: every person, the +1 and the answer all belong to the same
        // reply — never Mario from one and Laura from the other.
        Assert.Equal(
            [winnerCame ? PartyRsvpStatuses.Attending : PartyRsvpStatuses.Declined], statuses);
        Assert.Equal(winnerCame ? 1 : 0, await verify.PartyGuests.CountAsync(g => g.IsAdditionalGuest));
        Assert.Equal(winnerCame ? 1 : 0, await verify.PartyRsvpAnswers.CountAsync());
    }

    [SkippableFact]
    public async Task A_reply_that_meets_an_in_flight_writer_waits_and_then_writes_nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // Another writer — an owner's edit, say — is genuinely IN FLIGHT: it has
        // spent the version and holds the group's row lock, uncommitted.
        await using var holder = NewContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.PartyInvitationGroups
            .Where(g => g.Id == _groupId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Version, g => g.Version + 1));

        // The guest quotes version 1, which is still all anybody outside that
        // transaction can see — so it passes every read and reaches the row.
        await using var guestDb = NewContext();
        var reply = Rsvp(guestDb).SubmitAsync(Token, Everyone(PartyRsvpStatuses.Attending, "Pesce"));
        var first = await Task.WhenAny(reply, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(reply, first); // waiting on the row lock, not finished

        await transaction.CommitAsync();
        var result = await reply;

        Assert.Equal(PartyRsvpOutcome.VersionConflict, result.Outcome);
        await using var verify = NewContext();
        Assert.All(await verify.PartyRsvps.ToListAsync(), r => Assert.Equal(PartyRsvpStatuses.Pending, r.Status));
        Assert.Empty(await verify.PartyRsvpAnswers.ToListAsync());
        Assert.Equal(0, await verify.PartyGuests.CountAsync(g => g.IsAdditionalGuest));
        Assert.Equal(2, (await verify.PartyInvitationGroups.SingleAsync()).Version);
    }

    // --- One click, one email ---------------------------------------------------

    [SkippableFact]
    public async Task Two_copies_of_one_send_click_record_one_delivery_and_send_one_email()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var click = Guid.NewGuid();

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PartyInvitationSendResult> Send(AppDbContext db)
        {
            await gate.Task;
            return await Delivery(db).SendAsync(_ownerId, _partyId, _groupId, click, null);
        }

        var first = Send(firstDb);
        var second = Send(secondDb);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(PartyInvitationOutcome.Ok, r.Outcome));
        Assert.Single(results, r => !r.Delivery!.Replayed);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PartyInvitationDeliveries.CountAsync());
        Assert.Single(_email.Messages);
    }

    [SkippableFact]
    public async Task A_send_that_meets_an_uncommitted_copy_of_its_click_waits_and_sends_nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var click = Guid.NewGuid();

        // The other copy of the click has recorded its row and not committed.
        await using var holder = NewContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        holder.PartyInvitationDeliveries.Add(new PartyInvitationDelivery
        {
            Id = Guid.NewGuid(), PartyInvitationGroupId = _groupId, ClientRequestId = click,
            CapabilityId = _capabilityId, Kind = PartyInvitationDeliveryKinds.Initial,
            Status = PartyInvitationDeliveryStatuses.Pending, CreatedAt = DateTime.UtcNow,
        });
        await holder.SaveChangesAsync();

        // This copy cannot see it, so it tries to record its own — and the
        // unique index makes it WAIT for the first one's outcome.
        await using var senderDb = NewContext();
        var send = Delivery(senderDb).SendAsync(_ownerId, _partyId, _groupId, click, null);
        var first = await Task.WhenAny(send, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.NotSame(send, first);

        await transaction.CommitAsync();
        var result = await send;

        // The committed row is the answer. No email left this process.
        Assert.Equal(PartyInvitationOutcome.Ok, result.Outcome);
        Assert.True(result.Delivery!.Replayed);
        Assert.Equal(PartyInvitationDeliveryStatuses.Pending, result.Delivery.Status);
        Assert.Empty(_email.Messages);
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PartyInvitationDeliveries.CountAsync());
    }

    // --- Rotation -----------------------------------------------------------------

    [SkippableFact]
    public async Task A_committed_rotation_kills_the_old_link_and_opens_the_new_one()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var old = Token;

        await using (var db = NewContext())
        {
            Assert.NotNull(await Rsvp(db).ResolveAsync(old));
            var rotated = await Invitations(db).RotateLinkAsync(_ownerId, _partyId, _groupId, 1);
            Assert.Equal(PartyInvitationOutcome.Ok, rotated.Outcome);
        }

        await using var verify = NewContext();
        var group = await verify.PartyInvitationGroups.SingleAsync();
        Assert.NotEqual(_capabilityId, group.CapabilityId);
        Assert.Null(await Rsvp(verify).ResolveAsync(old));
        var fresh = await Rsvp(verify).ResolveAsync(_tokens.Derive(group.CapabilityId));
        Assert.Equal(_groupId, fresh!.GroupId);
    }

    // --- The schema is the authority ----------------------------------------------

    [SkippableFact]
    public async Task The_migration_applied_and_the_database_holds_every_rule()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var db = NewContext();

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_AddPartyGuestListRsvp"));

        // The two columns holding serialized JSON are TEXT: the domain bounds
        // what they hold, never the length of its escaped form.
        var jsonColumns = await db.Database
            .SqlQueryRaw<string>(
                "SELECT table_name || '.' || column_name || '=' || data_type AS \"Value\" "
                + "FROM information_schema.columns "
                + "WHERE (table_name = 'party_rsvp_answers' AND column_name = 'ValueJson') "
                + "OR (table_name = 'party_rsvp_questions' AND column_name = 'OptionsJson')")
            .ToListAsync();
        Assert.Equal(
            new[] { "party_rsvp_answers.ValueJson=text", "party_rsvp_questions.OptionsJson=text" },
            jsonColumns.Order());

        // And every foreign key of the six tables still RESTRICTS: what a party
        // owns is erased out loud, never by a cascade.
        var deleteRules = await db.Database
            .SqlQueryRaw<string>(
                "SELECT c.confdeltype::text AS \"Value\" FROM pg_constraint c "
                + "JOIN pg_class t ON t.oid = c.conrelid WHERE c.contype = 'f' AND t.relname IN "
                + "('party_invitation_groups', 'party_guests', 'party_rsvps', 'party_rsvp_questions', "
                + "'party_rsvp_answers', 'party_invitation_deliveries')")
            .ToListAsync();
        Assert.Equal(7, deleteRules.Count);
        Assert.All(deleteRules, rule => Assert.Equal("r", rule));

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename LIKE 'party_%'")
            .ToListAsync();
        foreach (var name in new[]
        {
            "ux_party_invitation_groups_token_hash", "ix_party_invitation_groups_party_created",
            "ix_party_guests_group_order", "ix_party_rsvp_questions_party_order",
            "ix_party_rsvp_answers_question", "ux_party_invitation_deliveries_request",
            "ix_party_invitation_deliveries_group_created",
        })
        {
            Assert.Contains(name, indexes);
        }

        var checks = await db.Database
            .SqlQueryRaw<string>("SELECT conname AS \"Value\" FROM pg_constraint WHERE contype = 'c'")
            .ToListAsync();
        foreach (var name in new[]
        {
            "ck_party_invitation_groups_max_additional", "ck_party_invitation_groups_token_hash",
            "ck_party_rsvps_status", "ck_party_rsvp_questions_kind", "ck_party_rsvp_questions_options",
            "ck_party_invitation_deliveries_kind", "ck_party_invitation_deliveries_status",
            "ck_party_invitation_deliveries_completion",
        })
        {
            Assert.Contains(name, checks);
        }

        // And they are enforced, not merely declared.
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_rsvps SET \"Status\" = 'maybe' WHERE \"PartyGuestId\" = {0}", _marioId);
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_invitation_groups SET \"TokenHash\" = 'abc' WHERE \"Id\" = {0}", _groupId);
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_invitation_groups SET \"MaxAdditionalGuests\" = 11 WHERE \"Id\" = {0}", _groupId);
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_rsvp_questions SET \"OptionsJson\" = NULL WHERE \"Id\" = {0}", _questionId);
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_rsvp_questions SET \"Kind\" = 'multiple_choice' WHERE \"Id\" = {0}", _questionId);

        const string insertDelivery =
            "INSERT INTO party_invitation_deliveries "
            + "(\"Id\", \"PartyInvitationGroupId\", \"ClientRequestId\", \"CapabilityId\", \"Kind\", \"Status\", \"CreatedAt\") "
            + "VALUES ({0}, {1}, {2}, {3}, 'initial', 'pending', now())";
        var click = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(insertDelivery, Guid.NewGuid(), _groupId, click, _capabilityId);
        await AssertRefusedAsync(db, "23505", insertDelivery, Guid.NewGuid(), _groupId, click, _capabilityId);
        await AssertRefusedAsync(db, "23514",
            "UPDATE party_invitation_deliveries SET \"Status\" = 'sent' WHERE \"ClientRequestId\" = {0}", click);
    }

    // --- Unicode against the storage contract ------------------------------------

    private PartyRsvpWrite Coming(int version, Guid extraQuestion, JsonElement extraAnswer) => new(
        version,
        [new PartyRsvpGuestWrite(_marioId, PartyRsvpStatuses.Attending, null),
         new PartyRsvpGuestWrite(_lauraId, PartyRsvpStatuses.Declined, null)],
        [],
        [new PartyRsvpAnswerWrite(_questionId, JsonDocument.Parse("\"Carne\"").RootElement),
         new PartyRsvpAnswerWrite(extraQuestion, extraAnswer)]);

    private static JsonElement JsonString(string value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    [SkippableFact]
    public async Task Five_hundred_emoji_are_stored_whole_and_the_five_hundred_and_first_is_refused_first()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var dedication = Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.PartyRsvpQuestions.Add(new PartyRsvpQuestion
            {
                Id = dedication, PartyId = _partyId, Prompt = "Una dedica?", Kind = PartyRsvpQuestionKinds.ShortText,
                IsActive = true, SortOrder = 1, Version = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var text = string.Concat(Enumerable.Repeat("🎉", PartyInvitationLimits.MaxShortTextAnswerLength));

        await using (var db = NewContext())
        {
            var saved = await Rsvp(db).SubmitAsync(Token, Coming(1, dedication, JsonString(text)));
            Assert.Equal(PartyRsvpOutcome.Ok, saved.Outcome);
            Assert.Equal(text, saved.View!.Invitation.Questions.Single(q => q.Id == dedication).Answer!.Value.GetString());
        }

        await using (var verify = NewContext())
        {
            var stored = await verify.PartyRsvpAnswers.SingleAsync(a => a.PartyRsvpQuestionId == dedication);
            // Its escaped JSON is longer than any length a column could have
            // guessed for it — and it is stored whole.
            Assert.True(stored.ValueJson.Length > 2048);
            Assert.Equal(text, JsonSerializer.Deserialize<string>(stored.ValueJson));
            var reread = await Rsvp(verify).ViewAsync((await Rsvp(verify).ResolveAsync(Token))!, Token);
            Assert.Equal(text, reread.Invitation.Questions.Single(q => q.Id == dedication).Answer!.Value.GetString());
        }

        // One more code point is the DOMAIN's refusal, before anything is written.
        await using (var db = NewContext())
        {
            var refused = await Rsvp(db).SubmitAsync(Token, Coming(2, dedication, JsonString(text + "🎉")));
            Assert.Equal(PartyRsvpOutcome.InvalidRequest, refused.Outcome);
            Assert.Equal("invalid_answer", refused.Error);
        }
        await using (var after = NewContext())
        {
            Assert.Equal(2, (await after.PartyInvitationGroups.SingleAsync()).Version);
            Assert.Equal(text, JsonSerializer.Deserialize<string>(
                (await after.PartyRsvpAnswers.SingleAsync(a => a.PartyRsvpQuestionId == dedication)).ValueJson));
        }
    }

    [SkippableFact]
    public async Task Twenty_options_of_a_hundred_and_twenty_code_points_are_stored_whole_and_answerable()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var options = Enumerable.Range(0, PartyInvitationLimits.MaxOptions)
            .Select(i => $"{i:D2}" + string.Concat(Enumerable.Repeat("🎉", PartyInvitationLimits.MaxOptionLength - 2)))
            .ToArray();
        Assert.All(options, o => Assert.Equal(PartyInvitationLimits.MaxOptionLength, PartyInvitationLimits.CodePoints(o)));

        Guid questionId;
        await using (var db = NewContext())
        {
            var created = await Invitations(db).CreateQuestionAsync(
                _ownerId, _partyId,
                new PartyRsvpQuestionWrite("Quale festa?", PartyRsvpQuestionKinds.SingleChoice, false, options));
            Assert.Equal(PartyInvitationOutcome.Ok, created.Outcome);
            var question = created.GuestList!.Questions.Single(q => q.Prompt == "Quale festa?");
            Assert.Equal(options, question.Options);
            questionId = question.Id;
        }

        await using (var verify = NewContext())
        {
            var stored = await verify.PartyRsvpQuestions.SingleAsync(q => q.Id == questionId);
            Assert.True(stored.OptionsJson!.Length > 8192);
            Assert.Equal(options, PartyRsvpQuestionRules.ParseOptions(stored.OptionsJson));

            var answered = await Rsvp(verify).SubmitAsync(Token, Coming(1, questionId, JsonString(options[7])));
            Assert.Equal(PartyRsvpOutcome.Ok, answered.Outcome);
            Assert.Equal(options[7], answered.View!.Invitation.Questions.Single(q => q.Id == questionId).Answer!.Value.GetString());
        }
    }

    private static async Task AssertRefusedAsync(AppDbContext db, string sqlState, string sql, params object[] args)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, args));
        Assert.Equal(sqlState, refused.SqlState);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

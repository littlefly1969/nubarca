using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Invitation emails, and the ledger that makes one click one email.
///
/// <para>Every test here reads what WOULD have been sent from the recording
/// sender, so none opens a socket. The rules pinned down: the first send
/// publishes a Draft through the lifecycle and never starts the party; a request
/// id is spent once, whatever happens to the email; a failed or unconfirmed
/// delivery is shown as exactly that and changes no answer; a reminder reaches
/// only a group that was invited on the link it holds and has not replied; and
/// nothing is sent once the party is under way.</para>
/// </summary>
public sealed class PartyInvitationDeliveryTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_first_invitation_publishes_a_draft_and_emails_one_personal_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Matrimonio di Marta");
        var groupId = (await AddGroupAsync(owner, partyId, "Mario e Laura", "mario@example.com", 0, "Mario", "Laura"))
            .GetProperty("id").GetGuid();

        var response = await SendAsync(owner, partyId, groupId, partyVersion: 1);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var delivery = body.GetProperty("delivery");
        Assert.Equal("initial", delivery.GetProperty("kind").GetString());
        Assert.Equal("sent", delivery.GetProperty("status").GetString());
        Assert.False(delivery.GetProperty("replayed").GetBoolean());
        // Published through the lifecycle, with the version the page must adopt.
        Assert.Equal(PartyStatuses.Published, body.GetProperty("party").GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("party").GetProperty("version").GetInt32());
        var row = Group(body.GetProperty("guestList"), "Mario e Laura");
        Assert.Equal("sent", row.GetProperty("delivery").GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("delivery").GetProperty("lastSentAt").ValueKind);

        var message = Assert.Single(_factory.EmailSender.Messages);
        Assert.Equal("mario@example.com", message.ToAddress);
        Assert.Contains("Matrimonio di Marta", message.Subject);
        Assert.Contains($"{Origin}/party/invite/", message.TextBody);
        // The link in the email opens exactly this group.
        var view = await ViewAsync(_factory.CreateClient(), TokenFrom(message));
        Assert.Equal("Mario e Laura", view.GetProperty("invitation").GetProperty("label").GetString());
        // Announcing is not starting.
        Assert.Equal(PartyStatuses.Published, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = await db.PartyInvitationDeliveries.SingleAsync();
        Assert.Equal(PartyInvitationDeliveryStatuses.Sent, ledger.Status);
        Assert.NotNull(ledger.CompletedAt);
        Assert.Equal((await db.PartyInvitationGroups.SingleAsync()).CapabilityId, ledger.CapabilityId);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PartyInvitationSend);
        Assert.Contains("initial", audit.MetadataJson);
        Assert.Contains("sent", audit.MetadataJson);
        foreach (var leak in new[] { "mario@example.com", "Mario", TokenFrom(message), "/party/invite/" })
        {
            Assert.DoesNotContain(leak, audit.MetadataJson);
        }
    }

    [Fact]
    public async Task A_send_from_a_stale_read_of_a_draft_publishes_nothing_and_sends_nothing()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();

        foreach (int? version in new int?[] { 99, null })
        {
            var response = await SendAsync(owner, partyId, groupId, partyVersion: version);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("party_version_conflict", body.GetProperty("error").GetString());
            Assert.Equal(PartyStatuses.Draft, body.GetProperty("party").GetProperty("status").GetString());
        }

        Assert.Empty(_factory.EmailSender.Messages);
        Assert.Equal(PartyStatuses.Draft, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyInvitationDeliveries.ToListAsync());
    }

    [Fact]
    public async Task One_click_sends_one_email_however_often_it_arrives()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var click = Guid.NewGuid();

        var first = await (await SendAsync(owner, partyId, groupId, click, partyVersion: 1))
            .Content.ReadFromJsonAsync<JsonElement>();
        // The retry of the same click — the party has moved on since, and the
        // answer is still the first one.
        var retry = await SendAsync(owner, partyId, groupId, click, partyVersion: 1);
        retry.EnsureSuccessStatusCode();
        var replayed = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivery");
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal("initial", replayed.GetProperty("kind").GetString());
        Assert.Equal(
            first.GetProperty("delivery").GetProperty("createdAt").GetDateTime(),
            replayed.GetProperty("createdAt").GetDateTime());
        Assert.Single(_factory.EmailSender.Messages);

        // A NEW click is a new attempt — a resend of the same link.
        var resend = await SendAsync(owner, partyId, groupId);
        Assert.Equal("resend", (await resend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("delivery").GetProperty("kind").GetString());
        Assert.Equal(2, _factory.EmailSender.Messages.Count);
        Assert.Equal(TokenFrom(_factory.EmailSender.Messages[0]), TokenFrom(_factory.EmailSender.Messages[1]));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.PartyInvitationDeliveries.CountAsync());
        // A replay sent nothing, so it recorded nothing either.
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PartyInvitationSend));
    }

    [Fact]
    public async Task A_refused_delivery_is_recorded_as_failed_and_touches_no_answer()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var marioId = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 0, "Mario")).GetProperty("id").GetGuid();
        var saraId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, marioId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token), new Dictionary<string, string> { ["Mario"] = "attending" })))
            .EnsureSuccessStatusCode();

        _factory.EmailSender.FailDelivery = true;
        var resend = await SendAsync(owner, partyId, marioId);
        resend.EnsureSuccessStatusCode();
        var body = await resend.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", body.GetProperty("delivery").GetProperty("status").GetString());
        var mario = Group(body.GetProperty("guestList"), "Mario");
        // The invitation that DID arrive still stands; the last attempt says it failed.
        Assert.Equal("sent", mario.GetProperty("delivery").GetProperty("state").GetString());
        Assert.Equal("failed", mario.GetProperty("delivery").GetProperty("lastAttemptStatus").GetString());
        Assert.Equal("attending", OwnerGuest(mario, "Mario").GetProperty("status").GetString());
        Assert.Equal("attending", GuestRow(await ViewAsync(guest, token), "Mario").GetProperty("status").GetString());

        var never = await SendAsync(owner, partyId, saraId);
        var sara = Group((await never.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("guestList"), "Sara");
        Assert.Equal("failed", sara.GetProperty("delivery").GetProperty("state").GetString());
        Assert.True(sara.GetProperty("canSend").GetBoolean());
        Assert.Equal("pending", OwnerGuest(sara, "Sara").GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.All(
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyInvitationDeliveries
                .Where(d => d.Status == PartyInvitationDeliveryStatuses.Failed).ToListAsync(),
            d => Assert.NotNull(d.CompletedAt));
    }

    [Fact]
    public async Task An_unconfirmed_attempt_is_shown_as_unconfirmed_and_its_retry_sends_nothing()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var click = Guid.NewGuid();

        // A process that died after SMTP accepted the message and before the
        // outcome was written leaves exactly this behind.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var group = await db.PartyInvitationGroups.SingleAsync();
            db.PartyInvitationDeliveries.Add(new PartyInvitationDelivery
            {
                Id = Guid.NewGuid(), PartyInvitationGroupId = groupId, ClientRequestId = click,
                CapabilityId = group.CapabilityId, Kind = PartyInvitationDeliveryKinds.Initial,
                Status = PartyInvitationDeliveryStatuses.Pending, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var uncertain = Group(await GuestListAsync(owner, partyId), "Sara");
        Assert.Equal("pending", uncertain.GetProperty("delivery").GetProperty("state").GetString());

        var retry = await SendAsync(owner, partyId, groupId, click);
        retry.EnsureSuccessStatusCode();
        var replayed = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivery");
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal("pending", replayed.GetProperty("status").GetString());
        Assert.Empty(_factory.EmailSender.Messages);

        // Pressing resend is a new decision, and it sends.
        var resend = await SendAsync(owner, partyId, groupId);
        Assert.Equal("initial", (await resend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("delivery").GetProperty("kind").GetString());
        Assert.Single(_factory.EmailSender.Messages);
        Assert.Equal("sent", Group(await GuestListAsync(owner, partyId), "Sara")
            .GetProperty("delivery").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_reminder_reaches_only_an_invited_group_that_has_not_answered()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var waiting = (await AddGroupAsync(owner, partyId, "Famiglia Bianchi", "bianchi@example.com", 0, "Paolo", "Marta"))
            .GetProperty("id").GetGuid();
        var answered = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var never = (await AddGroupAsync(owner, partyId, "Nico", "nico@example.com", 0, "Nico")).GetProperty("id").GetGuid();

        async Task AssertRefused(Guid groupId)
        {
            var response = await SendAsync(owner, partyId, groupId, action: "remind");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("reminder_not_allowed", await ErrorOf(response));
        }

        // Before any invitation there is nothing to remind anybody of.
        await AssertRefused(waiting);

        var waitingToken = await InviteAsync(_factory, owner, partyId, waiting);
        var saraToken = await InviteAsync(_factory, owner, partyId, answered);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, saraToken, Reply(
            await ViewAsync(guest, saraToken), new Dictionary<string, string> { ["Sara"] = "declined" })))
            .EnsureSuccessStatusCode();

        await AssertRefused(answered);
        await AssertRefused(never);

        var reminded = await SendAsync(owner, partyId, waiting, action: "remind");
        reminded.EnsureSuccessStatusCode();
        var delivery = (await reminded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivery");
        Assert.Equal("reminder", delivery.GetProperty("kind").GetString());
        Assert.Equal("sent", delivery.GetProperty("status").GetString());
        var message = _factory.EmailSender.Last!;
        Assert.Equal("bianchi@example.com", message.ToAddress);
        Assert.StartsWith("Promemoria", message.Subject);
        Assert.Equal(waitingToken, TokenFrom(message));

        // A reminder is an email, never an answer.
        var list = await GuestListAsync(owner, partyId);
        Assert.All(Group(list, "Famiglia Bianchi").GetProperty("guests").EnumerateArray(),
            g => Assert.Equal("pending", g.GetProperty("status").GetString()));
        Assert.True(Group(list, "Famiglia Bianchi").GetProperty("canRemind").GetBoolean());
        Assert.False(Group(list, "Sara").GetProperty("canRemind").GetBoolean());
        Assert.False(Group(list, "Nico").GetProperty("canRemind").GetBoolean());

        // A reminder carrying a link that no longer opens would remind nobody.
        (await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{waiting}/rotate-link",
            new { version = Group(list, "Famiglia Bianchi").GetProperty("version").GetInt32() }))
            .EnsureSuccessStatusCode();
        await AssertRefused(waiting);

        Assert.Equal(3, _factory.EmailSender.Messages.Count);
    }

    [Fact]
    public async Task Invitations_stop_once_the_party_is_under_way()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Nico", "nico@example.com", 0, "Nico")).GetProperty("id").GetGuid();
        await InviteAsync(_factory, owner, partyId, groupId);

        foreach (var action in new[] { "start-live", "end-live" })
        {
            await AdvanceAsync(owner, partyId, action);
            foreach (var kind in new[] { "send", "remind" })
            {
                var response = await SendAsync(owner, partyId, groupId, action: kind);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("invitations_closed", await ErrorOf(response));
            }
            var row = Group(await GuestListAsync(owner, partyId), "Nico");
            Assert.False(row.GetProperty("canSend").GetBoolean());
            Assert.False(row.GetProperty("canRemind").GetBoolean());
        }
        Assert.Single(_factory.EmailSender.Messages);
    }

    [Fact]
    public async Task Without_a_mailer_the_host_is_told_and_nothing_moves()
    {
        // No public origin configured: there is nothing to build a link on.
        using var plain = new SqliteWebApplicationFactory();
        plain.EnsureDatabaseCreated();
        var (_, owner) = await NewHostAsync(plain);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Nico", "nico@example.com", 0, "Nico")).GetProperty("id").GetGuid();

        var list = await GuestListAsync(owner, partyId);
        Assert.False(list.GetProperty("mailAvailable").GetBoolean());
        Assert.False(Group(list, "Nico").GetProperty("canSend").GetBoolean());

        var response = await SendAsync(owner, partyId, groupId, partyVersion: 1);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("mail_unavailable", await ErrorOf(response));
        // Not even the publication an actual send would have made.
        Assert.Equal(PartyStatuses.Draft, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());
        Assert.Empty(plain.EmailSender.Messages);
        using (var scope = plain.Services.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyInvitationDeliveries.ToListAsync());
        }

        // A switched-off mailer is the same answer, origin or not.
        var (_, host) = await NewHostAsync(_factory);
        var otherParty = await CreatePartyAsync(host);
        var otherGroup = (await AddGroupAsync(host, otherParty, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        _factory.EmailSender.Enabled = false;
        var off = await SendAsync(host, otherParty, otherGroup, partyVersion: 1);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("mail_unavailable", await ErrorOf(off));
    }

    [Fact]
    public async Task The_invitation_speaks_the_hosts_language()
    {
        var (hostId, owner) = await NewHostAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.SingleAsync(u => u.Id == hostId)).UiLanguage = "en";
            await db.SaveChangesAsync();
        }
        var partyId = await CreatePartyAsync(owner, "Marta's wedding");
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();

        await InviteAsync(_factory, owner, partyId, groupId);

        Assert.Equal("You're invited: Marta's wedding", _factory.EmailSender.Last!.Subject);
    }
}

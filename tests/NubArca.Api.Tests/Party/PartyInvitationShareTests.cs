using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// WhatsApp and Copia link: the group's own personal link, handed to the host.
///
/// <para>The rules pinned down: a share hands over the CURRENT link on the
/// operator's origin and nothing else; it is recorded once per click as
/// <c>shared</c> — never <c>sent</c> — and a retry hands back the very same link;
/// it publishes a Draft through the lifecycle like the first email, and never
/// once the party is under way; a rotation kills every link shared before; it
/// reaches no other host's group; and none of the link, the message, a number or
/// a name reaches the audit trail. Email is unchanged beside it.</para>
/// </summary>
public sealed class PartyInvitationShareTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    private static Task<HttpResponseMessage> ShareAsync(
        HttpClient owner, Guid partyId, Guid groupId, string channel, Guid? requestId = null, int? partyVersion = null) =>
        owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/share",
            new { channel, clientRequestId = requestId ?? Guid.NewGuid(), partyVersion });

    private static async Task<Guid> AddGroupWithPhoneAsync(
        HttpClient owner, Guid partyId, string label, string email, string? phone, params string[] names)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups",
            GroupBody(label, email, 0, names.Select(n => (object)new { name = n }), phone: phone));
        response.EnsureSuccessStatusCode();
        return Group(await response.Content.ReadFromJsonAsync<JsonElement>(), label).GetProperty("id").GetGuid();
    }

    private static string TokenOf(string url)
    {
        Assert.StartsWith($"{Origin}/party/invite/", url);
        return url[$"{Origin}/party/invite/".Length..];
    }

    [Fact]
    public async Task A_whatsapp_share_publishes_a_draft_and_hands_the_host_the_current_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Compleanno di Anna");
        var groupId = await AddGroupWithPhoneAsync(
            owner, partyId, "Famiglia Rossi", "rossi@example.com", "+39 333 123 4567", "Mario Rossi", "Laura Rossi");

        var response = await ShareAsync(owner, partyId, groupId, "whatsapp", partyVersion: 1);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var share = body.GetProperty("share");
        Assert.Equal("whatsapp", share.GetProperty("channel").GetString());
        Assert.Equal("initial", share.GetProperty("kind").GetString());
        Assert.Equal("shared", share.GetProperty("status").GetString());
        Assert.False(share.GetProperty("replayed").GetBoolean());
        var url = share.GetProperty("url").GetString()!;
        var token = TokenOf(url);
        var text = share.GetProperty("text").GetString()!;
        Assert.Contains("\"Compleanno di Anna\"", text);
        Assert.Contains(url, text);
        Assert.Equal(
            "https://wa.me/393331234567?text=" + Uri.EscapeDataString(text),
            share.GetProperty("whatsappUrl").GetString());

        // The link opens exactly this group's invitation.
        var view = await ViewAsync(_factory.CreateClient(), token);
        Assert.Equal("Famiglia Rossi", view.GetProperty("invitation").GetProperty("label").GetString());
        // Announced through the lifecycle, with the version the page must adopt — and not started.
        Assert.Equal(PartyStatuses.Published, body.GetProperty("party").GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("party").GetProperty("version").GetInt32());
        // The card, as the directory now lists it.
        var item = body.GetProperty("item");
        Assert.Equal("group", item.GetProperty("kind").GetString());
        Assert.Equal("shared", item.GetProperty("invitation").GetProperty("state").GetString());
        Assert.Equal("whatsapp", item.GetProperty("invitation").GetProperty("lastAttemptChannel").GetString());
        Assert.True(item.GetProperty("whatsappDirect").GetBoolean());
        // Sharing sends nothing.
        Assert.Empty(_factory.EmailSender.Messages);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PartyInvitationDeliveries.SingleAsync();
        Assert.Equal(PartyInvitationDeliveryChannels.WhatsApp, row.Channel);
        Assert.Equal(PartyInvitationDeliveryStatuses.Shared, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal((await db.PartyInvitationGroups.SingleAsync()).CapabilityId, row.CapabilityId);

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PartyInvitationShare);
        using (var metadata = JsonDocument.Parse(audit.MetadataJson!))
        {
            Assert.Equal("whatsapp", metadata.RootElement.GetProperty("channel").GetString());
            Assert.Equal("initial", metadata.RootElement.GetProperty("kind").GetString());
            Assert.Equal(groupId, metadata.RootElement.GetProperty("invitationGroupId").GetGuid());
            Assert.Equal(partyId, metadata.RootElement.GetProperty("partyId").GetGuid());
        }
        var everyLine = string.Join('\n', await db.AuditLogs
            .Where(a => a.Action.StartsWith("party.invitation"))
            .Select(a => a.MetadataJson ?? string.Empty)
            .ToListAsync());
        foreach (var leak in new[]
        {
            token, "/party/invite/", "wa.me", "393331234567", "333 123 4567", "Mario", "Rossi",
            "rossi@example.com", "Compleanno", PartyInvitationTokens.Hash(token),
        })
        {
            Assert.DoesNotContain(leak, everyLine);
        }
    }

    [Fact]
    public async Task Without_a_certain_international_number_WhatsApp_asks_whom_to_send_to()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");

        foreach (var (label, phone) in new[] { ("Senza numero", (string?)null), ("Numero italiano", "333 123 4567") })
        {
            var groupId = await AddGroupWithPhoneAsync(owner, partyId, label, "x@example.com", phone, "Ospite");
            var share = (await (await ShareAsync(owner, partyId, groupId, "whatsapp")).Content
                .ReadFromJsonAsync<JsonElement>()).GetProperty("share");
            Assert.StartsWith("https://wa.me/?text=", share.GetProperty("whatsappUrl").GetString());
        }
    }

    [Fact]
    public async Task Copying_hands_over_the_same_personal_link_and_a_second_share_is_a_resend()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", "+39 333 1", "Sara");

        var copied = (await (await ShareAsync(owner, partyId, groupId, "copy")).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share");
        Assert.Equal("copy", copied.GetProperty("channel").GetString());
        Assert.Equal("initial", copied.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, copied.GetProperty("whatsappUrl").ValueKind);

        var whatsapp = (await (await ShareAsync(owner, partyId, groupId, "whatsapp")).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share");
        Assert.Equal("resend", whatsapp.GetProperty("kind").GetString());
        // One capability, three channels: the same link.
        Assert.Equal(copied.GetProperty("url").GetString(), whatsapp.GetProperty("url").GetString());
        Assert.Equal(CurrentToken(_factory, groupId), TokenOf(whatsapp.GetProperty("url").GetString()!));
    }

    [Fact]
    public async Task A_share_from_a_stale_read_of_a_draft_publishes_nothing_and_records_nothing()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");

        foreach (int? version in new int?[] { 99, null })
        {
            var response = await ShareAsync(owner, partyId, groupId, "whatsapp", partyVersion: version);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("party_version_conflict", body.GetProperty("error").GetString());
            Assert.Equal(PartyStatuses.Draft, body.GetProperty("party").GetProperty("status").GetString());
            Assert.False(body.TryGetProperty("share", out _));
        }

        Assert.Equal(PartyStatuses.Draft, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyInvitationDeliveries.ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(a => a.Action == AuditActions.PartyInvitationShare).ToListAsync());
    }

    [Fact]
    public async Task Shares_stop_once_the_party_is_under_way()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Nico", "nico@example.com", null, "Nico");

        foreach (var action in new[] { "start-live", "end-live" })
        {
            await AdvanceAsync(owner, partyId, action);
            foreach (var channel in new[] { "whatsapp", "copy" })
            {
                var response = await ShareAsync(owner, partyId, groupId, channel);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("invitations_closed", await ErrorOf(response));
            }
            Assert.False(Group(await GuestListAsync(owner, partyId), "Nico").GetProperty("canShare").GetBoolean());
        }
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyInvitationDeliveries.ToListAsync());
    }

    [Fact]
    public async Task One_click_shares_once_and_its_retry_hands_back_the_same_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");
        var click = Guid.NewGuid();

        var first = (await (await ShareAsync(owner, partyId, groupId, "whatsapp", click, partyVersion: 1)).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share");
        // The retry quotes the version it read before the first answer published the party.
        var retry = await ShareAsync(owner, partyId, groupId, "whatsapp", click, partyVersion: 1);
        retry.EnsureSuccessStatusCode();
        var replayed = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("share");

        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.GetProperty("url").GetString(), replayed.GetProperty("url").GetString());
        Assert.Equal(first.GetProperty("whatsappUrl").GetString(), replayed.GetProperty("whatsappUrl").GetString());
        Assert.Equal(first.GetProperty("createdAt").GetDateTime(), replayed.GetProperty("createdAt").GetDateTime());
        Assert.Equal("initial", replayed.GetProperty("kind").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PartyInvitationDeliveries.CountAsync());
        // A replay handed over nothing new, so it is no event either.
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PartyInvitationShare));
    }

    [Fact]
    public async Task A_click_is_one_act_on_one_channel()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");
        var click = Guid.NewGuid();
        (await ShareAsync(owner, partyId, groupId, "whatsapp", click)).EnsureSuccessStatusCode();

        var copy = await ShareAsync(owner, partyId, groupId, "copy", click);
        Assert.Equal(HttpStatusCode.BadRequest, copy.StatusCode);
        Assert.Equal("request_id_reused", await ErrorOf(copy));
        var email = await SendAsync(owner, partyId, groupId, click);
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode);
        Assert.Equal("request_id_reused", await ErrorOf(email));
        Assert.Empty(_factory.EmailSender.Messages);

        // And the other way round: an email's click is not a share.
        var emailClick = Guid.NewGuid();
        (await SendAsync(owner, partyId, groupId, emailClick)).EnsureSuccessStatusCode();
        var reuse = await ShareAsync(owner, partyId, groupId, "whatsapp", emailClick);
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("request_id_reused", await ErrorOf(reuse));
    }

    [Fact]
    public async Task A_rotation_kills_every_link_shared_before_and_the_next_share_carries_the_new_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");
        var before = TokenOf((await (await ShareAsync(owner, partyId, groupId, "whatsapp")).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share").GetProperty("url").GetString()!);
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{before}")).StatusCode);

        var version = Group(await GuestListAsync(owner, partyId), "Sara").GetProperty("version").GetInt32();
        (await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/rotate-link", new { version })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{before}")).StatusCode);
        // The new link has been shared with nobody yet.
        var sara = Group(await GuestListAsync(owner, partyId), "Sara");
        Assert.Equal("not_sent", sara.GetProperty("delivery").GetProperty("state").GetString());

        var after = (await (await ShareAsync(owner, partyId, groupId, "copy")).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share");
        Assert.Equal("initial", after.GetProperty("kind").GetString());
        var token = TokenOf(after.GetProperty("url").GetString()!);
        Assert.NotEqual(before, token);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_share_reaches_only_the_callers_own_group_and_speaks_a_closed_vocabulary()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var otherPartyId = await CreatePartyAsync(owner, "Altra festa");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");
        var (_, stranger) = await NewHostAsync(_factory);
        var strangerParty = await CreatePartyAsync(stranger);

        Assert.Equal(HttpStatusCode.NotFound,
            (await ShareAsync(stranger, partyId, groupId, "whatsapp", partyVersion: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ShareAsync(stranger, strangerParty, groupId, "whatsapp", partyVersion: 1)).StatusCode);
        // The host's own OTHER party does not own this group either.
        Assert.Equal(HttpStatusCode.NotFound,
            (await ShareAsync(owner, otherPartyId, groupId, "whatsapp", partyVersion: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ShareAsync(owner, partyId, Guid.NewGuid(), "whatsapp", partyVersion: 1)).StatusCode);

        foreach (var channel in new[] { "email", "sms", "WhatsApp", "" })
        {
            var refused = await ShareAsync(owner, partyId, groupId, channel, partyVersion: 1);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("invalid_channel", await ErrorOf(refused));
        }
        var noId = await ShareAsync(owner, partyId, groupId, "copy", Guid.Empty, partyVersion: 1);
        Assert.Equal("invalid_request_id", await ErrorOf(noId));

        // Nothing moved: no row, no publication.
        Assert.Equal(PartyStatuses.Draft, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyInvitationDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Without_a_public_origin_there_is_no_link_to_share_but_without_a_mailer_there_is()
    {
        using var plain = new SqliteWebApplicationFactory();
        plain.EnsureDatabaseCreated();
        var (_, owner) = await NewHostAsync(plain);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Nico", "nico@example.com", 0, "Nico")).GetProperty("id").GetGuid();

        var list = await GuestListAsync(owner, partyId);
        Assert.False(list.GetProperty("shareAvailable").GetBoolean());
        Assert.False(Group(list, "Nico").GetProperty("canShare").GetBoolean());
        var refused = await ShareAsync(owner, partyId, groupId, "whatsapp", partyVersion: 1);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("link_unavailable", await ErrorOf(refused));
        Assert.Equal(PartyStatuses.Draft, (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());

        // A switched-off mailer blocks email, never a share.
        _factory.EmailSender.Enabled = false;
        var (_, host) = await NewHostAsync(_factory);
        var hostParty = await CreatePartyAsync(host);
        var hostGroup = (await AddGroupAsync(host, hostParty, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var hostList = await GuestListAsync(host, hostParty);
        Assert.False(hostList.GetProperty("mailAvailable").GetBoolean());
        Assert.True(hostList.GetProperty("shareAvailable").GetBoolean());
        Assert.True(Group(hostList, "Sara").GetProperty("canShare").GetBoolean());
        (await ShareAsync(host, hostParty, hostGroup, "whatsapp", partyVersion: 1)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Email_is_unchanged_beside_a_share_and_a_shared_group_can_be_reminded()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Bianchi", "bianchi@example.com", null, "Paolo", "Marta");

        (await ShareAsync(owner, partyId, groupId, "whatsapp", partyVersion: 1)).EnsureSuccessStatusCode();
        var shared = Group(await GuestListAsync(owner, partyId), "Bianchi");
        Assert.Equal("shared", shared.GetProperty("delivery").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("delivery").GetProperty("lastSentAt").ValueKind);
        // Invited on the link it holds, somebody has not answered: a reminder is due.
        Assert.True(shared.GetProperty("canRemind").GetBoolean());

        // The email after a share is a RESEND of the same link.
        var sent = await SendAsync(owner, partyId, groupId);
        sent.EnsureSuccessStatusCode();
        var delivery = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivery");
        Assert.Equal("resend", delivery.GetProperty("kind").GetString());
        Assert.Equal("sent", delivery.GetProperty("status").GetString());
        Assert.Equal("email", delivery.GetProperty("channel").GetString());
        var message = Assert.Single(_factory.EmailSender.Messages);
        Assert.Equal(CurrentToken(_factory, groupId), TokenFrom(message));

        var emailed = Group(await GuestListAsync(owner, partyId), "Bianchi").GetProperty("delivery");
        Assert.Equal("sent", emailed.GetProperty("state").GetString());
        Assert.Equal("email", emailed.GetProperty("lastAttemptChannel").GetString());

        var reminded = await SendAsync(owner, partyId, groupId, action: "remind");
        reminded.EnsureSuccessStatusCode();
        Assert.Equal("reminder", (await reminded.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("delivery").GetProperty("kind").GetString());
        Assert.Equal(2, _factory.EmailSender.Messages.Count);
        // Neither a share nor an email is an answer.
        Assert.All(Group(await GuestListAsync(owner, partyId), "Bianchi").GetProperty("guests").EnumerateArray(),
            g => Assert.Equal("pending", g.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task The_shared_message_speaks_the_hosts_language()
    {
        var (hostId, owner) = await NewHostAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.SingleAsync(u => u.Id == hostId)).UiLanguage = "en";
            await db.SaveChangesAsync();
        }
        var partyId = await CreatePartyAsync(owner, "Marta's wedding");
        var groupId = await AddGroupWithPhoneAsync(owner, partyId, "Sara", "sara@example.com", null, "Sara");

        var share = (await (await ShareAsync(owner, partyId, groupId, "copy", partyVersion: 1)).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("share");

        Assert.StartsWith("You're invited to \"Marta's wedding\"", share.GetProperty("text").GetString());
    }
}

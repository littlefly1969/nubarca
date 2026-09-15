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
/// The host's guest list: groups, the people in them, the questions, and the
/// personal link each group holds.
///
/// <para>What this file pins down is the part that is easy to get wrong once a
/// host can edit all of it while the guests are replying: every write to a group
/// spends the group's version, the +1s a group added are never the host's to
/// lose, a new mailbox gets a new link, and another host's list is the same
/// generic not-found as one that does not exist.</para>
/// </summary>
public sealed class PartyGuestListOwnerTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_group_of_two_starts_two_pending_answers_and_stores_only_a_hash()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);

        var group = await AddGroupAsync(owner, partyId, "Mario e Laura", "mario@example.com", 0, "Mario", "Laura");

        var guests = group.GetProperty("guests").EnumerateArray().ToList();
        Assert.Equal(new[] { "Mario", "Laura" }, guests.Select(g => g.GetProperty("name").GetString()!));
        Assert.All(guests, g =>
        {
            Assert.Equal(PartyRsvpStatuses.Pending, g.GetProperty("status").GetString());
            Assert.False(g.GetProperty("isAdditionalGuest").GetBoolean());
        });
        Assert.Equal(2, group.GetProperty("pendingCount").GetInt32());
        Assert.Equal("not_sent", group.GetProperty("delivery").GetProperty("state").GetString());
        Assert.True(group.GetProperty("canSend").GetBoolean());
        Assert.False(group.GetProperty("canRemind").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PartyInvitationGroups.SingleAsync();
        var raw = scope.ServiceProvider.GetRequiredService<PartyInvitationTokens>().Derive(row.CapabilityId);
        Assert.Matches("^[0-9a-f]{64}$", row.TokenHash);
        Assert.Equal(PartyInvitationTokens.Hash(raw), row.TokenHash);
        Assert.Equal(2, await db.PartyRsvps.CountAsync(r => r.Status == PartyRsvpStatuses.Pending && r.RespondedAt == null));

        // Neither the raw token, nor its hash, nor what it is derived from
        // reaches the host: the host needs to know it was sent, not what it is.
        var list = await owner.GetStringAsync($"/api/parties/{partyId}/guest-list");
        Assert.DoesNotContain(raw, list);
        Assert.DoesNotContain(row.TokenHash, list);
        Assert.DoesNotContain(row.CapabilityId.ToString(), list);
        Assert.DoesNotContain("tokenHash", list, StringComparison.OrdinalIgnoreCase);

        // The hash is the lookup key, and the DATABASE says it is unique.
        db.PartyInvitationGroups.Add(new PartyInvitationGroup
        {
            Id = Guid.NewGuid(), PartyId = partyId, Label = "Copia", RecipientEmail = "copia@example.com",
            CapabilityId = Guid.NewGuid(), TokenHash = row.TokenHash, CapabilityIssuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_group_is_somebody_with_a_label_and_a_real_address()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        object[] mario = [new { name = "Mario" }];

        var refused = new (object Body, string Error)[]
        {
            (GroupBody("  ", "mario@example.com", 0, mario), "invalid_label"),
            (GroupBody(new string('x', 121), "mario@example.com", 0, mario), "invalid_label"),
            (GroupBody("Mario", "mario", 0, mario), "invalid_email"),
            (GroupBody("Mario", "Mario <mario@example.com>", 0, mario), "invalid_email"),
            (GroupBody("Mario", "mario@example.com", 11, mario), "invalid_max_additional_guests"),
            (GroupBody("Mario", "mario@example.com", -1, mario), "invalid_max_additional_guests"),
            (GroupBody("Mario", "mario@example.com", 0, []), "invalid_guests"),
            (GroupBody("Tanti", "mario@example.com", 0,
                Enumerable.Range(0, 21).Select(i => (object)new { name = $"Ospite {i}" })), "invalid_guests"),
            (GroupBody("Mario", "mario@example.com", 0, [new { name = " " }]), "invalid_guest"),
            (GroupBody("Mario", "mario@example.com", 0, [new { name = "Mario", email = "non-un-indirizzo" }]), "invalid_guest"),
            (GroupBody("Mario", "mario@example.com", 0, [new { id = Guid.NewGuid(), name = "Mario" }]), "unknown_guest"),
        };
        foreach (var (body, error) in refused)
        {
            var response = await owner.PostAsJsonAsync($"/api/parties/{partyId}/invitation-groups", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, await ErrorOf(response));
        }
        Assert.Equal(0, (await GuestListAsync(owner, partyId)).GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public async Task Editing_the_named_list_keeps_the_groups_plus_ones_and_a_new_address_gets_a_new_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", 1, "Mario", "Laura"))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);

        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);
        (await RsvpAsync(guest, token, Reply(
            view,
            new Dictionary<string, string> { ["Mario"] = "attending", ["Laura"] = "declined" },
            additionalGuests: [new { name = "Giulia" }]))).EnsureSuccessStatusCode();

        // The host renames Mario, drops Laura and adds Nonna Rosa — and never
        // mentions Giulia, who is the group's own +1.
        var current = Group(await GuestListAsync(owner, partyId), "Famiglia Rossi");
        var edited = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Famiglia Rossi", "rossi@example.com", 1,
                [new { id = OwnerGuest(current, "Mario").GetProperty("id").GetGuid(), name = "Mario Rossi" },
                 new { name = "Nonna Rosa" }],
                version: current.GetProperty("version").GetInt32()));
        edited.EnsureSuccessStatusCode();
        var after = Group(await edited.Content.ReadFromJsonAsync<JsonElement>(), "Famiglia Rossi");
        Assert.Equal(
            new[] { ("Mario Rossi", "attending", false), ("Nonna Rosa", "pending", false), ("Giulia", "attending", true) },
            after.GetProperty("guests").EnumerateArray().Select(g => (
                g.GetProperty("name").GetString()!,
                g.GetProperty("status").GetString()!,
                g.GetProperty("isAdditionalGuest").GetBoolean())));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.PartyGuests.AnyAsync(g => g.Name == "Laura"));
            Assert.Equal(3, await db.PartyRsvps.CountAsync());
        }
        // The same mailbox keeps the same link.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);

        object[] named = after.GetProperty("guests").EnumerateArray()
            .Where(g => !g.GetProperty("isAdditionalGuest").GetBoolean())
            .Select(g => (object)new { id = g.GetProperty("id").GetGuid(), name = g.GetProperty("name").GetString() })
            .ToArray();
        var version = after.GetProperty("version").GetInt32();

        // The allowance cannot drop below the +1 the group already brought.
        var shrink = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Famiglia Rossi", "rossi@example.com", 0, named, version: version));
        Assert.Equal(HttpStatusCode.BadRequest, shrink.StatusCode);
        Assert.Equal("additional_guests_in_use", await ErrorOf(shrink));

        // A different mailbox: the old link stops opening anything, at once.
        var moved = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Famiglia Rossi", "nuova.casa@example.com", 1, named, version: version));
        moved.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);
        var rotated = Group(await moved.Content.ReadFromJsonAsync<JsonElement>(), "Famiglia Rossi");
        Assert.Equal("not_sent", rotated.GetProperty("delivery").GetProperty("state").GetString());
        Assert.False(rotated.GetProperty("canRemind").GetBoolean());

        using var verify = _factory.Services.CreateScope();
        var audit = await verify.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .SingleAsync(a => a.Action == AuditActions.PartyInvitationRotate);
        Assert.Contains("recipient_changed", audit.MetadataJson);
        Assert.DoesNotContain("nuova.casa@example.com", audit.MetadataJson);
        Assert.DoesNotContain("Rossi", audit.MetadataJson);
    }

    [Fact]
    public async Task A_stale_write_is_refused_with_the_list_as_it_is()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 0, "Mario"))
            .GetProperty("id").GetGuid();

        var responses = new[]
        {
            await owner.PutAsJsonAsync(
                $"/api/parties/{partyId}/invitation-groups/{groupId}",
                GroupBody("Mario Bianchi", "mario@example.com", 0, [new { name = "Mario" }], version: 99)),
            await owner.DeleteAsync($"/api/parties/{partyId}/invitation-groups/{groupId}?version=99"),
            await owner.PostAsJsonAsync(
                $"/api/parties/{partyId}/invitation-groups/{groupId}/rotate-link", new { version = 99 }),
        };
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("version_conflict", body.GetProperty("error").GetString());
            var group = Group(body.GetProperty("guestList"), "Mario");
            Assert.Equal(1, group.GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task Removing_a_group_revokes_its_link_and_erases_its_rows_and_nothing_else()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, viewToken, uploadToken) = await OpenPublicQrAsync(owner, partyId);
        var keepId = (await AddGroupAsync(owner, partyId, "Resta", "resta@example.com", 0, "Anna")).GetProperty("id").GetGuid();
        var goneId = (await AddGroupAsync(owner, partyId, "Va via", "via@example.com", 1, "Bruno")).GetProperty("id").GetGuid();
        var keepToken = await InviteAsync(_factory, owner, partyId, keepId);
        var goneToken = await InviteAsync(_factory, owner, partyId, goneId);

        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, goneToken, Reply(
            await ViewAsync(guest, goneToken),
            new Dictionary<string, string> { ["Bruno"] = "attending" },
            additionalGuests: [new { name = "Carla" }]))).EnsureSuccessStatusCode();

        // The party is on and a browser has joined it through the QR — an
        // identity that was never this group's.
        await AdvanceAsync(owner, partyId, "start-live");
        (await _factory.CreateClient().PostAsync($"/api/party/{uploadToken}/upload-session", null))
            .EnsureSuccessStatusCode();

        var version = Group(await GuestListAsync(owner, partyId), "Va via").GetProperty("version").GetInt32();
        var removed = await owner.DeleteAsync($"/api/parties/{partyId}/invitation-groups/{goneId}?version={version}");
        removed.EnsureSuccessStatusCode();
        var list = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "Resta" }, list.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("label").GetString()!));

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{goneToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{keepToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{viewToken}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PartyInvitationGroups.AnyAsync(g => g.Id == goneId));
        Assert.False(await db.PartyGuests.AnyAsync(g => g.PartyInvitationGroupId == goneId));
        Assert.False(await db.PartyInvitationDeliveries.AnyAsync(d => d.PartyInvitationGroupId == goneId));
        Assert.False(await db.PartyRsvpAnswers.AnyAsync(a => a.PartyInvitationGroupId == goneId));
        // Anna's row, and only hers, is left.
        Assert.Equal(1, await db.PartyRsvps.CountAsync());
        Assert.Equal(1, await db.PartyParticipants.CountAsync());
        Assert.True(await db.PartyAlbumLinks.AnyAsync(l => l.PartyId == partyId && l.Enabled && l.RevokedAt == null));
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PartyInvitationGroupDelete);
        Assert.DoesNotContain("Bruno", audit.MetadataJson);
    }

    [Fact]
    public async Task Rotating_kills_the_old_link_keeps_its_history_and_starts_the_invitation_again()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var first = await InviteAsync(_factory, owner, partyId, groupId);

        var sent = Group(await GuestListAsync(owner, partyId), "Sara");
        Assert.Equal("sent", sent.GetProperty("delivery").GetProperty("state").GetString());
        Assert.True(sent.GetProperty("canRemind").GetBoolean());

        var rotation = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/rotate-link",
            new { version = sent.GetProperty("version").GetInt32() });
        rotation.EnsureSuccessStatusCode();
        var rotated = Group(await rotation.Content.ReadFromJsonAsync<JsonElement>(), "Sara");
        // An email that carried a dead link invited nobody.
        Assert.Equal("not_sent", rotated.GetProperty("delivery").GetProperty("state").GetString());
        Assert.False(rotated.GetProperty("canRemind").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/party-invitations/{first}")).StatusCode);

        var again = await SendAsync(owner, partyId, groupId);
        again.EnsureSuccessStatusCode();
        Assert.Equal("initial", (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("delivery").GetProperty("kind").GetString());
        var second = TokenFrom(_factory.EmailSender.Last!);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync($"/api/party-invitations/{second}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync($"/api/party-invitations/{first}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The history stays, tagged with the generation it sent.
        Assert.Equal(2, await db.PartyInvitationDeliveries.CountAsync());
        Assert.Equal(2, await db.PartyInvitationDeliveries.Select(d => d.CapabilityId).Distinct().CountAsync());
        Assert.Contains("\"owner\"", (await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PartyInvitationRotate)).MetadataJson);
    }

    [Fact]
    public async Task Another_hosts_guest_list_is_the_same_404_as_one_that_does_not_exist()
    {
        var (_, alice) = await NewHostAsync(_factory);
        var (_, bob) = await NewHostAsync(_factory);
        var aliceParty = await CreatePartyAsync(alice, "Festa di Alice");
        var aliceGroup = (await AddGroupAsync(alice, aliceParty, "Zia Ottavia", "ottavia@example.com", 0, "Ottavia"))
            .GetProperty("id").GetGuid();
        var aliceQuestion = await AddQuestionAsync(alice, aliceParty, "Allergie?", "short_text");
        var bobParty = await CreatePartyAsync(bob, "Festa di Bob");

        async Task<(HttpStatusCode Status, string Body)> Call(HttpMethod method, string url, object? body)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            var response = await bob.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        static (HttpMethod, string, object?)[] Routes(Guid party, Guid group, Guid question) =>
        [
            (HttpMethod.Get, $"/api/parties/{party}/guest-list", null),
            (HttpMethod.Post, $"/api/parties/{party}/invitation-groups",
                GroupBody("X", "x@example.com", 0, [new { name = "X" }])),
            (HttpMethod.Put, $"/api/parties/{party}/invitation-groups/{group}",
                GroupBody("X", "x@example.com", 0, [new { name = "X" }], version: 1)),
            (HttpMethod.Delete, $"/api/parties/{party}/invitation-groups/{group}?version=1", null),
            (HttpMethod.Post, $"/api/parties/{party}/invitation-groups/{group}/send",
                new { clientRequestId = Guid.NewGuid(), partyVersion = 1 }),
            (HttpMethod.Post, $"/api/parties/{party}/invitation-groups/{group}/remind",
                new { clientRequestId = Guid.NewGuid() }),
            (HttpMethod.Post, $"/api/parties/{party}/invitation-groups/{group}/rotate-link", new { version = 1 }),
            (HttpMethod.Post, $"/api/parties/{party}/rsvp-questions",
                new { prompt = "X", kind = "yes_no", required = false }),
            (HttpMethod.Put, $"/api/parties/{party}/rsvp-questions/{question}",
                new { prompt = "X", kind = "yes_no", required = false, isActive = true, version = 1 }),
            (HttpMethod.Put, $"/api/parties/{party}/rsvp-questions/order", new { questionIds = new[] { question } }),
        ];

        foreach (var (method, url, body) in Routes(aliceParty, aliceGroup, aliceQuestion))
        {
            var foreign = await Call(method, url, body);
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
            Assert.DoesNotContain("Ottavia", foreign.Body);
            Assert.DoesNotContain("Allergie", foreign.Body);
        }
        foreach (var (method, url, body) in Routes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Call(method, url, body)).Status);
        }
        // Bob's OWN party does not make Alice's group or question his: the two
        // ids are proven to belong together, never trusted one at a time.
        var bobsOwn = Routes(bobParty, aliceGroup, aliceQuestion);
        foreach (var (method, url, body) in new[] { 2, 3, 4, 5, 6, 8 }.Select(i => bobsOwn[i]))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Call(method, url, body)).Status);
        }

        // And nothing of Alice's moved.
        Assert.Empty(_factory.EmailSender.Messages);
        var aliceList = await GuestListAsync(alice, aliceParty);
        Assert.Equal(1, Group(aliceList, "Zia Ottavia").GetProperty("version").GetInt32());
        Assert.Equal("Allergie?", Question(aliceList, aliceQuestion).GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Questions_are_three_closed_kinds_frozen_once_answered_and_ordered_by_the_host()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var arrival = await AddQuestionAsync(owner, partyId, "Come arrivi?", "short_text");
        var menu = await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", required: true,
            options: ["Carne", "Pesce"]);
        var parking = await AddQuestionAsync(owner, partyId, "Ti serve il parcheggio?", "yes_no");

        var refused = new (object Body, string Error)[]
        {
            (new { prompt = "Menù?", kind = "single_choice", required = false, options = new[] { "Carne" } }, "invalid_options"),
            (new { prompt = "Menù?", kind = "single_choice", required = false, options = new[] { "Carne", "carne" } }, "invalid_options"),
            (new { prompt = "Menù?", kind = "single_choice", required = false,
                options = Enumerable.Range(0, 21).Select(i => $"Piatto {i}").ToArray() }, "invalid_options"),
            (new { prompt = "Parcheggio?", kind = "yes_no", required = false, options = new[] { "Sì", "No" } }, "invalid_options"),
            (new { prompt = "Forse?", kind = "maybe", required = false, options = (string[]?)null }, "invalid_kind"),
            (new { prompt = "  ", kind = "yes_no", required = false, options = (string[]?)null }, "invalid_prompt"),
            (new { prompt = new string('x', 301), kind = "yes_no", required = false, options = (string[]?)null }, "invalid_prompt"),
        };
        foreach (var (body, error) in refused)
        {
            var response = await owner.PostAsJsonAsync($"/api/parties/{partyId}/rsvp-questions", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, await ErrorOf(response));
        }

        var list = await GuestListAsync(owner, partyId);
        Assert.Equal(
            new[] { "short_text", "single_choice", "yes_no" },
            list.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("kind").GetString()!));
        Assert.Equal(new[] { "Carne", "Pesce" },
            Question(list, menu).GetProperty("options").EnumerateArray().Select(o => o.GetString()!));

        // Answered once, frozen: what it ASKS cannot change under an answer.
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token),
            new Dictionary<string, string> { ["Sara"] = "attending" },
            answers: [new { questionId = menu, value = "Pesce" }]))).EnsureSuccessStatusCode();

        var answered = Question(await GuestListAsync(owner, partyId), menu);
        Assert.True(answered.GetProperty("locked").GetBoolean());
        Assert.Equal(1, answered.GetProperty("answerCount").GetInt32());
        var reworded = await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{menu}", new
        {
            prompt = "Carne, pesce o vegano?", kind = "single_choice", required = true,
            options = new[] { "Carne", "Pesce", "Vegano" }, isActive = true,
            version = answered.GetProperty("version").GetInt32(),
        });
        Assert.Equal(HttpStatusCode.Conflict, reworded.StatusCode);
        var locked = await reworded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("question_locked", locked.GetProperty("error").GetString());
        Assert.Equal("Carne o pesce?", Question(locked.GetProperty("guestList"), menu).GetProperty("prompt").GetString());

        // …and it can still be retired.
        var retired = await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{menu}", new
        {
            prompt = "Carne o pesce?", kind = "single_choice", required = true,
            options = new[] { "Carne", "Pesce" }, isActive = false,
            version = answered.GetProperty("version").GetInt32(),
        });
        retired.EnsureSuccessStatusCode();
        Assert.False(Question(await retired.Content.ReadFromJsonAsync<JsonElement>(), menu).GetProperty("isActive").GetBoolean());

        // An unanswered question stays freely editable.
        var arrivalRow = Question(await GuestListAsync(owner, partyId), arrival);
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{arrival}", new
        {
            prompt = "Con che mezzo arrivi?", kind = "short_text", required = false, isActive = true,
            version = arrivalRow.GetProperty("version").GetInt32(),
        })).EnsureSuccessStatusCode();

        var reordered = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/rsvp-questions/order", new { questionIds = new[] { parking, arrival, menu } });
        reordered.EnsureSuccessStatusCode();
        Assert.Equal(
            new[] { parking, arrival, menu },
            (await reordered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("questions")
                .EnumerateArray().Select(q => q.GetProperty("id").GetGuid()));
        var partial = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/rsvp-questions/order", new { questionIds = new[] { parking, arrival } });
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        Assert.Equal("invalid_order", await ErrorOf(partial));
    }

    [Fact]
    public async Task A_form_holds_at_most_twenty_active_questions()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var ids = new List<Guid>();
        for (var i = 0; i < PartyInvitationLimits.MaxActiveQuestions; i++)
        {
            ids.Add(await AddQuestionAsync(owner, partyId, $"Domanda {i}?", "yes_no"));
        }

        var tooMany = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/rsvp-questions", new { prompt = "Ancora una?", kind = "yes_no", required = false });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("too_many_questions", await ErrorOf(tooMany));

        // Retiring one makes room; bringing it back would overflow again.
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{ids[0]}", new
        {
            prompt = "Domanda 0?", kind = "yes_no", required = false, isActive = false, version = 1,
        })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/rsvp-questions", new { prompt = "Ancora una?", kind = "yes_no", required = false }))
            .EnsureSuccessStatusCode();
        var back = await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{ids[0]}", new
        {
            prompt = "Domanda 0?", kind = "yes_no", required = false, isActive = true, version = 2,
        });
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.Equal("too_many_questions", await ErrorOf(back));
    }
}

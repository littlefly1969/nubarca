using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The personal invitation, from the guest's side: what the link opens, what it
/// never opens, and what a reply may say.
///
/// <para>Three identities stay three here. The invitation link is a capability
/// over ONE group's RSVP; the party's QR is a capability over the party; a
/// PartyParticipant is the anonymous browser at the party. No request on the
/// first ever creates the third, and neither capability answers for the
/// other.</para>
/// </summary>
public sealed class PartyInvitationRsvpTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    private static Dictionary<string, string> Statuses(params (string Name, string Status)[] people) =>
        people.ToDictionary(p => p.Name, p => p.Status);

    [Fact]
    public async Task The_personal_link_opens_its_own_group_without_an_account_and_mints_no_participant()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Matrimonio di Marta");
        var mine = (await AddGroupAsync(owner, partyId, "Mario e Laura", "mario@example.com", 1, "Mario", "Laura"))
            .GetProperty("id").GetGuid();
        await AddGroupAsync(owner, partyId, "Famiglia Verdi", "verdi@example.com", 0, "Gino Verdi", "Pina Verdi");
        var token = await InviteAsync(_factory, owner, partyId, mine);

        // No account, no cookie jar holding anything.
        var guest = _factory.CreateClient();
        var response = await guest.GetAsync($"/api/party-invitations/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var raw = await response.Content.ReadAsStringAsync();
        var view = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Matrimonio di Marta", view.GetProperty("party").GetProperty("title").GetString());
        Assert.Equal("before", view.GetProperty("party").GetProperty("phase").GetString());
        var invitation = view.GetProperty("invitation");
        Assert.Equal("Mario e Laura", invitation.GetProperty("label").GetString());
        Assert.True(invitation.GetProperty("canRespond").GetBoolean());
        Assert.Equal(1, invitation.GetProperty("maxAdditionalGuests").GetInt32());
        Assert.Equal(new[] { "Mario", "Laura" },
            invitation.GetProperty("guests").EnumerateArray().Select(g => g.GetProperty("name").GetString()!));

        // Nothing of the other group, nothing the guest has no use for, and
        // nothing internal.
        foreach (var leak in new[]
        {
            "Verdi", "verdi@example.com", "mario@example.com", mine.ToString(), partyId.ToString(),
            "tokenHash", "capabilityId", "recipientEmail", "ownerUserId", "phone",
        })
        {
            Assert.DoesNotContain(leak, raw, StringComparison.OrdinalIgnoreCase);
        }

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyParticipants.ToListAsync());
    }

    [Fact]
    public async Task Unknown_draft_rotated_and_removed_links_are_all_the_same_404()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var draftToken = CurrentToken(_factory, groupId);
        var guest = _factory.CreateClient();

        async Task AssertGone(string token)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await RsvpAsync(guest, token, new
            {
                version = 1, guests = Array.Empty<object>(), additionalGuests = Array.Empty<object>(),
                answers = Array.Empty<object>(),
            })).StatusCode);
        }

        // A Draft party has announced nothing, so its link opens nothing.
        await AssertGone(draftToken);
        await AssertGone("definitely-not-a-token");
        await AssertGone(new string('A', 43));
        await AssertGone(new string('A', 4000));

        // Published: the very same link opens — no new one was needed.
        await AdvanceAsync(owner, partyId, "publish");
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{draftToken}")).StatusCode);

        var version = Group(await GuestListAsync(owner, partyId), "Sara").GetProperty("version").GetInt32();
        (await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/rotate-link", new { version })).EnsureSuccessStatusCode();
        await AssertGone(draftToken);

        var otherId = (await AddGroupAsync(owner, partyId, "Nico", "nico@example.com", 0, "Nico")).GetProperty("id").GetGuid();
        var otherToken = await InviteAsync(_factory, owner, partyId, otherId);
        (await owner.DeleteAsync($"/api/parties/{partyId}/invitation-groups/{otherId}?version=1")).EnsureSuccessStatusCode();
        await AssertGone(otherToken);
    }

    [Fact]
    public async Task A_host_who_may_no_longer_run_parties_has_closed_their_invitations()
    {
        var roleKey = await _factory.CreateRoleAsync($"Host {Guid.NewGuid():N}", EveryPartyPermission);
        var (_, owner) = await _factory.CreateRoleClientAsync(roleKey, $"host-{Guid.NewGuid():N}@example.com");
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);

        // Re-read on every request, with no rotation: the role is the answer.
        await _factory.SetRolePermissionsRawAsync(roleKey);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);

        await _factory.SetRolePermissionsRawAsync(roleKey, EveryPartyPermission);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{token}")).StatusCode);
    }

    [Fact]
    public async Task The_invitation_opens_no_live_capability_and_the_party_qr_answers_no_invitation()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Festa dal vivo");
        var (_, viewToken, uploadToken) = await OpenPublicQrAsync(owner, partyId);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var invitation = await InviteAsync(_factory, owner, partyId, groupId);
        await AdvanceAsync(owner, partyId, "start-live");
        var guest = _factory.CreateClient();

        // The party's own QR really is open and live…
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{viewToken}")).StatusCode);

        // …and the invitation token reaches none of it.
        var attempts = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, $"/api/party/{invitation}", null),
            (HttpMethod.Get, $"/api/party/{invitation}/items", null),
            (HttpMethod.Post, $"/api/party/{invitation}/upload-session", null),
            (HttpMethod.Post, $"/api/party/{invitation}/messages", new { text = "Auguri!" }),
            (HttpMethod.Get, $"/api/party/{invitation}/game", null),
            (HttpMethod.Post, $"/api/party/{invitation}/game/join", null),
            (HttpMethod.Get, $"/api/party/{invitation}/print", null),
            (HttpMethod.Get, $"/api/party/{invitation}/challenges", null),
        };
        foreach (var (method, url, body) in attempts)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            Assert.Equal(HttpStatusCode.NotFound, (await guest.SendAsync(request)).StatusCode);
        }
        // A chat app pasting it is told nothing about the party either.
        var preview = await guest.GetStringAsync($"/api/party/{invitation}/link-preview");
        Assert.DoesNotContain("Festa dal vivo", preview);

        // The invitation still shows — read-only now the party is on.
        var view = await ViewAsync(guest, invitation);
        Assert.Equal("live", view.GetProperty("party").GetProperty("phase").GetString());
        Assert.False(view.GetProperty("invitation").GetProperty("canRespond").GetBoolean());

        // And the QR's tokens are not invitations.
        foreach (var capability in new[] { viewToken, uploadToken! })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{capability}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await RsvpAsync(guest, capability, new
            {
                version = 1, guests = Array.Empty<object>(), additionalGuests = Array.Empty<object>(),
                answers = Array.Empty<object>(),
            })).StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyParticipants.ToListAsync());
    }

    [Fact]
    public async Task A_family_answers_person_by_person_with_a_plus_one_notes_and_every_kind_of_question()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var arrival = await AddQuestionAsync(owner, partyId, "Come arrivi?", "short_text");
        var menu = await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", required: true,
            options: ["Carne", "Pesce"]);
        var parking = await AddQuestionAsync(owner, partyId, "Ti serve il parcheggio?", "yes_no");
        var groupId = (await AddGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", 2, "Mario", "Laura", "Luca"))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);

        // Laura has not decided yet; the rest of the family has.
        var first = await RsvpAsync(guest, token, new
        {
            version = Version(view),
            guests = new object[]
            {
                new { guestId = GuestId(view, "Mario"), status = "attending", dietaryNotes = "Senza glutine" },
                new { guestId = GuestId(view, "Laura"), status = "pending", dietaryNotes = (string?)null },
                new { guestId = GuestId(view, "Luca"), status = "declined", dietaryNotes = (string?)null },
            },
            additionalGuests = new[] { new { name = "Giulia", dietaryNotes = "Vegetariana" } },
            answers = new object[]
            {
                new { questionId = arrival, value = "  In treno " },
                new { questionId = menu, value = "Pesce" },
                new { questionId = parking, value = false },
            },
        });
        first.EnsureSuccessStatusCode();
        var answered = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(Version(view) + 1, Version(answered));
        Assert.Equal("attending", GuestRow(answered, "Mario").GetProperty("status").GetString());
        Assert.Equal("Senza glutine", GuestRow(answered, "Mario").GetProperty("dietaryNotes").GetString());
        Assert.Equal("pending", GuestRow(answered, "Laura").GetProperty("status").GetString());
        Assert.Equal("declined", GuestRow(answered, "Luca").GetProperty("status").GetString());
        var giulia = GuestRow(answered, "Giulia");
        Assert.True(giulia.GetProperty("isAdditionalGuest").GetBoolean());
        Assert.Equal("attending", giulia.GetProperty("status").GetString());
        Assert.Equal(1, answered.GetProperty("invitation").GetProperty("additionalGuestsUsed").GetInt32());
        var questions = answered.GetProperty("invitation").GetProperty("questions").EnumerateArray()
            .ToDictionary(q => q.GetProperty("id").GetGuid(), q => q.GetProperty("answer"));
        Assert.Equal("In treno", questions[arrival].GetString());
        Assert.Equal("Pesce", questions[menu].GetString());
        Assert.Equal(JsonValueKind.False, questions[parking].ValueKind);

        // The host sees it at once, counted by the definitions and nothing else.
        var list = await GuestListAsync(owner, partyId);
        var family = Group(list, "Famiglia Rossi");
        Assert.Equal(1, family.GetProperty("pendingCount").GetInt32());
        Assert.Equal(2, family.GetProperty("attendingCount").GetInt32());
        Assert.Equal(1, family.GetProperty("declinedCount").GetInt32());
        Assert.Equal(1, family.GetProperty("additionalGuestsUsed").GetInt32());
        Assert.Equal("Senza glutine", OwnerGuest(family, "Mario").GetProperty("dietaryNotes").GetString());
        var summary = list.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("invited").GetInt32());
        Assert.Equal(1, summary.GetProperty("missingResponses").GetInt32());
        Assert.Equal(2, summary.GetProperty("attending").GetInt32());
        Assert.Equal(1, summary.GetProperty("declined").GetInt32());
        Assert.Equal(2, summary.GetProperty("expectedPeople").GetInt32());

        DateTime? firstReply;
        DateTime marioUpdated;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mario = await db.PartyRsvps.SingleAsync(r => r.PartyGuestId == GuestId(view, "Mario"));
            firstReply = mario.RespondedAt;
            marioUpdated = mario.UpdatedAt;
            Assert.NotNull(firstReply);
            Assert.Null((await db.PartyRsvps.SingleAsync(r => r.PartyGuestId == GuestId(view, "Laura"))).RespondedAt);
        }

        // Laura decides, Giulia is not coming after all, Mario adds a detail,
        // and two answers are withdrawn.
        await Task.Delay(20);
        var second = await RsvpAsync(guest, token, new
        {
            version = Version(answered),
            guests = new object[]
            {
                new { guestId = GuestId(view, "Mario"), status = "attending", dietaryNotes = "Senza glutine e lattosio" },
                new { guestId = GuestId(view, "Laura"), status = "attending", dietaryNotes = (string?)null },
                new { guestId = GuestId(view, "Luca"), status = "declined", dietaryNotes = (string?)null },
            },
            additionalGuests = Array.Empty<object>(),
            answers = new object[] { new { questionId = menu, value = "Carne" } },
        });
        second.EnsureSuccessStatusCode();
        var changed = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(changed.GetProperty("invitation").GetProperty("guests").EnumerateArray(),
            g => g.GetProperty("name").GetString() == "Giulia");
        Assert.Equal("attending", GuestRow(changed, "Laura").GetProperty("status").GetString());

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        // Giulia is gone — the person and her answer, not just hidden.
        Assert.False(await verifyDb.PartyGuests.AnyAsync(g => g.Name == "Giulia"));
        Assert.Equal(3, await verifyDb.PartyRsvps.CountAsync());
        var marioAfter = await verifyDb.PartyRsvps.SingleAsync(r => r.PartyGuestId == GuestId(view, "Mario"));
        // A change of mind is not a first reply.
        Assert.Equal(firstReply, marioAfter.RespondedAt);
        Assert.True(marioAfter.UpdatedAt > marioUpdated);
        Assert.NotNull((await verifyDb.PartyRsvps.SingleAsync(r => r.PartyGuestId == GuestId(view, "Laura"))).RespondedAt);
        var stored = await verifyDb.PartyRsvpAnswers.ToListAsync();
        Assert.Equal(menu, Assert.Single(stored).PartyRsvpQuestionId);
        Assert.Equal("\"Carne\"", stored[0].ValueJson);
    }

    [Fact]
    public async Task A_group_can_decline_whole_without_answering_what_only_those_coming_need_to_say()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", required: true, options: ["Carne", "Pesce"]);
        var groupId = (await AddGroupAsync(owner, partyId, "Anna e Luca", "anna@example.com", 1, "Anna", "Luca"))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();

        var declined = await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token), Statuses(("Anna", "declined"), ("Luca", "declined"))));
        declined.EnsureSuccessStatusCode();
        var group = Group(await GuestListAsync(owner, partyId), "Anna e Luca");
        Assert.Equal(2, group.GetProperty("declinedCount").GetInt32());
        Assert.Equal(0, group.GetProperty("pendingCount").GetInt32());
        Assert.False(group.GetProperty("canRemind").GetBoolean());

        // A +1 comes WITH somebody…
        var view = await declined.Content.ReadFromJsonAsync<JsonElement>();
        var lonelyPlusOne = await RsvpAsync(guest, token, Reply(
            view, Statuses(("Anna", "declined"), ("Luca", "declined")), additionalGuests: [new { name = "Ospite" }]));
        Assert.Equal(HttpStatusCode.BadRequest, lonelyPlusOne.StatusCode);
        Assert.Equal("additional_guests_need_attendee", await ErrorOf(lonelyPlusOne));

        // …and a group that IS coming answers what is required.
        var unanswered = await RsvpAsync(guest, token, Reply(view, Statuses(("Anna", "attending"), ("Luca", "declined"))));
        Assert.Equal(HttpStatusCode.BadRequest, unanswered.StatusCode);
        Assert.Equal("required_answer_missing", await ErrorOf(unanswered));
    }

    [Fact]
    public async Task The_server_refuses_any_reply_it_did_not_offer_and_writes_none_of_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var menu = await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", required: true, options: ["Carne", "Pesce"]);
        var mineId = (await AddGroupAsync(owner, partyId, "Mario e Laura", "mario@example.com", 1, "Mario", "Laura"))
            .GetProperty("id").GetGuid();
        var gino = OwnerGuest(await AddGroupAsync(owner, partyId, "Verdi", "verdi@example.com", 0, "Gino"), "Gino")
            .GetProperty("id").GetGuid();
        var otherParty = await CreatePartyAsync(owner, "Un'altra festa");
        var otherQuestion = await AddQuestionAsync(owner, otherParty, "Altro?", "yes_no");
        var token = await InviteAsync(_factory, owner, partyId, mineId);
        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);

        object Person(string name, string status, string? notes = null) =>
            new { guestId = GuestId(view, name), status, dietaryNotes = notes };
        object Body(object[]? guests = null, object[]? additional = null, object[]? answers = null, int? version = null) => new
        {
            version = version ?? Version(view),
            guests = guests ?? [Person("Mario", "attending"), Person("Laura", "attending")],
            additionalGuests = additional ?? [],
            answers = answers ?? [new { questionId = menu, value = "Carne" }],
        };

        var refused = new (object Body, string Error)[]
        {
            (Body(additional: [new { name = "Uno" }, new { name = "Due" }]), "too_many_additional_guests"),
            (Body(guests: [Person("Mario", "attending")]), "invalid_guests"),
            (Body(guests: [Person("Mario", "attending"), new { guestId = gino, status = "attending" }]), "invalid_guests"),
            (Body(guests: [Person("Mario", "attending"), Person("Laura", "attending"),
                new { guestId = gino, status = "attending" }]), "invalid_guests"),
            (Body(guests: [Person("Mario", "maybe"), Person("Laura", "attending")]), "invalid_status"),
            (Body(guests: [Person("Mario", "attending", new string('x', 501)), Person("Laura", "attending")]), "invalid_dietary_notes"),
            (Body(answers: [new { questionId = menu, value = "Pollo" }]), "invalid_answer"),
            (Body(answers: [new { questionId = menu, value = 1 }]), "invalid_answer"),
            (Body(answers: [new { questionId = menu, value = new { choice = "Carne" } }]), "invalid_answer"),
            (Body(answers: [new { questionId = menu, value = "Carne" }, new { questionId = otherQuestion, value = true }]), "unknown_question"),
            (Body(answers: [new { questionId = menu, value = "Carne" }, new { questionId = menu, value = "Pesce" }]), "unknown_question"),
            (Body(additional: [new { guestId = GuestId(view, "Mario"), name = "Mario" }]), "invalid_additional_guest"),
            (Body(additional: [new { name = " " }]), "invalid_additional_guest"),
        };
        foreach (var (body, error) in refused)
        {
            var response = await RsvpAsync(guest, token, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(error, await ErrorOf(response));
        }
        var untouched = Group(await GuestListAsync(owner, partyId), "Mario e Laura");
        Assert.Equal(2, untouched.GetProperty("pendingCount").GetInt32());
        Assert.Equal(Version(view), untouched.GetProperty("version").GetInt32());

        // A stale form is a conflict that carries the invitation as it is now.
        (await RsvpAsync(guest, token, Body())).EnsureSuccessStatusCode();
        var stale = await RsvpAsync(guest, token, Body());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", conflict.GetProperty("error").GetString());
        Assert.Equal(Version(view) + 1, Version(conflict.GetProperty("invitation")));

        // And "pending" is not an answer somebody who has answered can give.
        var backwards = await RsvpAsync(guest, token, Body(
            guests: [Person("Mario", "pending"), Person("Laura", "attending")], version: Version(view) + 1));
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal("invalid_status", await ErrorOf(backwards));
    }

    [Fact]
    public async Task Once_the_party_starts_the_invitation_shows_the_answers_and_takes_no_new_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(await ViewAsync(guest, token), Statuses(("Sara", "attending")))))
            .EnsureSuccessStatusCode();

        foreach (var (action, phase) in new[] { ("start-live", "live"), ("end-live", "after") })
        {
            await AdvanceAsync(owner, partyId, action);
            var view = await ViewAsync(guest, token);
            Assert.Equal(phase, view.GetProperty("party").GetProperty("phase").GetString());
            Assert.False(view.GetProperty("invitation").GetProperty("canRespond").GetBoolean());
            Assert.Equal("attending", GuestRow(view, "Sara").GetProperty("status").GetString());

            var late = await RsvpAsync(guest, token, Reply(view, Statuses(("Sara", "declined"))));
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            var closed = await late.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("rsvp_closed", closed.GetProperty("error").GetString());
            Assert.Equal("attending", GuestRow(closed.GetProperty("invitation"), "Sara").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task A_retired_questions_answer_stays_the_hosts_and_leaves_the_guests_form()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var arrival = await AddQuestionAsync(owner, partyId, "Come arrivi?", "short_text");
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token), Statuses(("Sara", "attending")),
            answers: [new { questionId = arrival, value = "In treno" }]))).EnsureSuccessStatusCode();

        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{arrival}", new
        {
            prompt = "Come arrivi?", kind = "short_text", required = false, isActive = false, version = 1,
        })).EnsureSuccessStatusCode();

        var view = await ViewAsync(guest, token);
        Assert.Equal(0, view.GetProperty("invitation").GetProperty("questions").GetArrayLength());
        (await RsvpAsync(guest, token, Reply(view, Statuses(("Sara", "attending"))))).EnsureSuccessStatusCode();

        var answers = Group(await GuestListAsync(owner, partyId), "Sara").GetProperty("answers").EnumerateArray().ToList();
        var kept = Assert.Single(answers);
        Assert.Equal(arrival, kept.GetProperty("questionId").GetGuid());
        Assert.Equal("In treno", kept.GetProperty("value").GetString());
    }

    [Fact]
    public async Task The_counts_mean_exactly_what_they_say()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var mario = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 1, "Mario")).GetProperty("id").GetGuid();
        var couple = (await AddGroupAsync(owner, partyId, "Anna e Luca", "anna@example.com", 0, "Anna", "Luca")).GetProperty("id").GetGuid();
        var family = (await AddGroupAsync(owner, partyId, "Famiglia Bianchi", "bianchi@example.com", 0, "Paolo", "Marta", "Nico"))
            .GetProperty("id").GetGuid();
        var sara = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var guest = _factory.CreateClient();

        async Task Answer(Guid groupId, Dictionary<string, string> statuses, object[]? extras = null)
        {
            var token = await InviteAsync(_factory, owner, partyId, groupId);
            (await RsvpAsync(guest, token, Reply(await ViewAsync(guest, token), statuses, extras)))
                .EnsureSuccessStatusCode();
        }
        await Answer(mario, Statuses(("Mario", "attending")), [new { name = "Giulia" }]);
        await Answer(couple, Statuses(("Anna", "attending"), ("Luca", "declined")));
        await Answer(sara, Statuses(("Sara", "declined")));
        await InviteAsync(_factory, owner, partyId, family);

        var list = await GuestListAsync(owner, partyId);
        var summary = list.GetProperty("summary");
        Assert.Equal(4, summary.GetProperty("groups").GetInt32());
        Assert.Equal(7, summary.GetProperty("invited").GetInt32());          // named guests
        Assert.Equal(3, summary.GetProperty("missingResponses").GetInt32()); // named, pending
        Assert.Equal(3, summary.GetProperty("attending").GetInt32());        // Mario, Giulia, Anna
        Assert.Equal(2, summary.GetProperty("declined").GetInt32());         // Luca, Sara
        Assert.Equal(3, summary.GetProperty("expectedPeople").GetInt32());
        Assert.Equal(1, summary.GetProperty("unansweredGroups").GetInt32());

        var marioGroup = Group(list, "Mario");
        Assert.Equal(2, marioGroup.GetProperty("attendingCount").GetInt32());
        Assert.Equal(1, marioGroup.GetProperty("additionalGuestsUsed").GetInt32());
        Assert.True(Group(list, "Famiglia Bianchi").GetProperty("canRemind").GetBoolean());
        Assert.False(Group(list, "Sara").GetProperty("canRemind").GetBoolean());
    }
}

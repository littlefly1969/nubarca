using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The guest list against the rest of the party's life: tearing it down,
/// duplicating it, and every public surface it must never appear on.
/// </summary>
public sealed class PartyGuestListLifecycleTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    /// <summary>A group that has been invited, has replied with a +1, and answered a question.</summary>
    private async Task<string> SeedAnsweredGroupAsync(HttpClient owner, Guid partyId, string label)
    {
        var question = await AddQuestionAsync(owner, partyId, $"Domanda di {label}?", "yes_no");
        var groupId = (await AddGroupAsync(owner, partyId, label, $"{Guid.NewGuid():N}@example.com", 1, label))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token),
            new Dictionary<string, string> { [label] = "attending" },
            additionalGuests: [new { name = $"Ospite di {label}" }],
            answers: [new { questionId = question, value = true }]))).EnsureSuccessStatusCode();
        return token;
    }

    [Fact]
    public async Task Tearing_a_party_down_erases_its_guest_list_and_nothing_of_another_party()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var doomed = await CreatePartyAsync(owner, "Da smontare");
        var kept = await CreatePartyAsync(owner, "Da tenere");
        var doomedToken = await SeedAnsweredGroupAsync(owner, doomed, "Mario");
        var keptToken = await SeedAnsweredGroupAsync(owner, kept, "Sara");

        var version = (await GetPartyAsync(owner, doomed)).GetProperty("version").GetInt32();
        (await owner.DeleteAsync($"/api/parties/{doomed}?version={version}")).EnsureSuccessStatusCode();

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party-invitations/{doomedToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party-invitations/{keptToken}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // What is left is exactly the other party's: one group, Sara and her
        // +1, their two answers to one question, one email.
        Assert.Equal(kept, (await db.PartyInvitationGroups.SingleAsync()).PartyId);
        Assert.Equal(2, await db.PartyGuests.CountAsync());
        Assert.Equal(2, await db.PartyRsvps.CountAsync());
        Assert.Single(await db.PartyRsvpAnswers.ToListAsync());
        Assert.Single(await db.PartyInvitationDeliveries.ToListAsync());
        Assert.Equal(kept, (await db.PartyRsvpQuestions.SingleAsync()).PartyId);
    }

    [Fact]
    public async Task A_duplicate_asks_the_same_active_questions_and_invites_nobody()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Festa di Anna");
        await OpenPublicQrAsync(owner, partyId);
        var menu = await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", required: true,
            options: ["Carne", "Pesce"]);
        var retired = await AddQuestionAsync(owner, partyId, "Vecchia domanda?", "short_text");
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/rsvp-questions/{retired}", new
        {
            prompt = "Vecchia domanda?", kind = "short_text", required = false, isActive = false, version = 1,
        })).EnsureSuccessStatusCode();
        var groupId = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 1, "Mario")).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token), new Dictionary<string, string> { ["Mario"] = "attending" },
            additionalGuests: [new { name = "Giulia" }],
            answers: [new { questionId = menu, value = "Carne" }]))).EnsureSuccessStatusCode();
        var before = await owner.GetStringAsync($"/api/parties/{partyId}/guest-list");

        var copy = await owner.PostAsJsonAsync($"/api/parties/{partyId}/duplicate", new { title = "Di nuovo" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var cloneId = (await copy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var clone = await GuestListAsync(owner, cloneId);
        Assert.Equal(0, clone.GetProperty("groups").GetArrayLength());
        var question = Assert.Single(clone.GetProperty("questions").EnumerateArray().ToList());
        Assert.NotEqual(menu, question.GetProperty("id").GetGuid());
        Assert.Equal("Carne o pesce?", question.GetProperty("prompt").GetString());
        Assert.Equal("single_choice", question.GetProperty("kind").GetString());
        Assert.True(question.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "Carne", "Pesce" }, question.GetProperty("options").EnumerateArray().Select(o => o.GetString()!));
        Assert.True(question.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, question.GetProperty("version").GetInt32());
        Assert.Equal(0, question.GetProperty("answerCount").GetInt32());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.PartyInvitationGroups.AnyAsync(g => g.PartyId == cloneId));
            // Everything the evening produced exists once — the original's.
            Assert.Single(await db.PartyInvitationGroups.ToListAsync());
            Assert.Equal(2, await db.PartyGuests.CountAsync());
            Assert.Equal(2, await db.PartyRsvps.CountAsync());
            Assert.Single(await db.PartyRsvpAnswers.ToListAsync());
            Assert.Single(await db.PartyInvitationDeliveries.ToListAsync());
        }

        // The original is untouched, and its link still opens it.
        Assert.Equal(before, await owner.GetStringAsync($"/api/parties/{partyId}/guest-list"));
        Assert.Equal("Mario", (await ViewAsync(guest, token)).GetProperty("invitation").GetProperty("label").GetString());
    }

    [Fact]
    public async Task No_public_party_surface_carries_the_guest_list()
    {
        // A Member: every non-administrative permission, the television's
        // included, so the same host can pair the screen the party is on.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"host-{Guid.NewGuid():N}@example.com");
        var partyId = await CreatePartyAsync(owner, "Matrimonio di Marta");
        var (albumId, viewToken, _) = await OpenPublicQrAsync(owner, partyId);
        await AddPhotoAsync(owner, albumId);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 300, maxChallengeIntervalSeconds = 540,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        var secretQuestion = await AddQuestionAsync(owner, partyId, "Allergie segrete?", "short_text");
        var created = await owner.PostAsJsonAsync($"/api/parties/{partyId}/invitation-groups", GroupBody(
            "Zenobia Quartararo", "zenobia.quartararo@example.org", 1,
            [new { name = "Zenobia Quartararo" }, new { name = "Ottone Pellegrini" }],
            phone: "+39 333 000 1111"));
        created.EnsureSuccessStatusCode();
        var groupId = Group(await created.Content.ReadFromJsonAsync<JsonElement>(), "Zenobia Quartararo")
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);
        (await RsvpAsync(guest, token, new
        {
            version = Version(view),
            guests = new object[]
            {
                new { guestId = GuestId(view, "Zenobia Quartararo"), status = "attending", dietaryNotes = "allergia-arachidi-XYZ" },
                new { guestId = GuestId(view, "Ottone Pellegrini"), status = "declined", dietaryNotes = (string?)null },
            },
            additionalGuests = new[] { new { name = "Ippolita Sforzesca", dietaryNotes = (string?)null } },
            answers = new object[] { new { questionId = secretQuestion, value = "risposta-segreta-ABC" } },
        })).EnsureSuccessStatusCode();
        await AdvanceAsync(owner, partyId, "start-live");
        var tv = await PairTvAsync(owner);

        var surfaces = new List<string>
        {
            await guest.GetStringAsync($"/api/party/{viewToken}"),
            await guest.GetStringAsync($"/api/party/{viewToken}/items"),
            await guest.GetStringAsync($"/api/party/{viewToken}/game"),
            await guest.GetStringAsync($"/api/party/{viewToken}/game?display=1"),
            await guest.GetStringAsync($"/api/party/{viewToken}/link-preview"),
            await TvGetAsync(tv, "/api/tv/session"),
            await TvGetAsync(tv, "/api/tv/albums"),
            await TvGetAsync(tv, $"/api/tv/albums/{albumId}/items"),
        };
        // The audit trail is the HOST's record, so it may name a group by id —
        // but never a person, an address, a note, an answer or a link.
        List<string> auditLines;
        using (var scope = _factory.Services.CreateScope())
        {
            auditLines = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
                .Select(a => a.MetadataJson ?? string.Empty).ToListAsync();
        }

        var secrets = new[]
        {
            "Zenobia", "Quartararo", "Ottone", "Pellegrini", "Ippolita", "zenobia.quartararo@example.org",
            "333 000 1111", "allergia-arachidi-XYZ", "risposta-segreta-ABC", "Allergie segrete", token,
        };
        foreach (var text in surfaces.Concat(auditLines))
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }
        // And no public surface so much as has a field for any of it.
        var fieldNames = new[] { "rsvp", "dietary", "recipientEmail", "guestList", "invitationGroup" };
        foreach (var surface in surfaces)
        {
            foreach (var field in fieldNames)
            {
                Assert.DoesNotContain(field, surface, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // --- Attendance against the party's life ---------------------------------------

    /// <summary>A live party with a QR, one group checked in by its own link and by the host, and somebody else.</summary>
    private async Task<(string Token, Guid AlbumId, string ViewToken)> SeedArrivalsAsync(
        HttpClient owner, Guid partyId, string label, string otherName)
    {
        var (albumId, viewToken, _) = await OpenPublicQrAsync(owner, partyId, $"Album di {label}");
        var groupId = (await AddGroupAsync(owner, partyId, label, $"{Guid.NewGuid():N}@example.com", 0, label, $"{label} Due"))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        await AdvanceAsync(owner, partyId, "start-live");
        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);
        (await guest.PutAsync($"/api/party-invitations/{token}/attendance/guests/{GuestId(view, label)}", null))
            .EnsureSuccessStatusCode();
        (await owner.PutAsync($"/api/parties/{partyId}/attendance/guests/{GuestId(view, $"{label} Due")}", null))
            .EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/attendance/other-guests",
            new { name = otherName, clientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        return (token, albumId, viewToken);
    }

    [Fact]
    public async Task Tearing_a_party_down_erases_its_arrivals_and_nothing_of_another_party()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var doomed = await CreatePartyAsync(owner, "Da smontare");
        var kept = await CreatePartyAsync(owner, "Da tenere");
        await SeedArrivalsAsync(owner, doomed, "Mario", "Walter");
        await SeedArrivalsAsync(owner, kept, "Sara", "Gino");
        await AdvanceAsync(owner, doomed, "end-live");

        var version = (await GetPartyAsync(owner, doomed)).GetProperty("version").GetInt32();
        (await owner.DeleteAsync($"/api/parties/{doomed}?version={version}")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(kept, (await db.PartyAttendanceGuests.SingleAsync()).PartyId);
        Assert.Equal(2, await db.PartyGuestAttendances.CountAsync());
        Assert.Equal(kept, (await db.PartyInvitationGroups.SingleAsync()).PartyId);
    }

    [Fact]
    public async Task Taking_a_person_or_a_group_off_the_list_takes_their_arrival_with_them()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await SeedArrivalsAsync(owner, partyId, "Mario", "Walter");
        var group = Group(await GuestListAsync(owner, partyId), "Mario");
        var mario = OwnerGuest(group, "Mario");

        // The host edits "Mario Due" off the group: his arrival goes with him.
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/invitation-groups/{group.GetProperty("id").GetGuid()}",
            GroupBody("Mario", group.GetProperty("recipientEmail").GetString()!, 0,
                [new { id = mario.GetProperty("id").GetGuid(), name = "Mario" }],
                version: group.GetProperty("version").GetInt32()))).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(mario.GetProperty("id").GetGuid(), (await db.PartyGuestAttendances.SingleAsync()).PartyGuestId);
        }

        // And removing the whole group removes the last one. Nobody else's arrival moves.
        var current = Group(await GuestListAsync(owner, partyId), "Mario");
        (await owner.DeleteAsync(
            $"/api/parties/{partyId}/invitation-groups/{current.GetProperty("id").GetGuid()}?version={current.GetProperty("version").GetInt32()}"))
            .EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.PartyGuestAttendances.CountAsync());
            Assert.Equal("Walter", (await db.PartyAttendanceGuests.SingleAsync()).Name);
        }
    }

    [Fact]
    public async Task A_duplicate_carries_no_arrival()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Festa di Anna");
        await SeedArrivalsAsync(owner, partyId, "Anna", "Walter");
        var before = await owner.GetStringAsync($"/api/parties/{partyId}/attendance");

        var copy = await owner.PostAsJsonAsync($"/api/parties/{partyId}/duplicate", new { title = "Di nuovo" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var cloneId = (await copy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var clone = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{cloneId}/attendance");
        Assert.Equal(0, clone.GetProperty("summary").GetProperty("totalArrivals").GetInt32());
        Assert.Empty(clone.GetProperty("otherGuests").EnumerateArray());
        Assert.Empty(clone.GetProperty("groups").EnumerateArray());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.PartyAttendanceGuests.AnyAsync(g => g.PartyId == cloneId));
            Assert.Equal(2, await db.PartyGuestAttendances.CountAsync());
            Assert.Single(await db.PartyAttendanceGuests.ToListAsync());
        }
        Assert.Equal(before, await owner.GetStringAsync($"/api/parties/{partyId}/attendance"));
    }

    [Fact]
    public async Task No_public_party_surface_carries_attendance()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"host-{Guid.NewGuid():N}@example.com");
        var partyId = await CreatePartyAsync(owner, "Matrimonio di Marta");
        var (albumId, viewToken, _) = await OpenPublicQrAsync(owner, partyId);
        await AddPhotoAsync(owner, albumId);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 300, maxChallengeIntervalSeconds = 540,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        var arrived = (await AddGroupAsync(owner, partyId, "Zenobia", "zenobia@example.org", 0, "Zenobia Quartararo"))
            .GetProperty("id").GetGuid();
        var neighbour = (await AddGroupAsync(owner, partyId, "Vicini", "vicini@example.org", 0, "Ottone Pellegrini"))
            .GetProperty("id").GetGuid();
        var arrivedToken = await InviteAsync(_factory, owner, partyId, arrived);
        var neighbourToken = await InviteAsync(_factory, owner, partyId, neighbour);
        await AdvanceAsync(owner, partyId, "start-live");
        var guest = _factory.CreateClient();
        (await guest.PutAsync(
            $"/api/party-invitations/{arrivedToken}/attendance/guests/{GuestId(await ViewAsync(guest, arrivedToken), "Zenobia Quartararo")}",
            null)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/attendance/other-guests",
            new { name = "Ippolita Sforzesca", clientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var tv = await PairTvAsync(owner);

        var surfaces = new List<string>
        {
            await guest.GetStringAsync($"/api/party/{viewToken}"),
            await guest.GetStringAsync($"/api/party/{viewToken}/items"),
            await guest.GetStringAsync($"/api/party/{viewToken}/game"),
            await guest.GetStringAsync($"/api/party/{viewToken}/game?display=1"),
            await guest.GetStringAsync($"/api/party/{viewToken}/link-preview"),
            await TvGetAsync(tv, "/api/tv/session"),
            await TvGetAsync(tv, "/api/tv/albums"),
            await TvGetAsync(tv, $"/api/tv/albums/{albumId}/items"),
        };
        // ANOTHER group's personal invitation sees none of it either.
        var neighbourView = await guest.GetStringAsync($"/api/party-invitations/{neighbourToken}");
        List<string> auditLines;
        using (var scope = _factory.Services.CreateScope())
        {
            auditLines = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
                .Select(a => a.MetadataJson ?? string.Empty).ToListAsync();
        }

        var secrets = new[] { "Zenobia", "Quartararo", "Ippolita", "Sforzesca" };
        foreach (var text in surfaces.Append(neighbourView).Concat(auditLines))
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }
        foreach (var surface in surfaces)
        {
            foreach (var field in new[] { "attendance", "checkedIn", "otherGuests", "arrival" })
            {
                Assert.DoesNotContain(field, surface, StringComparison.OrdinalIgnoreCase);
            }
        }
        // The neighbour's own person has not arrived, and it is all its link says.
        Assert.Contains("\"checkedInAt\":null", neighbourView);
        Assert.DoesNotContain("otherGuests", neighbourView);
    }

    // --- helpers ------------------------------------------------------------------

    private static async Task AddPhotoAsync(HttpClient owner, Guid albumId)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", "a.png" } });
        upload.EnsureSuccessStatusCode();
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId })).EnsureSuccessStatusCode();
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = "URDLSUDLR",
                personalCodeConfirmation = "URDLSUDLR",
            })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        var setCookie = response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        return setCookie[(setCookie.IndexOf('=') + 1)..];
    }

    private async Task<string> TvGetAsync(string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={cookie}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

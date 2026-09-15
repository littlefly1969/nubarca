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
/// Cooperative attendance: a group says "Sono qui" for its own people from its
/// personal invitation, and "Entra nel Party" takes the phone to the party's
/// ordinary public page.
///
/// <para>The flow is Invitation → group → guest → arrival, and it stays apart
/// from QR → participant. Both halves are pinned down here: the personal link
/// records its own people's arrival and nobody else's, only while the party is
/// live, converging with the host's check-in on the same row; and entering the
/// party mints a participant exactly the way scanning the room's QR does — same
/// capability, same quotas — with no binding between the two.</para>
/// </summary>
public sealed class PartyAttendanceInvitationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_group_says_sono_qui_for_its_own_people_while_the_party_is_live_and_no_rsvp_moves()
    {
        var party = await InvitedAsync(withQr: false);
        await AdvanceAsync(party.Owner, party.PartyId, "start-live");
        var phone = _factory.CreateClient();
        var view = await ViewAsync(phone, party.RossiToken);
        Assert.True(view.GetProperty("invitation").GetProperty("canCheckIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, GuestRow(view, "Mario").GetProperty("checkedInAt").ValueKind);
        var rsvpBefore = await party.Owner.GetStringAsync($"/api/parties/{party.PartyId}/guest-list");

        var mario = await Ok(await SelfCheckInAsync(phone, party.RossiToken, GuestId(view, "Mario")));
        Assert.Equal("invitation", GuestRow(mario, "Mario").GetProperty("checkInSource").GetString());
        Assert.NotEqual(JsonValueKind.Null, GuestRow(mario, "Mario").GetProperty("checkedInAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, GuestRow(mario, "Laura").GetProperty("checkedInAt").ValueKind);

        // She said no, and came: the arrival is recorded and the answer stays hers.
        var laura = await Ok(await SelfCheckInAsync(phone, party.RossiToken, GuestId(view, "Laura")));
        Assert.Equal("declined", GuestRow(laura, "Laura").GetProperty("status").GetString());

        // The host sees both, marked as recorded from the invitation.
        var owner = await OwnerAttendanceAsync(party.Owner, party.PartyId);
        Assert.Equal("invitation", OwnerGuest(owner, "Mario").GetProperty("checkInSource").GetString());
        Assert.Equal(2, owner.GetProperty("summary").GetProperty("totalArrivals").GetInt32());
        Assert.Equal(1, owner.GetProperty("summary").GetProperty("unexpectedKnownGuests").GetInt32());

        Assert.Equal(rsvpBefore, await party.Owner.GetStringAsync($"/api/parties/{party.PartyId}/guest-list"));
        Assert.Equal(0, await CountAsync(db => db.PartyParticipants.CountAsync()));
        // The audit line is anonymous, carries ids and the source, and names nobody.
        var lines = await AuditAsync("party.attendance.check_in");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.Null(l.UserId);
            Assert.Contains("\"invitation\"", l.Metadata);
            Assert.DoesNotContain("Mario", l.Metadata);
            Assert.DoesNotContain("Laura", l.Metadata);
            Assert.DoesNotContain(party.RossiToken, l.Metadata);
        });
    }

    [Fact]
    public async Task Repeating_sono_qui_changes_nothing_and_the_first_arrival_keeps_its_moment_and_its_source()
    {
        var party = await InvitedAsync(withQr: false);
        await AdvanceAsync(party.Owner, party.PartyId, "start-live");
        var phone = _factory.CreateClient();
        var view = await ViewAsync(phone, party.RossiToken);
        var mario = GuestId(view, "Mario");
        var laura = GuestId(view, "Laura");

        var first = await Ok(await SelfCheckInAsync(phone, party.RossiToken, mario));
        var at = GuestRow(first, "Mario").GetProperty("checkedInAt").GetDateTime();
        await Task.Delay(30);
        await Ok(await SelfCheckInAsync(phone, party.RossiToken, mario));
        // The host's tap on somebody already here keeps the guest's moment and source.
        await Ok(await OwnerCheckInAsync(party.Owner, party.PartyId, mario));
        // And the other way round: the host first, the guest after.
        await Ok(await OwnerCheckInAsync(party.Owner, party.PartyId, laura));
        await Ok(await SelfCheckInAsync(phone, party.RossiToken, laura));

        var rows = await ScalarAsync(db => db.PartyGuestAttendances.AsNoTracking().ToListAsync());
        Assert.Equal(2, rows.Count);
        var marioRow = rows.Single(r => r.PartyGuestId == mario);
        Assert.Equal(PartyAttendanceSources.Invitation, marioRow.Source);
        Assert.Equal(at, marioRow.CheckedInAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(PartyAttendanceSources.Owner, rows.Single(r => r.PartyGuestId == laura).Source);
        // One event per person: repeating was never an event.
        Assert.Equal(2, (await AuditAsync("party.attendance.check_in")).Count);
    }

    [Fact]
    public async Task A_group_takes_back_only_its_own_sono_qui_and_only_while_the_party_is_live()
    {
        var party = await InvitedAsync(withQr: false);
        await AdvanceAsync(party.Owner, party.PartyId, "start-live");
        var phone = _factory.CreateClient();
        var view = await ViewAsync(phone, party.RossiToken);
        var mario = GuestId(view, "Mario");
        var laura = GuestId(view, "Laura");

        await Ok(await SelfCheckInAsync(phone, party.RossiToken, mario));
        var undone = await Ok(await SelfUndoAsync(phone, party.RossiToken, mario));
        Assert.Equal(JsonValueKind.Null, GuestRow(undone, "Mario").GetProperty("checkedInAt").ValueKind);
        // A second undo is an undo already done.
        await Ok(await SelfUndoAsync(phone, party.RossiToken, mario));

        // The host's record is the host's to correct.
        await Ok(await OwnerCheckInAsync(party.Owner, party.PartyId, laura));
        var refused = await SelfUndoAsync(phone, party.RossiToken, laura);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("attendance_recorded_by_host", body.GetProperty("error").GetString());
        Assert.Equal("owner", GuestRow(body.GetProperty("invitation"), "Laura").GetProperty("checkInSource").GetString());
        Assert.Equal(1, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));

        // After the party the group's buttons are gone; the host still corrects.
        await Ok(await SelfCheckInAsync(phone, party.RossiToken, mario));
        await AdvanceAsync(party.Owner, party.PartyId, "end-live");
        foreach (var closed in new[]
        {
            await SelfUndoAsync(phone, party.RossiToken, mario),
            await SelfCheckInAsync(phone, party.RossiToken, laura),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
            Assert.Equal("attendance_not_open", await ErrorOf(closed));
        }
        (await party.Owner.DeleteAsync($"/api/parties/{party.PartyId}/attendance/guests/{mario}")).EnsureSuccessStatusCode();
        Assert.Equal(1, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));
    }

    [Fact]
    public async Task Sono_qui_is_refused_in_draft_published_and_ended()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 0, "Mario"))
            .GetProperty("id").GetGuid();
        var phone = _factory.CreateClient();

        // DRAFT: the personal link opens nothing yet, so there is nothing to answer.
        var draftToken = CurrentToken(_factory, groupId);
        var mario = await ScalarAsync(db => db.PartyGuests.Where(g => g.PartyInvitationGroupId == groupId)
            .Select(g => g.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await SelfCheckInAsync(phone, draftToken, mario)).StatusCode);

        // PUBLISHED: the invitation is open, arriving is not.
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var published = await SelfCheckInAsync(phone, token, mario);
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        var body = await published.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("attendance_not_open", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("invitation").GetProperty("invitation").GetProperty("canCheckIn").GetBoolean());

        // LIVE: yes.
        await AdvanceAsync(owner, partyId, "start-live");
        await Ok(await SelfCheckInAsync(phone, token, mario));
        await Ok(await SelfUndoAsync(phone, token, mario));

        // ENDED: no — and the party stayed exactly where the host put it.
        await AdvanceAsync(owner, partyId, "end-live");
        var ended = await SelfCheckInAsync(phone, token, mario);
        Assert.Equal(HttpStatusCode.Conflict, ended.StatusCode);
        Assert.Equal("attendance_not_open", await ErrorOf(ended));
        Assert.Equal(0, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));
        Assert.Equal("ended", (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_personal_link_reaches_its_own_people_and_sees_nobody_elses_arrival()
    {
        var party = await InvitedAsync(withQr: true);
        await AdvanceAsync(party.Owner, party.PartyId, "start-live");
        var phone = _factory.CreateClient();
        var sara = OwnerGuestId(await OwnerAttendanceAsync(party.Owner, party.PartyId), "Sara Verdi");

        // Another host's live party, with a guest of its own.
        var (_, bob) = await NewHostAsync(_factory);
        var bobParty = await CreatePartyAsync(bob, "Festa di Bob");
        var bobGroup = (await AddGroupAsync(bob, bobParty, "Ottavia", "ottavia@example.com", 0, "Ottavia"))
            .GetProperty("id").GetGuid();
        await InviteAsync(_factory, bob, bobParty, bobGroup);
        await AdvanceAsync(bob, bobParty, "start-live");
        var ottavia = OwnerGuestId(await OwnerAttendanceAsync(bob, bobParty), "Ottavia");

        // Another group of the same party, another party, and nobody at all:
        // one generic not-found, and nothing written.
        foreach (var foreign in new[] { sara, ottavia, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SelfCheckInAsync(phone, party.RossiToken, foreign)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SelfUndoAsync(phone, party.RossiToken, foreign)).StatusCode);
        }
        Assert.Equal(0, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));

        // Other people arrive: Sara, checked in by the host, and somebody not on the list.
        await Ok(await OwnerCheckInAsync(party.Owner, party.PartyId, sara));
        (await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/attendance/other-guests",
            new { name = "Walter Esterno", clientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var own = await Ok(await SelfCheckInAsync(phone, party.RossiToken, GuestId(await ViewAsync(phone, party.RossiToken), "Mario")));
        var raw = own.GetRawText();
        foreach (var secret in new[] { "Sara", "Verdi", "Walter", "Esterno", "Ottavia" })
        {
            Assert.DoesNotContain(secret, raw);
        }
        foreach (var field in new[] { "summary", "otherGuests", "totalArrivals", "expectedPeople", "attendanceGuest" })
        {
            Assert.DoesNotContain(field, raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(new[] { "Mario", "Laura" },
            own.GetProperty("invitation").GetProperty("guests").EnumerateArray().Select(g => g.GetProperty("name").GetString()));

        // Nor does the personal token reach the host's list, or any other attendance route.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await phone.GetAsync($"/api/parties/{party.PartyId}/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await phone.GetAsync($"/api/party-invitations/{party.RossiToken}/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await phone.PutAsync($"/api/party/{party.ViewToken}/attendance/guests/{sara}", null)).StatusCode);
    }

    // --- "Entra nel Party" -----------------------------------------------------------

    [Fact]
    public async Task Entering_the_party_from_the_invitation_is_the_rooms_own_qr_and_binds_nobody()
    {
        var party = await InvitedAsync(withQr: true);
        await AdvanceAsync(party.Owner, party.PartyId, "start-live");

        // Mario's phone: his invitation, then "Sono qui".
        var marioPhone = _factory.CreateClient();
        var view = await ViewAsync(marioPhone, party.RossiToken);
        var partyUrl = view.GetProperty("party").GetProperty("partyUrl").GetString();
        // The room's own public address — not a copy of it and not a variant.
        Assert.Equal($"/party/{party.ViewToken}", partyUrl);
        await Ok(await SelfCheckInAsync(marioPhone, party.RossiToken, GuestId(view, "Mario")));
        Assert.Equal(1, await ArrivalsAsync());
        Assert.Equal(0, await CountAsync(db => db.PartyParticipants.CountAsync()));

        // "Entra nel Party": the public page answers him exactly as it answers a
        // phone that scanned the QR.
        var qrPhone = _factory.CreateClient();
        var token = partyUrl!["/party/".Length..];
        var fromInvitation = await marioPhone.GetStringAsync($"/api/party/{token}");
        Assert.Equal(await qrPhone.GetStringAsync($"/api/party/{party.ViewToken}"), fromInvitation);
        var contributionUrl = JsonDocument.Parse(fromInvitation).RootElement
            .GetProperty("capabilities").GetProperty("contributionUrl").GetString()!;
        var uploadToken = contributionUrl["/party/".Length..^"/upload".Length];

        // He becomes a participant the ordinary way — and his arrival is untouched.
        var marioSession = await Ok(await marioPhone.PostAsync($"/api/party/{uploadToken}/upload-session", null));
        Assert.Equal(1, await CountAsync(db => db.PartyParticipants.CountAsync()));
        Assert.Equal(1, await ArrivalsAsync());
        // The same browser keeps its one participant.
        await Ok(await marioPhone.PostAsync($"/api/party/{uploadToken}/upload-session", null));
        Assert.Equal(1, await CountAsync(db => db.PartyParticipants.CountAsync()));

        // A phone that only scanned the QR: another participant, the same
        // quotas, and still no arrival for anybody.
        var qrSession = await Ok(await qrPhone.PostAsync($"/api/party/{uploadToken}/upload-session", null));
        Assert.Equal(2, await CountAsync(db => db.PartyParticipants.CountAsync()));
        Assert.Equal(qrSession.GetRawText(), marioSession.GetRawText());
        Assert.Equal(1, await ArrivalsAsync());
        // No participant carries anything of the guest list, and no arrival anything of a browser.
        Assert.Equal(PartyAttendanceSources.Invitation,
            await ScalarAsync(db => db.PartyGuestAttendances.Select(a => a.Source).SingleAsync()));
    }

    [Fact]
    public async Task Enter_the_party_is_offered_only_while_the_party_page_really_opens_and_sono_qui_never_needs_it()
    {
        // Published, with a QR: not yet.
        var withQr = await InvitedAsync(withQr: true);
        var phone = _factory.CreateClient();
        Assert.Equal(JsonValueKind.Null,
            (await ViewAsync(phone, withQr.RossiToken)).GetProperty("party").GetProperty("partyUrl").ValueKind);

        // Live, with no QR at all: no button — and "Sono qui" works regardless.
        var noQr = await InvitedAsync(withQr: false);
        await AdvanceAsync(noQr.Owner, noQr.PartyId, "start-live");
        var live = await ViewAsync(phone, noQr.RossiToken);
        Assert.Equal(JsonValueKind.Null, live.GetProperty("party").GetProperty("partyUrl").ValueKind);
        await Ok(await SelfCheckInAsync(phone, noQr.RossiToken, GuestId(live, "Mario")));

        // Live with a QR the host then switches off: the button goes with it,
        // and arriving still works.
        await AdvanceAsync(withQr.Owner, withQr.PartyId, "start-live");
        Assert.NotEqual(JsonValueKind.Null,
            (await ViewAsync(phone, withQr.RossiToken)).GetProperty("party").GetProperty("partyUrl").ValueKind);
        (await withQr.Owner.PatchAsJsonAsync(
            $"/api/albums/{withQr.AlbumId}/party-settings", new { enabled = false })).EnsureSuccessStatusCode();
        var switchedOff = await ViewAsync(phone, withQr.RossiToken);
        Assert.Equal(JsonValueKind.Null, switchedOff.GetProperty("party").GetProperty("partyUrl").ValueKind);
        await Ok(await SelfCheckInAsync(phone, withQr.RossiToken, GuestId(switchedOff, "Mario")));
    }

    // --- helpers --------------------------------------------------------------------------

    private sealed record InvitedParty(
        HttpClient Owner, Guid PartyId, string RossiToken, Guid? AlbumId, string? ViewToken);

    /// <summary>
    /// "Famiglia Rossi" (Mario attending, Laura declined) and "Sara Verdi"
    /// (invited, never answered), published by the first invitation — or by the
    /// QR, when the party has one.
    /// </summary>
    private async Task<InvitedParty> InvitedAsync(bool withQr)
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var rossi = (await AddGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", 0, "Mario", "Laura"))
            .GetProperty("id").GetGuid();
        var verdi = (await AddGroupAsync(owner, partyId, "Sara Verdi", "sara@example.com", 0, "Sara Verdi"))
            .GetProperty("id").GetGuid();
        Guid? albumId = null;
        string? viewToken = null;
        if (withQr)
        {
            var album = await owner.PostAsJsonAsync("/api/albums", new { name = $"Album {Guid.NewGuid():N}" });
            album.EnsureSuccessStatusCode();
            albumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var version = (await GetPartyAsync(owner, partyId)).GetProperty("version").GetInt32();
            (await owner.PutAsJsonAsync($"/api/parties/{partyId}/media/main", new { albumId, version }))
                .EnsureSuccessStatusCode();
            var settings = await owner.PatchAsJsonAsync(
                $"/api/albums/{albumId}/party-settings", new { enabled = true, uploadEnabled = true });
            settings.EnsureSuccessStatusCode();
            viewToken = (await settings.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("partyUrl").GetString()!["/party/".Length..];
        }
        var token = await InviteAsync(_factory, owner, partyId, rossi);
        await InviteAsync(_factory, owner, partyId, verdi);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token),
            new Dictionary<string, string> { ["Mario"] = "attending", ["Laura"] = "declined" }))).EnsureSuccessStatusCode();
        return new InvitedParty(owner, partyId, token, albumId, viewToken);
    }

    private static Task<HttpResponseMessage> SelfCheckInAsync(HttpClient phone, string token, Guid guestId) =>
        phone.PutAsync($"/api/party-invitations/{token}/attendance/guests/{guestId}", null);

    private static Task<HttpResponseMessage> SelfUndoAsync(HttpClient phone, string token, Guid guestId) =>
        phone.DeleteAsync($"/api/party-invitations/{token}/attendance/guests/{guestId}");

    private static Task<HttpResponseMessage> OwnerCheckInAsync(HttpClient owner, Guid partyId, Guid guestId) =>
        owner.PutAsync($"/api/parties/{partyId}/attendance/guests/{guestId}", null);

    private static async Task<JsonElement> OwnerAttendanceAsync(HttpClient owner, Guid partyId) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/attendance");

    private static JsonElement OwnerGuest(JsonElement attendance, string name) =>
        attendance.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("guests").EnumerateArray())
            .Single(g => g.GetProperty("name").GetString() == name);

    private static Guid OwnerGuestId(JsonElement attendance, string name) =>
        OwnerGuest(attendance, name).GetProperty("guestId").GetGuid();

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> ScalarAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> CountAsync(Func<AppDbContext, Task<int>> count) => ScalarAsync(count);

    private Task<int> ArrivalsAsync() => CountAsync(async db =>
        await db.PartyGuestAttendances.CountAsync() + await db.PartyAttendanceGuests.CountAsync());

    private Task<List<(Guid? UserId, string Metadata)>> AuditAsync(string action) => ScalarAsync(async db =>
        (await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == action)
            .Select(a => new { a.UserId, a.MetadataJson })
            .ToListAsync())
        .Select(a => (a.UserId, a.MetadataJson ?? string.Empty)).ToList());
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Who arrived, from the host's side — on ONE Party aggregate, whatever its
/// guest list looks like.
///
/// <para>Three parties run through this file and none of them is a "type": an
/// OPEN party has no invitation group at all and records whoever the host
/// wants to; an INVITED party checks its guests in against what they declared;
/// a MIXED party is an invited one that also records somebody not on its list.
/// What each is follows from its rows. What all three share is the rule this
/// file pins down: an arrival is its own fact. It never moves an RSVP, it is
/// never made by the party's QR, and it never makes, reads or changes a
/// PartyParticipant.</para>
/// </summary>
public sealed class PartyAttendanceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // --- A. The open party ---------------------------------------------------------

    [Fact]
    public async Task An_open_party_runs_its_whole_evening_with_no_guest_list_and_keeps_arrivals_apart()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Festa in giardino");
        Assert.Equal(0, (await GuestListAsync(owner, partyId)).GetProperty("groups").GetArrayLength());

        // The QR is the whole of how guests arrive, and opening it publishes.
        var (viewToken, uploadToken) = await OpenPublicQrWithUploadsAsync(owner, partyId);
        Assert.Equal("published", await StatusAsync(owner, partyId));

        // Before the party nobody can have arrived — and asking publishes and
        // starts nothing.
        var before = await AttendanceAsync(owner, partyId);
        Assert.False(before.GetProperty("canEdit").GetBoolean());
        AssertSummary(before, expected: 0, expectedArrived: 0, missing: 0, unexpectedKnown: 0, others: 0, total: 0);
        var early = await AddOtherAsync(owner, partyId, "Troppo presto");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("attendance_not_open", await ErrorOf(early));
        Assert.Equal("published", await StatusAsync(owner, partyId));

        await AdvanceAsync(owner, partyId, "start-live");

        // THE ORDINARY PUBLIC PARTY: two phones scan the QR, look, and upload.
        // Each becomes an anonymous participant, the normal way, and neither
        // becomes an arrival.
        var firstPhone = _factory.CreateClient();
        var secondPhone = _factory.CreateClient();
        (await firstPhone.GetAsync($"/api/party/{viewToken}")).EnsureSuccessStatusCode();
        (await firstPhone.GetAsync($"/api/party/{viewToken}/items")).EnsureSuccessStatusCode();
        (await GuestUploadAsync(firstPhone, uploadToken)).EnsureSuccessStatusCode();
        (await GuestUploadAsync(secondPhone, uploadToken)).EnsureSuccessStatusCode();
        Assert.Equal(2, await ParticipantsAsync());
        Assert.Equal(0, await ArrivalsAsync());
        var publicFace = await firstPhone.GetStringAsync($"/api/party/{viewToken}");

        // OPTIONAL manual attendance — the host records two people.
        await Ok(await AddOtherAsync(owner, partyId, "Zia Pina"));
        var recorded = await Ok(await AddOtherAsync(owner, partyId, "Zio Gino"));
        Assert.True(recorded.GetProperty("canEdit").GetBoolean());
        Assert.Equal(0, recorded.GetProperty("groups").GetArrayLength());
        AssertSummary(recorded, expected: 0, expectedArrived: 0, missing: 0, unexpectedKnown: 0, others: 2, total: 2);
        // Recording them touched no participant and changed nothing public.
        Assert.Equal(2, await ParticipantsAsync());
        Assert.Equal(publicFace, await firstPhone.GetStringAsync($"/api/party/{viewToken}"));

        // AFTER the party the host still corrects: a rename, a forgotten
        // arrival, a mistaken one removed.
        await AdvanceAsync(owner, partyId, "end-live");
        var pina = OtherOf(recorded, "Zia Pina");
        await Ok(await RenameOtherAsync(owner, partyId, pina.GetProperty("id").GetGuid(), "Zia Giuseppina", 1));
        await Ok(await AddOtherAsync(owner, partyId, "Cugino dimenticato"));
        var corrected = await Ok(await owner.DeleteAsync(
            $"{AttendanceUrl(partyId)}/other-guests/{OtherOf(recorded, "Zio Gino").GetProperty("id").GetGuid()}"));
        Assert.Equal("ended", corrected.GetProperty("partyStatus").GetString());
        Assert.Equal(
            new[] { "Cugino dimenticato", "Zia Giuseppina" },
            corrected.GetProperty("otherGuests").EnumerateArray().Select(o => o.GetProperty("name").GetString()!).Order());
        AssertSummary(corrected, expected: 0, expectedArrived: 0, missing: 0, unexpectedKnown: 0, others: 2, total: 2);

        // And at no point did the party grow a guest list to make any of it work.
        Assert.Equal(0, await CountAsync(db => db.PartyInvitationGroups.CountAsync()));
        Assert.Equal(0, await CountAsync(db => db.PartyGuests.CountAsync()));
        Assert.Equal(0, await CountAsync(db => db.PartyRsvps.CountAsync()));
        Assert.Equal(2, await ParticipantsAsync());
    }

    // --- B. The invited party --------------------------------------------------------

    [Fact]
    public async Task An_invited_party_counts_who_was_expected_and_who_came_and_never_rewrites_an_rsvp()
    {
        var (owner, partyId) = await InvitedLivePartyAsync();
        var start = await AttendanceAsync(owner, partyId);
        Assert.True(start.GetProperty("canEdit").GetBoolean());
        AssertSummary(start, expected: 2, expectedArrived: 0, missing: 2, unexpectedKnown: 0, others: 0, total: 0);
        Assert.Equal("attending", GuestOf(start, "Mario").GetProperty("rsvpStatus").GetString());
        Assert.Equal("declined", GuestOf(start, "Laura").GetProperty("rsvpStatus").GetString());
        Assert.Equal("pending", GuestOf(start, "Sara").GetProperty("rsvpStatus").GetString());
        Assert.True(GuestOf(start, "Giulia").GetProperty("isAdditionalGuest").GetBoolean());
        // The door sees names and arrivals — none of the list's contact details.
        var raw = start.GetRawText();
        foreach (var field in new[] { "email", "phone", "dietary", "answers", "recipientEmail" })
        {
            Assert.DoesNotContain(field, raw, StringComparison.OrdinalIgnoreCase);
        }
        var rsvpBefore = await owner.GetStringAsync($"/api/parties/{partyId}/guest-list");

        // attending + arrived
        var now = await Ok(await CheckInAsync(owner, partyId, GuestIdOf(start, "Mario")));
        AssertSummary(now, expected: 2, expectedArrived: 1, missing: 1, unexpectedKnown: 0, others: 0, total: 1);
        // declined + arrived: the RSVP is what she SAID, and it stays said.
        now = await Ok(await CheckInAsync(owner, partyId, GuestIdOf(start, "Laura")));
        AssertSummary(now, expected: 2, expectedArrived: 1, missing: 1, unexpectedKnown: 1, others: 0, total: 2);
        Assert.Equal("declined", GuestOf(now, "Laura").GetProperty("rsvpStatus").GetString());
        // pending + arrived
        now = await Ok(await CheckInAsync(owner, partyId, GuestIdOf(start, "Sara")));
        AssertSummary(now, expected: 2, expectedArrived: 1, missing: 1, unexpectedKnown: 2, others: 0, total: 3);
        // A +1 is a person of their own, checked in on their own.
        Assert.Null(GuestCheckedInAt(now, "Giulia"));
        now = await Ok(await CheckInAsync(owner, partyId, GuestIdOf(start, "Giulia")));
        AssertSummary(now, expected: 2, expectedArrived: 2, missing: 0, unexpectedKnown: 2, others: 0, total: 4);
        Assert.Equal("owner", GuestOf(now, "Giulia").GetProperty("checkInSource").GetString());

        // No RSVP moved: the host's guest list reads exactly as it did.
        Assert.Equal(rsvpBefore, await owner.GetStringAsync($"/api/parties/{partyId}/guest-list"));
        Assert.Empty(now.GetProperty("otherGuests").EnumerateArray());
        Assert.Equal(0, await ParticipantsAsync());
    }

    // --- C. The mixed party ----------------------------------------------------------

    [Fact]
    public async Task An_invited_party_becomes_mixed_by_recording_somebody_not_on_its_list()
    {
        var (owner, partyId) = await InvitedLivePartyAsync();
        var list = await AttendanceAsync(owner, partyId);
        await Ok(await CheckInAsync(owner, partyId, GuestIdOf(list, "Mario")));

        var mixed = await Ok(await AddOtherAsync(owner, partyId, "Collega di Mario"));

        // The same aggregate and the same shape: the list is still there, and so
        // is somebody who was never on it. No mode was switched.
        Assert.Equal(2, mixed.GetProperty("groups").GetArrayLength());
        var other = Assert.Single(mixed.GetProperty("otherGuests").EnumerateArray().ToList());
        Assert.Equal("Collega di Mario", other.GetProperty("name").GetString());
        AssertSummary(mixed, expected: 2, expectedArrived: 1, missing: 1, unexpectedKnown: 0, others: 1, total: 2);
        // Nobody became a guest, an RSVP or a participant by being recorded.
        Assert.Equal(4, await CountAsync(db => db.PartyGuests.CountAsync()));
        Assert.Equal(4, await CountAsync(db => db.PartyRsvps.CountAsync()));
        Assert.Equal(0, await ParticipantsAsync());
    }

    // --- Lifecycle ---------------------------------------------------------------------

    [Fact]
    public async Task Arrivals_are_readable_in_every_phase_and_recorded_only_while_live_or_after()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var groupId = (await AddGroupAsync(owner, partyId, "Mario", "mario@example.com", 0, "Mario"))
            .GetProperty("id").GetGuid();
        var mario = GuestIdOf(await AttendanceAsync(owner, partyId), "Mario");

        async Task AssertClosedAsync(string phase)
        {
            var read = await AttendanceAsync(owner, partyId);
            Assert.Equal(phase, read.GetProperty("partyStatus").GetString());
            Assert.False(read.GetProperty("canEdit").GetBoolean());
            foreach (var refused in new[]
            {
                await CheckInAsync(owner, partyId, mario),
                await UndoAsync(owner, partyId, mario),
                await AddOtherAsync(owner, partyId, "Troppo presto"),
            })
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("attendance_not_open", body.GetProperty("error").GetString());
                Assert.Equal(partyId, body.GetProperty("attendance").GetProperty("partyId").GetGuid());
            }
            // Asking neither published nor started the party.
            Assert.Equal(phase, await StatusAsync(owner, partyId));
            Assert.Equal(0, await ArrivalsAsync());
        }

        await AssertClosedAsync("draft");
        await InviteAsync(_factory, owner, partyId, groupId);
        await AssertClosedAsync("published");

        await AdvanceAsync(owner, partyId, "start-live");
        var live = await Ok(await CheckInAsync(owner, partyId, mario));
        Assert.True(live.GetProperty("canEdit").GetBoolean());
        Assert.NotNull(GuestCheckedInAt(live, "Mario"));

        // Ended stays a place to correct a mistake — and to record a forgotten one.
        await AdvanceAsync(owner, partyId, "end-live");
        var undone = await Ok(await UndoAsync(owner, partyId, mario));
        Assert.Equal(0, SummaryOf(undone).GetProperty("totalArrivals").GetInt32());
        var again = await Ok(await CheckInAsync(owner, partyId, mario));
        Assert.Equal("ended", again.GetProperty("partyStatus").GetString());
        Assert.True(again.GetProperty("canEdit").GetBoolean());
        Assert.Equal(1, SummaryOf(again).GetProperty("totalArrivals").GetInt32());
    }

    // --- Idempotency -------------------------------------------------------------------

    [Fact]
    public async Task Checking_a_guest_in_twice_keeps_the_first_moment_and_undoing_twice_is_undoing_once()
    {
        var (owner, partyId) = await InvitedLivePartyAsync();
        var mario = GuestIdOf(await AttendanceAsync(owner, partyId), "Mario");

        var first = await Ok(await CheckInAsync(owner, partyId, mario));
        var at = GuestCheckedInAt(first, "Mario");
        await Task.Delay(30);
        var second = await Ok(await CheckInAsync(owner, partyId, mario));

        Assert.Equal(at, GuestCheckedInAt(second, "Mario"));
        Assert.Equal(1, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));

        (await UndoAsync(owner, partyId, mario)).EnsureSuccessStatusCode();
        var undoneTwice = await Ok(await UndoAsync(owner, partyId, mario));
        Assert.Null(GuestCheckedInAt(undoneTwice, "Mario"));
        Assert.Equal(0, await CountAsync(db => db.PartyGuestAttendances.CountAsync()));

        // One event each. A repeated tap is not an event, and no line names anybody.
        var lines = await AuditAsync("party.attendance.");
        Assert.Equal(1, lines.Count(l => l.Action == "party.attendance.check_in"));
        Assert.Equal(1, lines.Count(l => l.Action == "party.attendance.undo"));
        Assert.All(lines, l =>
        {
            Assert.Contains(mario.ToString(), l.Metadata);
            Assert.Contains("\"owner\"", l.Metadata);
            Assert.DoesNotContain("Mario", l.Metadata);
        });
    }

    [Fact]
    public async Task One_add_retried_names_the_person_once_and_a_second_add_is_a_second_person()
    {
        var (owner, partyId) = await OpenLivePartyAsync();
        var click = Guid.NewGuid();

        await Ok(await AddOtherAsync(owner, partyId, "Walter", click));
        var replayed = await Ok(await AddOtherAsync(owner, partyId, "Walter, di nuovo", click));

        // The retry is answered by the first add: one person, the first name.
        var only = Assert.Single(replayed.GetProperty("otherGuests").EnumerateArray().ToList());
        Assert.Equal("Walter", only.GetProperty("name").GetString());

        // A new add is a new person, even with the same name — two Walters exist.
        var another = await Ok(await AddOtherAsync(owner, partyId, "Walter"));
        Assert.Equal(2, another.GetProperty("otherGuests").GetArrayLength());
        Assert.Equal(2, (await AuditAsync("party.attendance.other_create")).Count);

        // The request id belongs to its party: the same id elsewhere is that party's own add.
        var (otherOwner, otherParty) = await OpenLivePartyAsync();
        var elsewhere = await Ok(await AddOtherAsync(otherOwner, otherParty, "Walter altrove", click));
        Assert.Single(elsewhere.GetProperty("otherGuests").EnumerateArray().ToList());
    }

    [Fact]
    public async Task A_rename_spends_the_version_and_a_stale_one_is_shown_the_name_as_it_is()
    {
        var (owner, partyId) = await OpenLivePartyAsync();
        var id = OtherOf(await Ok(await AddOtherAsync(owner, partyId, "Walt")), "Walt").GetProperty("id").GetGuid();

        var renamed = await Ok(await RenameOtherAsync(owner, partyId, id, "  Walter  ", 1));
        var walter = OtherOf(renamed, "Walter");
        Assert.Equal(2, walter.GetProperty("version").GetInt32());

        var stale = await RenameOtherAsync(owner, partyId, id, "Walterino", 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("error").GetString());
        Assert.Equal(2, OtherOf(body.GetProperty("attendance"), "Walter").GetProperty("version").GetInt32());
        Assert.Equal("Walter", await ScalarAsync(db => db.PartyAttendanceGuests.Select(g => g.Name).SingleAsync()));
        Assert.Single(await AuditAsync("party.attendance.other_update"));
    }

    [Fact]
    public async Task A_recorded_person_is_somebody_with_a_name_of_at_most_120_code_points()
    {
        var (owner, partyId) = await OpenLivePartyAsync();

        Assert.Equal("invalid_name", await ErrorOf(await AddOtherAsync(owner, partyId, "   ")));
        var tooLong = string.Concat(Enumerable.Repeat("🎉", 121));
        Assert.Equal("invalid_name", await ErrorOf(await AddOtherAsync(owner, partyId, tooLong)));
        var noRequest = await owner.PostAsJsonAsync(
            $"{AttendanceUrl(partyId)}/other-guests", new { name = "Anna", clientRequestId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, noRequest.StatusCode);
        Assert.Equal("invalid_request", await ErrorOf(noRequest));

        var longest = string.Concat(Enumerable.Repeat("🎉", 120));
        var stored = await Ok(await AddOtherAsync(owner, partyId, longest));
        var id = OtherOf(stored, longest).GetProperty("id").GetGuid();
        Assert.Equal("invalid_name", await ErrorOf(await RenameOtherAsync(owner, partyId, id, "", 1)));
        Assert.Equal(1, await CountAsync(db => db.PartyAttendanceGuests.CountAsync()));
    }

    // --- Isolation ---------------------------------------------------------------------

    [Fact]
    public async Task Another_hosts_arrivals_are_the_same_404_as_arrivals_that_do_not_exist()
    {
        var (alice, aliceParty) = await InvitedLivePartyAsync();
        var mario = GuestIdOf(await AttendanceAsync(alice, aliceParty), "Mario");
        var aliceOther = OtherOf(await Ok(await AddOtherAsync(alice, aliceParty, "Ottavia di Alice")), "Ottavia di Alice")
            .GetProperty("id").GetGuid();
        var (bob, bobParty) = await OpenLivePartyAsync();

        async Task<(HttpStatusCode Status, string Body)> Call(HttpMethod method, string url, object? body)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            var response = await bob.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        static (HttpMethod, string, object?)[] Routes(Guid party, Guid guest, Guid other) =>
        [
            (HttpMethod.Get, AttendanceUrl(party), null),
            (HttpMethod.Put, $"{AttendanceUrl(party)}/guests/{guest}", null),
            (HttpMethod.Delete, $"{AttendanceUrl(party)}/guests/{guest}", null),
            (HttpMethod.Post, $"{AttendanceUrl(party)}/other-guests", new { name = "X", clientRequestId = Guid.NewGuid() }),
            (HttpMethod.Put, $"{AttendanceUrl(party)}/other-guests/{other}", new { name = "X", version = 1 }),
            (HttpMethod.Delete, $"{AttendanceUrl(party)}/other-guests/{other}", null),
        ];

        foreach (var (method, url, body) in Routes(aliceParty, mario, aliceOther))
        {
            var foreign = await Call(method, url, body);
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
            Assert.DoesNotContain("Mario", foreign.Body);
            Assert.DoesNotContain("Ottavia", foreign.Body);
        }
        foreach (var (method, url, body) in Routes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Call(method, url, body)).Status);
        }
        // Bob's OWN live party does not make Alice's guest or arrival his: the
        // ids are proven to belong to the party, never trusted on their own.
        var bobsOwn = Routes(bobParty, mario, aliceOther);
        foreach (var (method, url, body) in new[] { 1, 2, 4, 5 }.Select(i => bobsOwn[i]))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Call(method, url, body)).Status);
        }
        // Nobody signed in is nobody.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(AttendanceUrl(aliceParty))).StatusCode);

        // And nothing of Alice's moved.
        var aliceNow = await AttendanceAsync(alice, aliceParty);
        Assert.Null(GuestCheckedInAt(aliceNow, "Mario"));
        Assert.Equal(1, OtherOf(aliceNow, "Ottavia di Alice").GetProperty("version").GetInt32());
    }

    // --- PartyParticipant stays its own identity ----------------------------------------

    [Fact]
    public async Task Attendance_never_makes_reads_or_changes_a_participant_and_the_qr_never_makes_an_arrival()
    {
        var (owner, partyId, viewToken, uploadToken) = await InvitedLivePartyWithQrAsync();

        // A phone at the party: the normal way a participant comes to exist.
        var phone = _factory.CreateClient();
        (await phone.GetAsync($"/api/party/{viewToken}")).EnsureSuccessStatusCode();
        (await GuestUploadAsync(phone, uploadToken)).EnsureSuccessStatusCode();
        var participants = await SnapshotParticipantsAsync();
        Assert.Single(participants);
        Assert.Equal(0, await ArrivalsAsync());

        // The host records a guest and somebody else.
        var list = await AttendanceAsync(owner, partyId);
        await Ok(await CheckInAsync(owner, partyId, GuestIdOf(list, "Mario")));
        await Ok(await AddOtherAsync(owner, partyId, "Vicino di casa"));
        Assert.Equal(2, await ArrivalsAsync());
        // The participant is exactly as it was — no row more, no counter moved.
        Assert.Equal(participants, await SnapshotParticipantsAsync());

        // Another phone scans the QR: a second participant, and still two arrivals.
        (await GuestUploadAsync(_factory.CreateClient(), uploadToken)).EnsureSuccessStatusCode();
        Assert.Equal(2, await ParticipantsAsync());
        Assert.Equal(2, await ArrivalsAsync());
    }

    // --- helpers --------------------------------------------------------------------------

    private static string AttendanceUrl(Guid partyId) => $"/api/parties/{partyId}/attendance";

    private static Task<HttpResponseMessage> CheckInAsync(HttpClient owner, Guid partyId, Guid guestId) =>
        owner.PutAsync($"{AttendanceUrl(partyId)}/guests/{guestId}", null);

    private static Task<HttpResponseMessage> UndoAsync(HttpClient owner, Guid partyId, Guid guestId) =>
        owner.DeleteAsync($"{AttendanceUrl(partyId)}/guests/{guestId}");

    private static Task<HttpResponseMessage> AddOtherAsync(
        HttpClient owner, Guid partyId, string name, Guid? requestId = null) =>
        owner.PostAsJsonAsync(
            $"{AttendanceUrl(partyId)}/other-guests", new { name, clientRequestId = requestId ?? Guid.NewGuid() });

    private static Task<HttpResponseMessage> RenameOtherAsync(
        HttpClient owner, Guid partyId, Guid id, string name, int version) =>
        owner.PutAsJsonAsync($"{AttendanceUrl(partyId)}/other-guests/{id}", new { name, version });

    private static async Task<JsonElement> AttendanceAsync(HttpClient owner, Guid partyId) =>
        await owner.GetFromJsonAsync<JsonElement>(AttendanceUrl(partyId));

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> StatusAsync(HttpClient owner, Guid partyId) =>
        (await GetPartyAsync(owner, partyId)).GetProperty("status").GetString()!;

    private static JsonElement SummaryOf(JsonElement attendance) => attendance.GetProperty("summary");

    private static void AssertSummary(
        JsonElement attendance, int expected, int expectedArrived, int missing, int unexpectedKnown, int others, int total)
    {
        var s = SummaryOf(attendance);
        Assert.Equal(
            (expected, expectedArrived, missing, unexpectedKnown, others, total),
            (s.GetProperty("expectedPeople").GetInt32(), s.GetProperty("expectedArrived").GetInt32(),
             s.GetProperty("expectedMissing").GetInt32(), s.GetProperty("unexpectedKnownGuests").GetInt32(),
             s.GetProperty("otherArrivals").GetInt32(), s.GetProperty("totalArrivals").GetInt32()));
    }

    private static JsonElement GuestOf(JsonElement attendance, string name) =>
        attendance.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("guests").EnumerateArray())
            .Single(g => g.GetProperty("name").GetString() == name);

    private static Guid GuestIdOf(JsonElement attendance, string name) =>
        GuestOf(attendance, name).GetProperty("guestId").GetGuid();

    private static DateTime? GuestCheckedInAt(JsonElement attendance, string name)
    {
        var value = GuestOf(attendance, name).GetProperty("checkedInAt");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDateTime();
    }

    private static JsonElement OtherOf(JsonElement attendance, string name) =>
        attendance.GetProperty("otherGuests").EnumerateArray().Single(o => o.GetProperty("name").GetString() == name);

    private async Task<T> ScalarAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> CountAsync(Func<AppDbContext, Task<int>> count) => ScalarAsync(count);

    private Task<int> ParticipantsAsync() => CountAsync(db => db.PartyParticipants.CountAsync());

    private Task<int> ArrivalsAsync() => CountAsync(async db =>
        await db.PartyGuestAttendances.CountAsync() + await db.PartyAttendanceGuests.CountAsync());

    /// <summary>Every participant with every counter it carries, so "unchanged" means unchanged.</summary>
    private Task<List<string>> SnapshotParticipantsAsync() => ScalarAsync(db => db.PartyParticipants.AsNoTracking()
        .OrderBy(p => p.Id)
        .Select(p => p.Id + ":" + p.AcceptedPhotoCount + ":" + p.AcceptedVideoCount + ":" + p.ChallengeVoteCount
            + ":" + p.SubmittedMessageCount + ":" + p.AcceptedPhotoPrintCount + ":" + p.AcceptedStripPrintCount)
        .ToListAsync());

    private Task<List<(string Action, string Metadata)>> AuditAsync(string actionPrefix) => ScalarAsync(async db =>
        (await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action.StartsWith(actionPrefix))
            .Select(a => new { a.Action, a.MetadataJson })
            .ToListAsync())
        .Select(a => (a.Action, a.MetadataJson ?? string.Empty)).ToList());

    /// <summary>The party's QR with guest uploads switched on. Publishes a Draft.</summary>
    private static async Task<(string ViewToken, string UploadToken)> OpenPublicQrWithUploadsAsync(
        HttpClient owner, Guid partyId)
    {
        var album = await owner.PostAsJsonAsync("/api/albums", new { name = $"Album {Guid.NewGuid():N}" });
        album.EnsureSuccessStatusCode();
        var albumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = (await GetPartyAsync(owner, partyId)).GetProperty("version").GetInt32();
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/media/main", new { albumId, version }))
            .EnsureSuccessStatusCode();
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true, uploadEnabled = true });
        settings.EnsureSuccessStatusCode();
        var status = await settings.Content.ReadFromJsonAsync<JsonElement>();
        var viewToken = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var uploadUrl = status.GetProperty("uploadUrl").GetString()!;
        return (viewToken, uploadUrl["/party/".Length..^"/upload".Length]);
    }

    private static Task<HttpResponseMessage> GuestUploadAsync(HttpClient phone, string uploadToken)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return phone.PostAsync(
            $"/api/party/{uploadToken}/upload", new MultipartFormDataContent { { part, "file", "festa.png" } });
    }

    /// <summary>An open party, live, and nobody on any list.</summary>
    private async Task<(HttpClient Owner, Guid PartyId)> OpenLivePartyAsync()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Festa aperta");
        await OpenPublicQrWithUploadsAsync(owner, partyId);
        await AdvanceAsync(owner, partyId, "start-live");
        return (owner, partyId);
    }

    /// <summary>
    /// An invited party, live. "Famiglia Rossi": Mario attending with Giulia as
    /// his +1, Laura declined. "Sara Verdi": invited, never answered.
    /// </summary>
    private async Task<(HttpClient Owner, Guid PartyId)> InvitedLivePartyAsync()
    {
        var (owner, partyId, _, _) = await InvitedPartyAsync(withQr: false);
        await AdvanceAsync(owner, partyId, "start-live");
        return (owner, partyId);
    }

    private async Task<(HttpClient Owner, Guid PartyId, string ViewToken, string UploadToken)> InvitedLivePartyWithQrAsync()
    {
        var (owner, partyId, viewToken, uploadToken) = await InvitedPartyAsync(withQr: true);
        await AdvanceAsync(owner, partyId, "start-live");
        return (owner, partyId, viewToken!, uploadToken!);
    }

    private async Task<(HttpClient Owner, Guid PartyId, string? ViewToken, string? UploadToken)> InvitedPartyAsync(bool withQr)
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var rossi = (await AddGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", 1, "Mario", "Laura"))
            .GetProperty("id").GetGuid();
        var verdi = (await AddGroupAsync(owner, partyId, "Sara Verdi", "sara@example.com", 0, "Sara"))
            .GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, rossi);
        await InviteAsync(_factory, owner, partyId, verdi);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(
            await ViewAsync(guest, token),
            new Dictionary<string, string> { ["Mario"] = "attending", ["Laura"] = "declined" },
            additionalGuests: [new { name = "Giulia" }]))).EnsureSuccessStatusCode();
        if (!withQr) return (owner, partyId, null, null);
        var (viewToken, uploadToken) = await OpenPublicQrWithUploadsAsync(owner, partyId);
        return (owner, partyId, viewToken, uploadToken);
    }
}

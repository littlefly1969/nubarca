using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The host's guest console, read in pages.
///
/// <para>Pinned down here: every page is a keyset page in a stable order —
/// other arrivals latest first, then groups by label — that neither repeats nor
/// skips when the list changes between two pages; the search reaches a guest's
/// name, a group's label, either address, either number and an other arrival,
/// accents and case folded; each filter says what it says before and during the
/// party; the counts are the guest list's and the attendance's own; a cursor
/// answers only the list it was issued for; a group's detail carries what the
/// host may see and nothing of its link; minimal answers keep a paging client
/// from downloading the whole list; and the search follows every write that
/// changes what it folds.</para>
/// </summary>
public sealed class PartyGuestDirectoryTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // --- Helpers ----------------------------------------------------------------------

    private static async Task<JsonElement> PageAsync(HttpClient owner, Guid partyId, string query = "")
    {
        var response = await owner.GetAsync($"/api/parties/{partyId}/guest-directory{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Search(string q) => "?q=" + Uri.EscapeDataString(q);

    private static string Name(JsonElement item) => item.GetProperty("kind").GetString() == "group"
        ? item.GetProperty("label").GetString()!
        : "other:" + item.GetProperty("name").GetString();

    private static string[] Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(Name).ToArray();

    private static string? Cursor(JsonElement page) =>
        page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();

    /// <summary>Every page of one list, asserting the counts come on the first page only.</summary>
    private static async Task<List<string>> WalkAsync(HttpClient owner, Guid partyId, int take, string parameters = "")
    {
        var names = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(owner, partyId,
                $"?take={take}{parameters}" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(pages == 0 ? JsonValueKind.Object : JsonValueKind.Null, page.GetProperty("summary").ValueKind);
            Assert.True(page.GetProperty("items").GetArrayLength() <= take);
            names.AddRange(Names(page));
            cursor = Cursor(page);
            Assert.True(++pages < 200, "the walk does not end");
        }
        while (cursor is not null);
        return names;
    }

    private static async Task<JsonElement> CreateGroupAsync(
        HttpClient owner, Guid partyId, string label, string email, string? phone, int maxAdditionalGuests,
        params object[] guests)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups",
            GroupBody(label, email, maxAdditionalGuests, guests, phone: phone));
        response.EnsureSuccessStatusCode();
        return Group(await response.Content.ReadFromJsonAsync<JsonElement>(), label);
    }

    private static Task<HttpResponseMessage> AddOtherAsync(HttpClient owner, Guid partyId, string name) =>
        owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/attendance/other-guests", new { name, clientRequestId = Guid.NewGuid() });

    private static Guid OwnerGuestId(JsonElement guestList, string label, string name) =>
        OwnerGuest(Group(guestList, label), name).GetProperty("id").GetGuid();

    private static HttpRequestMessage Minimal(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Prefer", "return=minimal");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    // --- Pages ------------------------------------------------------------------------

    [Fact]
    public async Task An_open_party_starts_with_an_empty_directory_and_zero_counts()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);

        var page = await PageAsync(owner, partyId);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Null(Cursor(page));
        Assert.Equal("draft", page.GetProperty("partyStatus").GetString());
        Assert.True(page.GetProperty("mailAvailable").GetBoolean());
        Assert.True(page.GetProperty("shareAvailable").GetBoolean());
        var summary = page.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("groups").GetInt32());
        Assert.Equal(0, summary.GetProperty("otherArrivals").GetInt32());
        Assert.Equal(0, summary.GetProperty("rsvp").GetProperty("invited").GetInt32());
        Assert.Equal(0, summary.GetProperty("attendance").GetProperty("totalArrivals").GetInt32());
    }

    [Fact]
    public async Task Pages_walk_the_whole_list_in_label_order_with_the_counts_on_the_first_page_only()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        foreach (var label in new[] { "delta", "Alfa", "charlie", "Bravo", "echo", "Foxtrot", "golf" })
        {
            await CreateGroupAsync(owner, partyId, label, "x@example.com", null, 0, new { name = label + " uno" });
        }

        var walked = await WalkAsync(owner, partyId, take: 3);

        Assert.Equal(new[] { "Alfa", "Bravo", "charlie", "delta", "echo", "Foxtrot", "golf" }, walked);
        var first = await PageAsync(owner, partyId, "?take=3");
        Assert.Equal(7, first.GetProperty("summary").GetProperty("groups").GetInt32());
        Assert.Equal(3, first.GetProperty("items").GetArrayLength());
        // The default page is forty.
        Assert.Equal(7, (await PageAsync(owner, partyId)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_page_neither_repeats_nor_skips_when_the_list_changes_between_two_pages()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        for (var i = 1; i <= 6; i++)
        {
            await CreateGroupAsync(owner, partyId, $"Ospite {i}", "x@example.com", null, 0, new { name = $"Persona {i}" });
        }

        var first = await PageAsync(owner, partyId, "?take=2");
        Assert.Equal(new[] { "Ospite 1", "Ospite 2" }, Names(first));

        // Meanwhile: one added before the page, one after it, one removed, one renamed to the end.
        await CreateGroupAsync(owner, partyId, "Ospite 0", "x@example.com", null, 0, new { name = "Zero" });
        await CreateGroupAsync(owner, partyId, "Ospite 2b", "x@example.com", null, 0, new { name = "Due bis" });
        var list = await GuestListAsync(owner, partyId);
        var third = Group(list, "Ospite 3");
        (await owner.DeleteAsync(
            $"/api/parties/{partyId}/invitation-groups/{third.GetProperty("id").GetGuid()}?version={third.GetProperty("version").GetInt32()}"))
            .EnsureSuccessStatusCode();
        var fourth = Group(list, "Ospite 4");
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{fourth.GetProperty("id").GetGuid()}",
            GroupBody("Ospite 9", "x@example.com", 0,
                [new { id = OwnerGuest(fourth, "Persona 4").GetProperty("id").GetGuid(), name = "Persona 4" }],
                version: fourth.GetProperty("version").GetInt32()))).EnsureSuccessStatusCode();

        var rest = new List<string>();
        var cursor = Cursor(first);
        while (cursor is not null)
        {
            var page = await PageAsync(owner, partyId, "?take=2&cursor=" + Uri.EscapeDataString(cursor));
            rest.AddRange(Names(page));
            cursor = Cursor(page);
        }

        // Continued from where the first page stopped: nothing before it again,
        // nothing that still exists after it missed.
        Assert.Equal(new[] { "Ospite 2b", "Ospite 5", "Ospite 6", "Ospite 9" }, rest);
    }

    [Fact]
    public async Task An_open_party_at_the_door_lists_its_other_arrivals_latest_first()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AdvanceAsync(owner, partyId, "publish");
        await AdvanceAsync(owner, partyId, "start-live");
        foreach (var name in new[] { "Anna", "Bruno", "Carla" })
        {
            (await AddOtherAsync(owner, partyId, name)).EnsureSuccessStatusCode();
        }

        var page = await PageAsync(owner, partyId);
        Assert.Equal(new[] { "other:Carla", "other:Bruno", "other:Anna" }, Names(page));
        Assert.Equal(new[] { "other:Carla", "other:Bruno", "other:Anna" }, await WalkAsync(owner, partyId, take: 2));
        var summary = page.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("groups").GetInt32());
        Assert.Equal(3, summary.GetProperty("otherArrivals").GetInt32());
        Assert.Equal(3, summary.GetProperty("attendance").GetProperty("totalArrivals").GetInt32());
        Assert.Equal(0, summary.GetProperty("attendance").GetProperty("expectedPeople").GetInt32());
        var anna = page.GetProperty("items")[2];
        Assert.Equal("other", anna.GetProperty("kind").GetString());
        Assert.Equal(1, anna.GetProperty("version").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, anna.GetProperty("checkedInAt").ValueKind);
    }

    // --- Search -------------------------------------------------------------------------

    [Fact]
    public async Task Search_finds_names_labels_addresses_and_numbers_with_accents_and_case_folded()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await CreateGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", "+39 333 444 5555", 0,
            new { name = "Mario Rossi", email = "mario.r@example.org" },
            new { name = "Nicolò Bianchi", phone = "0039 347 111 2222" });
        await CreateGroupAsync(owner, partyId, "Verdi", "verdi@example.com", null, 0, new { name = "Gino" });

        async Task<JsonElement> Only(string q)
        {
            var page = await PageAsync(owner, partyId, Search(q));
            return Assert.Single(page.GetProperty("items").EnumerateArray());
        }
        bool Matched(JsonElement item, string name) => item.GetProperty("people").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == name).GetProperty("matched").GetBoolean();

        var rossi = await Only("rossi");
        Assert.Equal("Famiglia Rossi", rossi.GetProperty("label").GetString());
        Assert.True(Matched(rossi, "Mario Rossi"));
        Assert.False(Matched(rossi, "Nicolò Bianchi"));

        var nicolo = await Only("NICOLO");
        Assert.True(Matched(nicolo, "Nicolò Bianchi"));
        Assert.False(Matched(nicolo, "Mario Rossi"));

        Assert.True(Matched(await Only("mario.r@ex"), "Mario Rossi"));
        // The group's own address and number find the group, and no person in it.
        foreach (var q in new[] { "rossi@example", "3334445555", "333 444", "+39 333" })
        {
            var group = await Only(q);
            Assert.Equal("Famiglia Rossi", group.GetProperty("label").GetString());
            Assert.All(group.GetProperty("people").EnumerateArray(), p => Assert.False(p.GetProperty("matched").GetBoolean()));
        }
        Assert.True(Matched(await Only("347 111"), "Nicolò Bianchi"));
        Assert.True(Matched(await Only("3471112222"), "Nicolò Bianchi"));
        Assert.Equal("Verdi", (await Only("gino")).GetProperty("label").GetString());
        Assert.Empty((await PageAsync(owner, partyId, Search("zzz"))).GetProperty("items").EnumerateArray());
        // No search is every group, and nobody "matched".
        var everyone = await PageAsync(owner, partyId);
        Assert.Equal(2, everyone.GetProperty("items").GetArrayLength());
        Assert.All(everyone.GetProperty("items").EnumerateArray().SelectMany(i => i.GetProperty("people").EnumerateArray()),
            p => Assert.False(p.GetProperty("matched").GetBoolean()));

        // A card is names and states: no address, no number, no link.
        var raw = everyone.GetRawText();
        foreach (var leak in new[] { "rossi@example.com", "mario.r@example.org", "+39 333 444 5555", "347 111", "verdi@" })
        {
            Assert.DoesNotContain(leak, raw);
        }
    }

    // --- Filters ------------------------------------------------------------------------

    [Fact]
    public async Task Filters_follow_the_answers_before_the_party_and_the_arrivals_during_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var ids = new Dictionary<string, Guid>();
        foreach (var (label, names) in new[]
        {
            ("Anna", new[] { "Anna" }), ("Bianchi", new[] { "Paolo", "Marta" }), ("Carla", new[] { "Carla" }),
            ("Dario", new[] { "Dario" }), ("Elena", new[] { "Elena" }),
        })
        {
            ids[label] = (await AddGroupAsync(owner, partyId, label, $"{label.ToLowerInvariant()}@example.com", 0, names))
                .GetProperty("id").GetGuid();
        }
        var guest = _factory.CreateClient();
        async Task Answer(string label, Dictionary<string, string> statuses)
        {
            var token = await InviteAsync(_factory, owner, partyId, ids[label]);
            (await RsvpAsync(guest, token, Reply(await ViewAsync(guest, token), statuses))).EnsureSuccessStatusCode();
        }
        await Answer("Anna", new() { ["Anna"] = "attending" });
        await Answer("Bianchi", new() { ["Paolo"] = "attending", ["Marta"] = "declined" });
        await Answer("Carla", new() { ["Carla"] = "declined" });
        await InviteAsync(_factory, owner, partyId, ids["Dario"]);

        async Task<string[]> Filtered(string state) => Names(await PageAsync(owner, partyId, $"?state={state}"));

        Assert.Equal(new[] { "Anna", "Bianchi", "Carla", "Dario", "Elena" }, await Filtered("all"));
        Assert.Equal(new[] { "Dario", "Elena" }, await Filtered("pending"));
        Assert.Equal(new[] { "Anna", "Bianchi" }, await Filtered("attending"));
        Assert.Equal(new[] { "Bianchi", "Carla" }, await Filtered("declined"));
        Assert.Equal(new[] { "Elena" }, await Filtered("not_invited"));
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/invitation-groups/{ids["Elena"]}/share",
            new { channel = "copy", clientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Empty(await Filtered("not_invited"));

        var before = (await PageAsync(owner, partyId)).GetProperty("summary").GetProperty("rsvp");
        Assert.Equal(5, before.GetProperty("groups").GetInt32());
        Assert.Equal(6, before.GetProperty("invited").GetInt32());
        Assert.Equal(2, before.GetProperty("missingResponses").GetInt32());
        Assert.Equal(2, before.GetProperty("attending").GetInt32());
        Assert.Equal(2, before.GetProperty("declined").GetInt32());
        Assert.Equal(2, before.GetProperty("unansweredGroups").GetInt32());
        var bianchi = (await PageAsync(owner, partyId, Search("bianchi"))).GetProperty("items")[0].GetProperty("counts");
        Assert.Equal(1, bianchi.GetProperty("attending").GetInt32());
        Assert.Equal(0, bianchi.GetProperty("pending").GetInt32());
        Assert.Equal(1, bianchi.GetProperty("declined").GetInt32());

        // The party starts. Anna arrives as expected, Carla although she declined,
        // and Zoë, who is not on the list.
        await AdvanceAsync(owner, partyId, "start-live");
        var list = await GuestListAsync(owner, partyId);
        foreach (var (label, name) in new[] { ("Anna", "Anna"), ("Carla", "Carla") })
        {
            (await owner.PutAsync(
                $"/api/parties/{partyId}/attendance/guests/{OwnerGuestId(list, label, name)}", null)).EnsureSuccessStatusCode();
        }
        (await AddOtherAsync(owner, partyId, "Zoë")).EnsureSuccessStatusCode();

        Assert.Equal(new[] { "other:Zoë", "Anna", "Bianchi", "Carla", "Dario", "Elena" }, await Filtered("all"));
        Assert.Equal(new[] { "Bianchi" }, await Filtered("to_arrive"));
        Assert.Equal(new[] { "other:Zoë", "Anna", "Carla" }, await Filtered("arrived"));
        Assert.Equal(new[] { "other:Zoë", "Carla" }, await Filtered("unexpected"));
        // Nobody not on the list "has not answered".
        Assert.Equal(new[] { "Dario", "Elena" }, await Filtered("pending"));
        Assert.Equal(new[] { "other:Zoë" }, Names(await PageAsync(owner, partyId, Search("zoe"))));

        var live = await PageAsync(owner, partyId);
        var attendance = live.GetProperty("summary").GetProperty("attendance");
        Assert.Equal(2, attendance.GetProperty("expectedPeople").GetInt32());
        Assert.Equal(1, attendance.GetProperty("expectedArrived").GetInt32());
        Assert.Equal(1, attendance.GetProperty("expectedMissing").GetInt32());
        Assert.Equal(1, attendance.GetProperty("unexpectedKnownGuests").GetInt32());
        Assert.Equal(1, attendance.GetProperty("otherArrivals").GetInt32());
        Assert.Equal(3, attendance.GetProperty("totalArrivals").GetInt32());
        // An arrival changes no answer.
        Assert.Equal(before.GetRawText(), live.GetProperty("summary").GetProperty("rsvp").GetRawText());
        var anna = live.GetProperty("items")[1];
        Assert.Equal(1, anna.GetProperty("counts").GetProperty("arrived").GetInt32());
        Assert.Equal("owner", anna.GetProperty("people")[0].GetProperty("checkInSource").GetString());
        Assert.False(anna.GetProperty("canShare").GetBoolean());
        Assert.False(anna.GetProperty("canSend").GetBoolean());
    }

    // --- Authority and input ------------------------------------------------------------

    [Fact]
    public async Task The_directory_a_group_and_the_questions_are_the_owners_alone()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var otherPartyId = await CreatePartyAsync(owner, "Altra festa");
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara")).GetProperty("id").GetGuid();
        var (_, stranger) = await NewHostAsync(_factory);

        foreach (var url in new[]
        {
            $"/api/parties/{partyId}/guest-directory",
            $"/api/parties/{partyId}/invitation-groups/{groupId}",
            $"/api/parties/{partyId}/rsvp-questions",
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
        }
        // Another party of the same host does not own the group.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"/api/parties/{otherPartyId}/invitation-groups/{groupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"/api/parties/{partyId}/invitation-groups/{Guid.NewGuid()}")).StatusCode);
        Assert.Empty((await PageAsync(owner, otherPartyId)).GetProperty("items").EnumerateArray());
        // Signed out is signed out.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/parties/{partyId}/guest-directory")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_request_is_refused_and_a_cursor_answers_only_the_list_it_was_issued_for()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var otherPartyId = await CreatePartyAsync(owner, "Altra festa");
        for (var i = 1; i <= 3; i++)
        {
            await CreateGroupAsync(owner, partyId, $"Anna {i}", "x@example.com", null, 0, new { name = $"Anna {i}" });
            await CreateGroupAsync(owner, otherPartyId, $"Anna {i}", "x@example.com", null, 0, new { name = $"Anna {i}" });
        }

        async Task<string> Refused(Guid party, string query)
        {
            var response = await owner.GetAsync($"/api/parties/{party}/guest-directory{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await ErrorOf(response);
        }

        Assert.Equal("invalid_state", await Refused(partyId, "?state=maybe"));
        Assert.Equal("invalid_take", await Refused(partyId, "?take=101"));
        Assert.Equal("invalid_take", await Refused(partyId, "?take=-1"));
        Assert.Equal("invalid_query", await Refused(partyId, Search(new string('a', 121))));
        Assert.Equal("invalid_cursor", await Refused(partyId, "?cursor=garbage"));

        var cursor = Cursor(await PageAsync(owner, partyId, "?take=1&q=anna"))!;
        var escaped = Uri.EscapeDataString(cursor);
        // Honoured for exactly the list it continues…
        Assert.Equal(new[] { "Anna 2" }, Names(await PageAsync(owner, partyId, $"?take=1&q=anna&cursor={escaped}")));
        // …and for nothing else.
        Assert.Equal("invalid_cursor", await Refused(partyId, $"?take=1&q=anna+1&cursor={escaped}"));
        Assert.Equal("invalid_cursor", await Refused(partyId, $"?take=1&q=anna&state=pending&cursor={escaped}"));
        Assert.Equal("invalid_cursor", await Refused(otherPartyId, $"?take=1&q=anna&cursor={escaped}"));
        Assert.Equal("invalid_cursor", await Refused(partyId, $"?take=1&q=anna&cursor={escaped[..^2]}"));

        // take=0 is the counts alone.
        var counts = await PageAsync(owner, partyId, "?take=0");
        Assert.Empty(counts.GetProperty("items").EnumerateArray());
        Assert.Null(Cursor(counts));
        Assert.Equal(3, counts.GetProperty("summary").GetProperty("groups").GetInt32());
    }

    // --- One group ----------------------------------------------------------------------

    [Fact]
    public async Task A_group_in_detail_carries_what_the_host_may_see_and_nothing_of_its_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var menu = await AddQuestionAsync(owner, partyId, "Carne o pesce?", "single_choice", options: ["Carne", "Pesce"]);
        await AddQuestionAsync(owner, partyId, "Allergie?", "short_text");
        var groupId = (await CreateGroupAsync(owner, partyId, "Famiglia Rossi", "rossi@example.com", "+39 333 123 4567", 0,
            new { name = "Mario", email = "mario@example.org", phone = "+39 333 000 1111" })).GetProperty("id").GetGuid();
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        var view = await ViewAsync(guest, token);
        (await RsvpAsync(guest, token, new
        {
            version = Version(view),
            guests = new[] { new { guestId = GuestId(view, "Mario"), status = "attending", dietaryNotes = "vegetariano" } },
            additionalGuests = Array.Empty<object>(),
            answers = new[] { new { questionId = menu, value = (object)"Carne" } },
        })).EnsureSuccessStatusCode();
        var version = Group(await GuestListAsync(owner, partyId), "Famiglia Rossi").GetProperty("version").GetInt32();
        (await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/rotate-link", new { version })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/invitation-groups/{groupId}/share",
            new { channel = "whatsapp", clientRequestId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var response = await owner.GetAsync($"/api/parties/{partyId}/invitation-groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        var group = detail.GetProperty("group");
        Assert.Equal("rossi@example.com", group.GetProperty("recipientEmail").GetString());
        Assert.Equal("+39 333 123 4567", group.GetProperty("phone").GetString());
        var mario = OwnerGuest(group, "Mario");
        Assert.Equal("mario@example.org", mario.GetProperty("email").GetString());
        Assert.Equal("vegetariano", mario.GetProperty("dietaryNotes").GetString());
        Assert.Equal("attending", mario.GetProperty("status").GetString());
        Assert.True(detail.GetProperty("whatsappDirect").GetBoolean());
        Assert.True(group.GetProperty("canShare").GetBoolean());

        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("whatsapp", history[0].GetProperty("channel").GetString());
        Assert.Equal("shared", history[0].GetProperty("status").GetString());
        Assert.True(history[0].GetProperty("currentLink").GetBoolean());
        Assert.Equal("email", history[1].GetProperty("channel").GetString());
        Assert.Equal("sent", history[1].GetProperty("status").GetString());
        Assert.False(history[1].GetProperty("currentLink").GetBoolean());

        var question = Assert.Single(detail.GetProperty("questions").EnumerateArray());
        Assert.Equal("Carne o pesce?", question.GetProperty("prompt").GetString());
        Assert.Equal("group", detail.GetProperty("item").GetProperty("kind").GetString());
        Assert.Equal("shared", detail.GetProperty("item").GetProperty("invitation").GetProperty("state").GetString());
        Assert.Equal(1, detail.GetProperty("summary").GetProperty("groups").GetInt32());
        Assert.Empty(detail.GetProperty("arrivals").EnumerateArray());

        // Neither the link it holds now nor the one it held before.
        var raw = detail.GetRawText();
        Assert.DoesNotContain(CurrentToken(_factory, groupId), raw);
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain("/party/invite/", raw);

        await AdvanceAsync(owner, partyId, "start-live");
        (await owner.PutAsync(
            $"/api/parties/{partyId}/attendance/guests/{mario.GetProperty("id").GetGuid()}", null)).EnsureSuccessStatusCode();
        var arrived = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/invitation-groups/{groupId}");
        var arrival = Assert.Single(arrived.GetProperty("arrivals").EnumerateArray());
        Assert.Equal(mario.GetProperty("id").GetGuid(), arrival.GetProperty("guestId").GetGuid());
        Assert.Equal("owner", arrival.GetProperty("checkInSource").GetString());
    }

    [Fact]
    public async Task The_questions_are_read_without_the_list()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AddQuestionAsync(owner, partyId, "Come arrivi?", "short_text");
        await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara");

        var body = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/rsvp-questions");

        Assert.Equal("Come arrivi?", Assert.Single(body.GetProperty("questions").EnumerateArray()).GetProperty("prompt").GetString());
        Assert.False(body.TryGetProperty("groups", out _));
    }

    // --- Minimal answers ----------------------------------------------------------------

    [Fact]
    public async Task A_client_that_pages_the_list_receives_only_what_changed()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await AddGroupAsync(owner, partyId, "Già presente", "x@example.com", 0, "Qualcuno");

        var created = await owner.SendAsync(Minimal(HttpMethod.Post, $"/api/parties/{partyId}/invitation-groups",
            GroupBody("Sara", "sara@example.com", 0, [new { name = "Sara" }])));
        created.EnsureSuccessStatusCode();
        Assert.Equal("return=minimal", created.Headers.GetValues("Preference-Applied").Single());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("groups", out _));
        var groupId = body.GetProperty("groupId").GetGuid();
        Assert.Equal(2, body.GetProperty("summary").GetProperty("groups").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("questions").ValueKind);
        Assert.False(body.GetProperty("linkRotated").GetBoolean());

        var renamed = await owner.SendAsync(Minimal(HttpMethod.Put, $"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Sara B.", "sara.b@example.com", 0, [new { name = "Sara" }], version: 1)));
        renamed.EnsureSuccessStatusCode();
        var renamedBody = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(groupId, renamedBody.GetProperty("groupId").GetGuid());
        Assert.True(renamedBody.GetProperty("linkRotated").GetBoolean());

        var stale = await owner.SendAsync(Minimal(HttpMethod.Put, $"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Sara C.", "sara.b@example.com", 0, [new { name = "Sara" }], version: 1)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", staleBody.GetProperty("error").GetString());
        Assert.False(staleBody.GetProperty("guestList").TryGetProperty("groups", out _));

        var sent = await owner.SendAsync(Minimal(HttpMethod.Post, $"/api/parties/{partyId}/invitation-groups/{groupId}/send",
            new { clientRequestId = Guid.NewGuid(), partyVersion = 1 }));
        sent.EnsureSuccessStatusCode();
        var sentBody = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sent", sentBody.GetProperty("delivery").GetProperty("status").GetString());
        Assert.Equal("published", sentBody.GetProperty("party").GetProperty("status").GetString());
        Assert.False(sentBody.TryGetProperty("guestList", out _));

        await AdvanceAsync(owner, partyId, "start-live");
        var guestId = OwnerGuestId(await GuestListAsync(owner, partyId), "Sara B.", "Sara");
        var checkedIn = await owner.SendAsync(Minimal(HttpMethod.Put, $"/api/parties/{partyId}/attendance/guests/{guestId}"));
        checkedIn.EnsureSuccessStatusCode();
        var checkedInBody = await checkedIn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(checkedInBody.GetProperty("changed").GetBoolean());
        Assert.Equal(guestId, checkedInBody.GetProperty("guest").GetProperty("guestId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, checkedInBody.GetProperty("guest").GetProperty("checkedInAt").ValueKind);
        Assert.Equal(1, checkedInBody.GetProperty("summary").GetProperty("totalArrivals").GetInt32());
        Assert.False(checkedInBody.TryGetProperty("groups", out _));
        var again = await (await owner.SendAsync(
            Minimal(HttpMethod.Put, $"/api/parties/{partyId}/attendance/guests/{guestId}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(again.GetProperty("changed").GetBoolean());

        var other = await owner.SendAsync(Minimal(HttpMethod.Post, $"/api/parties/{partyId}/attendance/other-guests",
            new { name = "Zoë", clientRequestId = Guid.NewGuid() }));
        other.EnsureSuccessStatusCode();
        var otherBody = await other.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Zoë", otherBody.GetProperty("otherGuest").GetProperty("name").GetString());
        Assert.Equal(2, otherBody.GetProperty("summary").GetProperty("totalArrivals").GetInt32());
        Assert.False(otherBody.TryGetProperty("otherGuests", out _));

        // Created at 1, renamed to 2; the refused edit, the email and the arrival spend nothing.
        var removed = await owner.SendAsync(Minimal(HttpMethod.Delete,
            $"/api/parties/{partyId}/invitation-groups/{groupId}?version=2"));
        removed.EnsureSuccessStatusCode();
        Assert.Equal(groupId, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetGuid());
    }

    // --- The folded text follows every write --------------------------------------------

    [Fact]
    public async Task The_search_follows_every_write_that_changes_what_it_folds()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var group = await CreateGroupAsync(owner, partyId, "Famiglia Rossi", "f@example.com", null, 1, new { name = "Mario" });
        Assert.Single((await PageAsync(owner, partyId, Search("rossi"))).GetProperty("items").EnumerateArray());

        // A rename: found by the new label, never by the old one.
        var groupId = group.GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/invitation-groups/{groupId}",
            GroupBody("Famiglia Verdi", "f@example.com", 1,
                [new { id = OwnerGuest(group, "Mario").GetProperty("id").GetGuid(), name = "Mario Neri" }],
                version: group.GetProperty("version").GetInt32()))).EnsureSuccessStatusCode();
        Assert.Empty((await PageAsync(owner, partyId, Search("rossi"))).GetProperty("items").EnumerateArray());
        Assert.Single((await PageAsync(owner, partyId, Search("verdi"))).GetProperty("items").EnumerateArray());
        Assert.Single((await PageAsync(owner, partyId, Search("neri"))).GetProperty("items").EnumerateArray());

        // A +1 the group adds on its own invitation.
        var token = await InviteAsync(_factory, owner, partyId, groupId);
        var guest = _factory.CreateClient();
        (await RsvpAsync(guest, token, Reply(await ViewAsync(guest, token),
            new Dictionary<string, string> { ["Mario Neri"] = "attending" },
            additionalGuests: [new { name = "Giulia", dietaryNotes = (string?)null }]))).EnsureSuccessStatusCode();
        var plusOne = Assert.Single((await PageAsync(owner, partyId, Search("giulia"))).GetProperty("items").EnumerateArray());
        var giulia = plusOne.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Giulia");
        Assert.True(giulia.GetProperty("isAdditionalGuest").GetBoolean());
        Assert.True(giulia.GetProperty("matched").GetBoolean());

        // An other arrival, then its correction.
        await AdvanceAsync(owner, partyId, "start-live");
        var added = await (await AddOtherAsync(owner, partyId, "Zoë")).Content.ReadFromJsonAsync<JsonElement>();
        var other = added.GetProperty("otherGuests")[0];
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/attendance/other-guests/{other.GetProperty("id").GetGuid()}",
            new { name = "Zeno", version = other.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
        Assert.Empty((await PageAsync(owner, partyId, Search("zoe"))).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "other:Zeno" }, Names(await PageAsync(owner, partyId, Search("zeno"))));

        // Rows whose folded text is missing or stale — written before the column
        // existed, or by an application that did not know it — are found again
        // once the reconciler has run, as it does when the API starts.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PartyInvitationGroups.ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, "stale"));
            await db.PartyGuests.ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, string.Empty));
            await db.PartyAttendanceGuests.ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, string.Empty));
        }
        Assert.Empty((await PageAsync(owner, partyId, Search("verdi"))).GetProperty("items").EnumerateArray());
        Assert.Empty((await PageAsync(owner, partyId, Search("giulia"))).GetProperty("items").EnumerateArray());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(4, await PartySearchTextReconciler.RunAsync(db));
            Assert.Equal(0, await PartySearchTextReconciler.RunAsync(db));
        }
        Assert.Single((await PageAsync(owner, partyId, Search("verdi"))).GetProperty("items").EnumerateArray());
        Assert.Single((await PageAsync(owner, partyId, Search("giulia"))).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "other:Zeno" }, Names(await PageAsync(owner, partyId, Search("zeno"))));
    }
}

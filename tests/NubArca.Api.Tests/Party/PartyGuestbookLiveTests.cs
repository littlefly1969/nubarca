using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using static NubArca.Api.Tests.Party.PartyCrewTestKit;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

// GUESTBOOK LIVE. Two decisions the regia makes about the book during the
// evening, kept apart from each other and from writing in it:
//
//   * letting the ROOM read the whole book — until then a guest reads only what
//     they wrote, and the party offers no "Guarda il Guestbook";
//   * putting the book on the party's TELEVISION — a takeover the game can
//     never share: the screen has one holder, decided by the server.
//
// What would break if each stopped holding is said beside each test.
public sealed class PartyGuestbookLiveTests : IDisposable
{
    // The invitation kit's host: a public origin is configured, which the
    // Party Crew's invite links need.
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // ── Letting the room read the book ──────────────────────────────────────

    [Fact]
    public async Task By_default_the_room_is_not_offered_the_book_and_reads_only_what_it_wrote()
    {
        var party = await SeedAsync();
        var control = await ControlAsync(party);
        Assert.False(control.GetProperty("viewingEnabled").GetBoolean());
        Assert.False(control.GetProperty("tvActive").GetBoolean());
        Assert.Equal(0, control.GetProperty("version").GetInt32());

        // No "Guarda il Guestbook" on the party…
        Assert.Equal(JsonValueKind.Null, (await CapabilitiesAsync(party)).GetProperty("guestbookViewUrl").ValueKind);

        // …and each guest reads their own memories and nobody else's.
        var ada = _factory.CreateClient();
        var bea = _factory.CreateClient();
        var adaEntry = await WriteAsync(party, ada, "Di Ada");
        var beaEntry = await WriteAsync(party, bea, "Di Bea");

        var adaBook = await BookAsync(party, ada);
        Assert.Equal("mine", adaBook.GetProperty("scope").GetString());
        Assert.Equal(["Di Ada"], Bodies(adaBook));
        Assert.Equal(["Di Bea"], Bodies(await BookAsync(party, bea)));
        // A stranger who wrote nothing reads nothing — not the whole book.
        Assert.Empty(Bodies(await BookAsync(party, _factory.CreateClient())));

        // The rule is the server's, so it holds for the pictures too.
        Assert.Equal(HttpStatusCode.OK, (await ada.GetAsync(PhotoUrl(party, adaEntry))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ada.GetAsync(PhotoUrl(party, beaEntry))).StatusCode);
    }

    [Fact]
    public async Task The_host_opens_the_book_to_the_room_and_closes_it_again()
    {
        var party = await SeedAsync();
        var ada = _factory.CreateClient();
        await WriteAsync(party, ada, "Di Ada");

        var opened = await OkAsync(await CommandAsync(party, "enable_viewing", 0));
        Assert.True(opened.GetProperty("viewingEnabled").GetBoolean());
        Assert.Equal(1, opened.GetProperty("version").GetInt32());
        Assert.Contains("disable_viewing", Commands(opened));

        // The party offers the book, and a guest who wrote nothing reads it all.
        Assert.Equal(
            $"/party/{party.ViewToken}/guestbook",
            (await CapabilitiesAsync(party)).GetProperty("guestbookViewUrl").GetString());
        var stranger = await BookAsync(party, _factory.CreateClient());
        Assert.Equal("all", stranger.GetProperty("scope").GetString());
        Assert.Equal(["Di Ada"], Bodies(stranger));

        var closed = await OkAsync(await CommandAsync(party, "disable_viewing", 1));
        Assert.False(closed.GetProperty("viewingEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await CapabilitiesAsync(party)).GetProperty("guestbookViewUrl").ValueKind);
        Assert.Empty(Bodies(await BookAsync(party, _factory.CreateClient())));
    }

    [Fact]
    public async Task Viewing_changes_nothing_about_writing_quota_moderation_or_the_photographs()
    {
        var party = await SeedAsync(approval: true);
        (await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-slideshow-settings",
            new { maxGuestbookEntriesPerParticipant = 2 })).EnsureSuccessStatusCode();
        var guest = _factory.CreateClient();

        // Closed to the room: the chooser still offers the album, a memory is
        // still written, it still waits for approval and still spends a slot.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.ViewToken}/guestbook/photos")).StatusCode);
        var first = await WriteRawAsync(party, guest, "Prima");
        first.EnsureSuccessStatusCode();
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("remaining").GetInt32());

        // Opened: identical.
        await GuestbookLiveFixture.OpenToTheRoomAsync(party.Owner, party.AlbumId);
        var second = await (await WriteRawAsync(party, guest, "Seconda")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", second.GetProperty("status").GetString());
        Assert.Equal(0, second.GetProperty("remaining").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await WriteRawAsync(party, guest, "Terza")).StatusCode);
    }

    [Fact]
    public async Task After_the_party_the_book_reads_as_it_always_has_and_live_controls_are_refused()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Di Ada");
        await AdvanceAsync(party.Owner, party.PartyId, "end-live");

        // AFTER is not a live surface: the whole book, for everybody.
        var book = await BookAsync(party, _factory.CreateClient());
        Assert.Equal("all", book.GetProperty("scope").GetString());
        Assert.Equal(["Di Ada"], Bodies(book));

        var refused = await CommandAsync(party, "enable_viewing", 0);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("party_not_live", await CodeOf(refused));
        Assert.Empty(Commands(await ControlAsync(party)));
    }

    [Fact]
    public async Task Before_the_party_is_live_the_controls_are_refused()
    {
        var party = await SeedAsync(live: false);
        var refused = await CommandAsync(party, "enable_viewing", 0);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("party_not_live", await CodeOf(refused));
    }

    [Fact]
    public async Task A_stale_version_is_refused_with_the_current_state()
    {
        var party = await SeedAsync();
        await OkAsync(await CommandAsync(party, "enable_viewing", 0));

        // A second regia surface still believing version 0.
        var stale = await CommandAsync(party, "disable_viewing", 0);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var refusal = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", refusal.GetProperty("code").GetString());
        // The refusal carries what the winner left, so the surface re-renders
        // correct rather than merely told off.
        Assert.Equal(1, refusal.GetProperty("control").GetProperty("version").GetInt32());
        Assert.True(refusal.GetProperty("control").GetProperty("viewingEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_command_that_does_not_apply_now_is_an_illegal_transition()
    {
        var party = await SeedAsync();
        var refused = await CommandAsync(party, "disable_viewing", 0);
        Assert.Equal("illegal_transition", await CodeOf(refused));
        var unknown = await party.Owner.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-guestbook-live/commands",
            new { command = "show_this_memory", expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_stranger_has_no_controls_and_cannot_use_any()
    {
        var party = await SeedAsync();
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync($"stranger-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/albums/{party.AlbumId}/party-guestbook-live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-guestbook-live/commands",
            new { command = "enable_viewing", expectedVersion = 0 })).StatusCode);
        Assert.False((await ControlAsync(party)).GetProperty("viewingEnabled").GetBoolean());
    }

    // ── The Party Crew, through the same service ────────────────────────────

    [Fact]
    public async Task A_director_runs_both_halves_and_a_dj_only_the_television()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Da mostrare", approve: false);

        var director = await PairCrewAsync(party, PartyCrewRoles.Director);
        var control = await CrewControlAsync(director, party);
        Assert.Equal(["enable_viewing", "show_on_tv"], Commands(control));

        await OkAsync(await CrewCommandAsync(director, party, "enable_viewing", 0));
        Assert.True((await ControlAsync(party)).GetProperty("viewingEnabled").GetBoolean());
        await OkAsync(await CrewCommandAsync(director, party, "disable_viewing", 1));

        // A collaborator holding only the screen in the room — the DJ's half —
        // is offered the television and refused the book's audience.
        var dj = await PairCrewAsync(party, PartyCrewRoles.Director,
            keepOnly: [PartyCrewCapabilities.ScreensManage]);
        var djControl = await CrewControlAsync(dj, party);
        Assert.Equal(["show_on_tv"], Commands(djControl));
        var forbidden = await CrewCommandAsync(dj, party, "enable_viewing", 2);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("forbidden", await CodeOf(forbidden));
        await OkAsync(await CrewCommandAsync(dj, party, "show_on_tv", 2));
        Assert.True((await ControlAsync(party)).GetProperty("tvActive").GetBoolean());
    }

    [Fact]
    public async Task A_crew_role_with_neither_half_reaches_nothing()
    {
        var party = await SeedAsync();
        // A director stripped of both halves — what the door's role holds.
        var reception = await PairCrewAsync(party, PartyCrewRoles.Director,
            keepOnly: [PartyCrewCapabilities.LifecycleManage]);
        AssertRefused(await reception.GetAsync($"/api/party-crew/parties/{party.PartyId}/guestbook-live"));
        AssertRefused(await reception.PostAsJsonAsync(
            $"/api/party-crew/parties/{party.PartyId}/guestbook-live/commands",
            new { command = "show_on_tv", expectedVersion = 0 }));
    }

    // ── The book on the television ──────────────────────────────────────────

    [Fact]
    public async Task Showing_the_book_takes_the_television_and_returning_gives_it_back()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "In TV");
        var tv = await PairAndAssignAsync(party);
        Assert.Equal("slideshow", await PresentationAsync(tv));

        var shown = await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        Assert.True(shown.GetProperty("tvActive").GetBoolean());
        Assert.Equal("guestbook", shown.GetProperty("tvPresentation").GetString());
        Assert.Equal(["return_to_slideshow"], Commands(shown).Where(c => c.Contains("slideshow") || c.Contains("tv")));
        Assert.Equal("guestbook", await PresentationAsync(tv));

        // Showing the book on the television does not open it on the phones.
        Assert.Equal(JsonValueKind.Null, (await CapabilitiesAsync(party)).GetProperty("guestbookViewUrl").ValueKind);

        var back = await OkAsync(await CommandAsync(party, "return_to_slideshow", 1));
        Assert.False(back.GetProperty("tvActive").GetBoolean());
        Assert.Equal("slideshow", await PresentationAsync(tv));
    }

    [Fact]
    public async Task A_television_that_was_off_converges_on_the_current_presentation()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Al ritorno");
        var tv = await PairAndAssignAsync(party);

        // Nothing listening: the request is the server's, not the device's.
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        Assert.Equal("guestbook", await PresentationAsync(tv));
        await OkAsync(await CommandAsync(party, "return_to_slideshow", 1));
        Assert.Equal("slideshow", await PresentationAsync(tv));
    }

    [Fact]
    public async Task An_empty_book_cannot_take_the_television()
    {
        var party = await SeedAsync(approval: true);
        var control = await ControlAsync(party);
        Assert.DoesNotContain("show_on_tv", Commands(control));
        Assert.Equal("guestbook_empty", control.GetProperty("tvUnavailableReason").GetString());
        Assert.Equal("guestbook_empty", await CodeOf(await CommandAsync(party, "show_on_tv", 0)));

        // Pending only is still empty for the television…
        var pending = await WriteAsync(party, _factory.CreateClient(), "In attesa", approve: false);
        Assert.Equal(1, (await ControlAsync(party)).GetProperty("pendingEntries").GetInt32());
        Assert.Equal("guestbook_empty", await CodeOf(await CommandAsync(party, "show_on_tv", 0)));

        // …and so are rejected and hidden ones.
        await ModerateAsync(party, pending, "reject");
        var hidden = await WriteAsync(party, _factory.CreateClient(), "Nascosta", approve: true);
        await ModerateAsync(party, hidden, "hide");
        Assert.Equal("guestbook_empty", await CodeOf(await CommandAsync(party, "show_on_tv", 0)));
    }

    [Fact]
    public async Task The_television_reads_only_the_visible_memories_in_the_books_order()
    {
        var party = await SeedAsync(approval: true);
        await WriteAsync(party, _factory.CreateClient(), "Prima", approve: true);
        await WriteAsync(party, _factory.CreateClient(), "In attesa", approve: false);
        await WriteAsync(party, _factory.CreateClient(), "Seconda", approve: true);
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        var deck = await TvJsonAsync("/api/tv/party/guestbook", tv);
        var entries = deck.GetProperty("entries");
        Assert.Equal(["Seconda", "Prima"], entries.EnumerateArray().Select(e => e.GetProperty("body").GetString()!).ToArray());

        var first = entries[0];
        Assert.Equal(
            ["id", "authorDisplayName", "body", "createdAt", "template", "media"],
            first.EnumerateObject().Select(p => p.Name).ToArray());
        var url = first.GetProperty("media").GetProperty("url").GetString()!;
        Assert.Equal($"/api/tv/party/guestbook/{first.GetProperty("id").GetGuid()}/photo", url);
        var photo = await TvGetAsync(url, tv);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType?.MediaType);

        // Nothing internal crosses: no blob, no source file, no moderation.
        var raw = await (await TvGetAsync("/api/tv/party/guestbook", tv)).Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "blobObjectId", "previewBlobObjectId", "sourceMediaItemId",
                     "storageKey", "status", "partyParticipantId", "ownerUserId", "moderatedAt" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hiding_the_last_visible_memory_hands_the_television_back()
    {
        var party = await SeedAsync();
        var only = await WriteAsync(party, _factory.CreateClient(), "L'unica");
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        Assert.Equal("guestbook", await PresentationAsync(tv));

        await ModerateAsync(party, only, "hide");

        Assert.Equal("slideshow", await PresentationAsync(tv));
        // The request is withdrawn too, so a memory approved later does not put
        // the book back on the screen by itself.
        var control = await ControlAsync(party);
        Assert.False(control.GetProperty("tvActive").GetBoolean());
        await ModerateAsync(party, only, "restore");
        Assert.Equal("slideshow", await PresentationAsync(tv));
    }

    [Fact]
    public async Task A_closed_book_cannot_take_the_television_and_closing_it_takes_it_off()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Sullo schermo");
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        (await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-contributions", new { guestbookEnabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal("slideshow", await PresentationAsync(tv));
        var control = await ControlAsync(party);
        Assert.False(control.GetProperty("tvActive").GetBoolean());
        Assert.Equal("guestbook_not_available", control.GetProperty("tvUnavailableReason").GetString());
        Assert.Equal("guestbook_not_available",
            await CodeOf(await CommandAsync(party, "show_on_tv", control.GetProperty("version").GetInt32())));
    }

    [Fact]
    public async Task Ending_the_party_takes_the_book_off_the_television()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Fine serata");
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        await AdvanceAsync(party.Owner, party.PartyId, "end-live");

        var control = await ControlAsync(party);
        Assert.False(control.GetProperty("tvActive").GetBoolean());
        Assert.False(control.GetProperty("partyLive").GetBoolean());
        Assert.Equal("party_not_live", control.GetProperty("tvUnavailableReason").GetString());
    }

    // ── One holder for the screen ───────────────────────────────────────────

    [Fact]
    public async Task While_the_game_holds_the_screen_the_book_cannot_take_it()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Aspetto");
        var tv = await PairAndAssignAsync(party);
        await EnableGameAsync(party);
        // The lobby is the game's takeover: the join code is on the screen.
        Assert.Equal("game", await PresentationAsync(tv));

        var control = await ControlAsync(party);
        Assert.DoesNotContain("show_on_tv", Commands(control));
        Assert.Equal("game_active", control.GetProperty("tvUnavailableReason").GetString());
        Assert.Equal("game", control.GetProperty("tvPresentation").GetString());
        var refused = await CommandAsync(party, "show_on_tv", 0);
        Assert.Equal("game_active", await CodeOf(refused));
        Assert.Equal("game", await PresentationAsync(tv));
    }

    [Fact]
    public async Task While_the_book_holds_the_screen_the_game_cannot_take_it()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "In onda");
        var tv = await PairAndAssignAsync(party);
        await EnableGameAsync(party);
        await PlayToIntermissionAsync(party);
        Assert.Equal("slideshow", await PresentationAsync(tv));

        await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        Assert.Equal("guestbook", await PresentationAsync(tv));

        // The game's read model offers nothing that would take the screen back…
        var game = await OwnerGameAsync(party);
        Assert.True(game.GetProperty("guestbookOnTv").GetBoolean());
        var offered = game.GetProperty("availableCommands").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.DoesNotContain("next_challenge", offered);
        Assert.DoesNotContain("finish", offered);

        // …and the server refuses it on arrival anyway.
        var refused = await GameCommandAsync(party, "next_challenge", game.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_active", await CodeOf(refused));
        Assert.Equal("guestbook", await PresentationAsync(tv));

        // Back to the slideshow, and the game may have its screen again.
        await OkAsync(await CommandAsync(party, "return_to_slideshow", 1));
        (await GameCommandAsync(party, "next_challenge", game.GetProperty("version").GetInt32()))
            .EnsureSuccessStatusCode();
        Assert.Equal("game", await PresentationAsync(tv));
    }

    [Fact]
    public async Task Switching_the_game_on_cannot_take_the_screen_from_the_book()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Prima del gioco");
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        var refused = await GameSettingsAsync(party, enabled: true);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_active", await CodeOf(refused));
        Assert.Equal("guestbook", await PresentationAsync(tv));
    }

    [Fact]
    public async Task A_game_operation_that_leaves_the_screen_alone_still_works()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Pausa");
        await EnableGameAsync(party);
        await PlayToIntermissionAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        // Planning the rest of the evening touches no screen.
        var game = await OwnerGameAsync(party);
        var remaining = game.GetProperty("plan").EnumerateArray()
            .First(e => e.GetProperty("state").GetString() == "remaining");
        var planned = await party.Owner.PostAsJsonAsync($"/api/albums/{party.AlbumId}/party-game/plan", new
        {
            action = "exclude",
            challengeId = remaining.GetProperty("id").GetGuid(),
            expectedVersion = game.GetProperty("version").GetInt32(),
        });
        planned.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Stored_state_claiming_both_holders_shows_the_game()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Corrotto");
        var tv = await PairAndAssignAsync(party);
        await EnableGameAsync(party);

        // No command can produce this; written by hand to prove the rule is
        // still deterministic if it ever happened.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyAlbumLinks
                .Where(l => l.AlbumId == party.AlbumId && l.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.GuestbookTvActive, true));
        }

        Assert.Equal("game", await PresentationAsync(tv));
    }

    // ── Who may read the book on a television ───────────────────────────────

    [Fact]
    public async Task The_television_reads_the_book_only_while_its_party_shows_it()
    {
        var party = await SeedAsync();
        var entry = await WriteAsync(party, _factory.CreateClient(), "Solo in onda");
        var tv = await PairAndAssignAsync(party);

        // Slideshow: assigned, valid, and still nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync($"/api/tv/party/guestbook/{entry}/photo", tv)).StatusCode);

        await OkAsync(await CommandAsync(party, "show_on_tv", 0));
        Assert.Equal(HttpStatusCode.OK, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);

        // Back to the slideshow: closed again, with nothing revoked.
        await OkAsync(await CommandAsync(party, "return_to_slideshow", 1));
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);

        // No session at all is a 401, not a party.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/tv/party/guestbook")).StatusCode);
    }

    [Fact]
    public async Task A_television_reads_only_the_party_it_is_assigned_to()
    {
        // Somebody else's party has its book on the screen…
        var theirs = await SeedAsync();
        await WriteAsync(theirs, _factory.CreateClient(), "Festa A");
        await OkAsync(await CommandAsync(theirs, "show_on_tv", 0));

        // …and this television, assigned to a different party, reads none of
        // it: there is no party to name, only the one it is assigned to.
        var mine = await SeedAsync();
        await WriteAsync(mine, _factory.CreateClient(), "Festa B");
        var tv = await PairAndAssignAsync(mine);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);

        // Its own party's book, while that is on the screen — and nothing once
        // the television is reassigned elsewhere.
        await OkAsync(await CommandAsync(mine, "show_on_tv", 0));
        var deck = await TvJsonAsync("/api/tv/party/guestbook", tv);
        Assert.Equal(["Festa B"], deck.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("body").GetString()!).ToArray());
        await AssignAsync(mine, null);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);
    }

    [Fact]
    public async Task A_television_never_receives_the_picture_of_a_memory_that_is_not_visible()
    {
        var party = await SeedAsync(approval: true);
        await WriteAsync(party, _factory.CreateClient(), "Visibile", approve: true);
        var pending = await WriteAsync(party, _factory.CreateClient(), "In attesa", approve: false);
        var hidden = await WriteAsync(party, _factory.CreateClient(), "Nascosta", approve: true);
        await ModerateAsync(party, hidden, "hide");
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync($"/api/tv/party/guestbook/{pending}/photo", tv)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync($"/api/tv/party/guestbook/{hidden}/photo", tv)).StatusCode);
    }

    [Fact]
    public async Task While_the_game_holds_the_screen_the_book_routes_stay_shut()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Niente");
        var tv = await PairAndAssignAsync(party);
        await EnableGameAsync(party);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync("/api/tv/party/guestbook", tv)).StatusCode);
    }

    [Fact]
    public async Task The_book_on_screen_never_makes_a_game_display_grant_mintable()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Nessun grant");
        var tv = await PairAndAssignAsync(party);
        await OkAsync(await CommandAsync(party, "show_on_tv", 0));

        // The game stage's credential is the game's alone.
        var mint = await TvGetAsync("/api/tv/party-display/grant", tv, post: true);
        Assert.False(mint.IsSuccessStatusCode);
    }

    // ── A copy of the party ─────────────────────────────────────────────────

    [Fact]
    public async Task A_duplicate_keeps_the_rooms_setting_and_none_of_the_screen()
    {
        var party = await SeedAsync();
        await WriteAsync(party, _factory.CreateClient(), "Originale");
        await OkAsync(await CommandAsync(party, "enable_viewing", 0));
        await OkAsync(await CommandAsync(party, "show_on_tv", 1));

        var copy = await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/duplicate", new { title = (string?)null });
        copy.EnsureSuccessStatusCode();
        var copyId = (await copy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var links = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyAlbumLinks
            .AsNoTracking().Where(l => l.PartyId == copyId).ToListAsync();
        Assert.All(links, l =>
        {
            Assert.True(l.GuestbookViewingEnabled);
            Assert.False(l.GuestbookTvActive);
            Assert.Equal(0, l.GuestbookControlVersion);
        });
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed record Party(
        HttpClient Owner, Guid OwnerId, Guid PartyId, Guid AlbumId, string ViewToken, Guid PhotoId);

    private async Task<Party> SeedAsync(bool live = true, bool approval = false, Party? owner = null)
    {
        // Every Party permission — a role may only delegate what its host holds —
        // and the television's, because these tests also pair one.
        var (ownerId, client) = owner is null
            ? await _factory.CreatePermissionClientAsync(
                $"host-{Guid.NewGuid():N}@example.com",
                [.. EveryPartyPermission, NubArca.Api.Access.Permissions.TvManage])
            : (owner.OwnerId, owner.Owner);
        var partyId = await CreatePartyAsync(client);
        var (albumId, viewToken, _) = await OpenPublicQrAsync(client, partyId, $"Album {Guid.NewGuid():N}");
        (await client.PatchAsJsonAsync($"/api/albums/{albumId}/party-contributions",
            new { guestbookEnabled = true, requireGuestbookApproval = approval })).EnsureSuccessStatusCode();
        var photoId = await UploadPhotoAsync(client);
        (await client.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = photoId }))
            .EnsureSuccessStatusCode();
        if (live) await AdvanceAsync(client, partyId, "start-live");
        return new Party(client, ownerId, partyId, albumId, viewToken, photoId);
    }

    private static Task<HttpResponseMessage> WriteRawAsync(Party party, HttpClient guest, string body) =>
        guest.PostAsJsonAsync($"/api/party/{party.ViewToken}/guestbook", new
        {
            sourceMediaItemId = party.PhotoId,
            authorDisplayName = "Ospite",
            body,
            templateKey = "nubarca",
            crop = new { centerX = 0.5, centerY = 0.5, zoom = 1.0 },
        });

    /// <summary>
    /// Writes a memory and returns its id. With approval on, <paramref name="approve"/>
    /// says whether the host lets it in.
    /// </summary>
    private async Task<Guid> WriteAsync(Party party, HttpClient guest, string body, bool approve = true)
    {
        var response = await WriteRawAsync(party, guest, body);
        response.EnsureSuccessStatusCode();
        var written = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = written.GetProperty("id").GetGuid();
        if (approve && written.GetProperty("status").GetString() == "pending")
            await ModerateAsync(party, id, "approve");
        return id;
    }

    private static async Task ModerateAsync(Party party, Guid entryId, string action) =>
        Assert.Equal(HttpStatusCode.NoContent, (await party.Owner.PostAsync(
            $"/api/parties/{party.PartyId}/guestbook/{entryId}/{action}", null)).StatusCode);

    private static Task<JsonElement> ControlAsync(Party party) =>
        GuestbookLiveFixture.ControlAsync(party.Owner, party.AlbumId);

    private static Task<HttpResponseMessage> CommandAsync(Party party, string command, int version) =>
        GuestbookLiveFixture.CommandAsync(party.Owner, party.AlbumId, command, version);

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Commands(JsonElement control) =>
        control.GetProperty("availableCommands").EnumerateArray().Select(c => c.GetString()!).ToArray();

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString()!
            : body.TryGetProperty("error", out var error) ? error.GetString()! : "";
    }

    private async Task<JsonElement> CapabilitiesAsync(Party party) =>
        (await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{party.ViewToken}"))
            .GetProperty("capabilities");

    private static Task<JsonElement> BookAsync(Party party, HttpClient reader) =>
        reader.GetFromJsonAsync<JsonElement>($"/api/party/{party.ViewToken}/guestbook");

    private static string[] Bodies(JsonElement book) =>
        book.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("body").GetString()!).ToArray();

    private static string PhotoUrl(Party party, Guid entryId) =>
        $"/api/party/{party.ViewToken}/guestbook/{entryId}/photo";

    // The Party Crew.

    /// <summary>
    /// A paired collaborator. With <paramref name="keepOnly"/>, every other
    /// capability grant is removed — how a test reaches the roles this release
    /// does not yet offer (DJ, reception), whose grants the resolver honours.
    /// </summary>
    private async Task<HttpClient> PairCrewAsync(Party party, string role, string[]? keepOnly = null)
    {
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            party.Owner, party.PartyId, "Sara", $"crew-{Guid.NewGuid():N}@example.com", role);
        var device = await PairAsync(_factory, invite);
        if (keepOnly is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyCollaboratorGrants
                .Where(g => g.PartyCollaboratorId == collaboratorId && !keepOnly.Contains(g.CapabilityKey))
                .ExecuteDeleteAsync();
        }
        return device;
    }

    private static Task<JsonElement> CrewControlAsync(HttpClient device, Party party) =>
        device.GetFromJsonAsync<JsonElement>($"/api/party-crew/parties/{party.PartyId}/guestbook-live");

    private static Task<HttpResponseMessage> CrewCommandAsync(HttpClient device, Party party, string command, int version) =>
        device.PostAsJsonAsync($"/api/party-crew/parties/{party.PartyId}/guestbook-live/commands",
            new { command, expectedVersion = version });

    // The game.

    private static async Task EnableGameAsync(Party party)
    {
        (await GameSettingsAsync(party, enabled: true)).EnsureSuccessStatusCode();
        foreach (var title in new[] { "Uno", "Due", "Tre" })
            (await party.Owner.PostAsJsonAsync($"/api/albums/{party.AlbumId}/party-challenges", new
            {
                title, body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
            })).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> GameSettingsAsync(Party party, bool enabled) =>
        party.Owner.PatchAsJsonAsync($"/api/albums/{party.AlbumId}/party-game-settings", new
        {
            gameEnabled = enabled, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null, priorityVotingEnabled = false,
        });

    private static Task<JsonElement> OwnerGameAsync(Party party) =>
        party.Owner.GetFromJsonAsync<JsonElement>($"/api/albums/{party.AlbumId}/party-game");

    private static Task<HttpResponseMessage> GameCommandAsync(Party party, string command, int version) =>
        party.Owner.PostAsJsonAsync($"/api/albums/{party.AlbumId}/party-game/commands",
            new { command, expectedVersion = version });

    /// <summary>One activity played through, then a pause: the screen goes back to the room.</summary>
    private static async Task PlayToIntermissionAsync(Party party)
    {
        foreach (var command in new[] { "start", "start_challenge", "open_voting", "close_voting", "reveal_result", "return_to_party" })
        {
            var version = (await OwnerGameAsync(party)).GetProperty("version").GetInt32();
            (await GameCommandAsync(party, command, version)).EnsureSuccessStatusCode();
        }
        Assert.Equal("intermission", (await OwnerGameAsync(party)).GetProperty("phase").GetString());
    }

    // The television.

    private async Task<string> PairAndAssignAsync(Party party)
    {
        var tvClient = _factory.CreateClient();
        var started = (await (await tvClient.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await party.Owner.PostAsJsonAsync($"/api/tv/pairing/{started.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret,
            personalCode = "URDLSUDLR", personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        await AssignAsync(party, party.AlbumId);
        return cookie;
    }

    /// <summary>
    /// Points the owner's television at a party's album, or back to general.
    /// Each test pairs at most one television per host, so it is that one.
    /// </summary>
    private async Task AssignAsync(Party owner, Guid? albumId)
    {
        Guid sessionId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            sessionId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().TvSessions
                .AsNoTracking().SingleAsync(t => t.OwnerUserId == owner.OwnerId && t.RevokedAt == null)).Id;
        }
        (await owner.Owner.PatchAsJsonAsync($"/api/tv-devices/{sessionId}/assignment",
            albumId is null
                ? new { kind = "general", albumId = (Guid?)null }
                : new { kind = "party", albumId })).EnsureSuccessStatusCode();
    }

    private async Task<string?> PresentationAsync(string cookie) =>
        (await TvJsonAsync("/api/tv/session", cookie)).GetProperty("assignment").GetProperty("presentation").GetString();

    private async Task<JsonElement> TvJsonAsync(string url, string cookie)
    {
        var response = await TvGetAsync(url, cookie);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> TvGetAsync(string url, string setCookie, bool post = false)
    {
        var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}

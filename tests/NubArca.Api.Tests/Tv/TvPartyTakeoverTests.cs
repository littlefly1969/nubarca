using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Tv;

/// <summary>
/// A paired television assigned to a party, taken over by it end to end.
///
/// The assignment says WHICH party; the presentation projected beside it says
/// WHAT that party wants on the screen right now — its slideshow, its game, or
/// an honest "unavailable". These tests drive the real endpoints and the real
/// game commands and read the answer the television reads, so what is asserted
/// is the contract the device navigates by, not an internal method.
///
/// The one thing they keep proving on the way is what the takeover is NOT: the
/// game is never moved by it (FINISHED stays FINISHED), a read never writes,
/// the television never becomes a participant, and no link id, token or party
/// URL reaches the device through the control plane.
/// </summary>
public sealed class TvPartyTakeoverTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public TvPartyTakeoverTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_general_television_is_told_it_is_general()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        var assignment = await TvAssignmentAsync(cookie);
        Assert.Equal("general", assignment.GetProperty("kind").GetString());
        Assert.Equal("general", assignment.GetProperty("presentation").GetString());
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("assignmentKey").ValueKind);
    }

    [Fact]
    public async Task A_party_with_a_game_takes_the_screen_on_the_very_first_read()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, sessionId, album);

        // The same read a television makes at startup: it already says what to
        // mount, so the device needs no second request to discover a takeover.
        var response = await TvGet("/api/tv/session", cookie);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var assignment = JsonDocument.Parse(raw).RootElement.GetProperty("assignment");
        Assert.Equal("party", assignment.GetProperty("kind").GetString());
        Assert.Equal("game", assignment.GetProperty("presentation").GetString());
        Assert.True(assignment.GetProperty("partyAvailable").GetBoolean());
        Assert.Equal(album, assignment.GetProperty("albumId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(assignment.GetProperty("assignmentKey").GetString()));

        // Nothing that identifies the party beyond the owner's own album id.
        var linkId = await ActiveLinkIdAsync(album);
        Assert.DoesNotContain(linkId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(linkId.ToString("N"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/party/", raw);

        // The owner's device list sees the same presentation, and no key: the
        // key is the television's own business.
        var device = (await owner.GetFromJsonAsync<JsonElement>("/api/tv-devices"))
            .EnumerateArray().Single().GetProperty("assignment");
        Assert.Equal("game", device.GetProperty("presentation").GetString());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("assignmentKey").ValueKind);
    }

    [Fact]
    public async Task Without_a_game_the_assigned_party_is_its_slideshow()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: false);
        await AssignAsync(owner, sessionId, album);

        Assert.Equal("slideshow", await PresentationAsync(cookie));

        // Switching the game on takes the screen (the lobby is the takeover)…
        await SetGameAsync(owner, album, enabled: true);
        await AddChallengesAsync(owner, album);
        Assert.Equal("game", await PresentationAsync(cookie));

        // …and switching it off gives the screen back.
        await SetGameAsync(owner, album, enabled: false);
        Assert.Equal("slideshow", await PresentationAsync(cookie));
    }

    [Fact]
    public async Task A_game_takes_the_screen_only_while_the_party_is_live()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var album = await PartyAlbumAsync(owner, "Festa", game: true, live: false);
        await AssignAsync(owner, await SingleSessionIdAsync(ownerId), album);

        // Published but not started: the lobby's code would send guests to a
        // game they cannot join yet. The party's slideshow instead.
        Assert.Equal("slideshow", await PresentationAsync(cookie));

        var partyId = await PartyIdAsync(owner, album);
        await TransitionAsync(owner, partyId, "start-live");
        Assert.Equal("game", await PresentationAsync(cookie));

        // After the party: never a game nobody can join any more.
        await TransitionAsync(owner, partyId, "end-live");
        Assert.NotEqual("game", await PresentationAsync(cookie));
    }

    [Fact]
    public async Task The_lobby_code_is_a_scalable_picture_with_its_quiet_zone_inside()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        await AssignAsync(owner, await SingleSessionIdAsync(ownerId),
            await PartyAlbumAsync(owner, "Festa", game: true));
        var grant = await MintAsync(cookie);

        var response = await DisplayAsync("/api/party-display/join-qr", grant.Token);
        response.EnsureSuccessStatusCode();
        var svg = await response.Content.ReadAsStringAsync();
        var root = Regex.Match(svg, "<svg[^>]*>").Value;
        // A viewBox and no fixed size: the stage scales the code into whatever
        // room the lobby leaves, and a fixed-pixel picture would be clipped or
        // stranded small inside it.
        Assert.Contains("viewBox=\"0 0 ", root);
        Assert.DoesNotMatch("\\swidth=\"", root);
        Assert.DoesNotMatch("\\sheight=\"", root);
        // The light ground — the quiet zone with it — is part of the picture.
        Assert.Contains("#ffffff", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_game_in_progress_keeps_the_screen_through_every_phase()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        await AssignAsync(owner, await SingleSessionIdAsync(ownerId),
            await PartyAlbumAsync(owner, "Festa", game: true) is var album ? album : default);

        var version = 0;
        foreach (var command in new[]
            { "start", "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
        {
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
            Assert.Equal("game", await PresentationAsync(cookie));
        }
    }

    [Fact]
    public async Task Finishing_hands_the_screen_back_after_the_closing_card_and_the_game_stays_finished()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, sessionId, album);
        var key = (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString();
        var linkId = await ActiveLinkIdAsync(album);

        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var finished = await CommandAsync(owner, album, "finish", version);
        Assert.Equal("finished", finished.GetProperty("phase").GetString());
        var finishedVersion = finished.GetProperty("version").GetInt32();

        // The closing card holds the screen for the dwell — on the SERVER's
        // clock, so every television in the room agrees.
        Assert.Equal("game", await PresentationAsync(cookie));

        // After the dwell the SAME party's slideshow is back — nothing else.
        await AgeFinishedAtAsync(album, TvPartyPresentations.FinishedDwell + TimeSpan.FromSeconds(1));
        var after = await TvAssignmentAsync(cookie);
        Assert.Equal("slideshow", after.GetProperty("presentation").GetString());
        Assert.Equal("party", after.GetProperty("kind").GetString());
        Assert.Equal(album, after.GetProperty("albumId").GetGuid());
        Assert.Equal(key, after.GetProperty("assignmentKey").GetString());

        // And the game did not move: FINISHED at the same version, the session
        // row still there, the assignment untouched.
        var owners = await OwnerGameAsync(owner, album);
        Assert.Equal("finished", owners.GetProperty("phase").GetString());
        Assert.Equal("finished", owners.GetProperty("status").GetString());
        Assert.Equal(finishedVersion, owners.GetProperty("version").GetInt32());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PartyGamePhases.Finished, (await db.PartyGameSessions.AsNoTracking().SingleAsync()).Phase);
        var tv = await db.TvSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        Assert.Equal(TvDisplayAssignments.Party, tv.DisplayAssignment);
        Assert.Equal(linkId, tv.AssignedPartyAlbumLinkId);
    }

    [Fact]
    public async Task Restarting_the_game_takes_the_screen_again_without_touching_the_assignment()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, sessionId, album);
        var key = (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString();

        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        version = (await CommandAsync(owner, album, "finish", version)).GetProperty("version").GetInt32();
        await AgeFinishedAtAsync(album, TimeSpan.FromMinutes(5));
        Assert.Equal("slideshow", await PresentationAsync(cookie));

        var lobby = await CommandAsync(owner, album, "restart_game", version);
        Assert.Equal("lobby", lobby.GetProperty("phase").GetString());

        // The next control read: the lobby's takeover, same party, same key —
        // no pairing, no reassignment.
        var assignment = await TvAssignmentAsync(cookie);
        Assert.Equal("game", assignment.GetProperty("presentation").GetString());
        Assert.Equal(key, assignment.GetProperty("assignmentKey").GetString());
    }

    [Fact]
    public async Task A_party_that_is_switched_off_fails_closed_and_its_successor_is_a_new_party()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, sessionId, album);
        var first = (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString();

        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        // Still a PARTY assignment, honestly unavailable — not general, and no
        // capability can be minted for it.
        var gone = await TvAssignmentAsync(cookie);
        Assert.Equal("party", gone.GetProperty("kind").GetString());
        Assert.Equal("unavailable", gone.GetProperty("presentation").GetString());
        Assert.False(gone.GetProperty("partyAvailable").GetBoolean());
        Assert.Equal(album, gone.GetProperty("albumId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookie)).StatusCode);

        // Re-enabling party mode mints a NEW link. The television does not
        // adopt it by itself: it still names the party that is over.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        Assert.Equal("unavailable", await PresentationAsync(cookie));

        // Only the owner assigning it again moves it — and it is a different
        // party to the television, so everything on screen starts over.
        await AssignAsync(owner, sessionId, album);
        var again = await TvAssignmentAsync(cookie);
        Assert.NotEqual("unavailable", again.GetProperty("presentation").GetString());
        Assert.NotEqual(first, again.GetProperty("assignmentKey").GetString());
    }

    [Fact]
    public async Task Party_A_to_party_B_is_a_different_party_to_the_television()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var partyA = await PartyAlbumAsync(owner, "Festa di Anna", game: true);
        var partyB = await PartyAlbumAsync(owner, "Compleanno", game: true);

        await AssignAsync(owner, sessionId, partyA);
        var keyA = (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString();
        // A poll is not a change.
        Assert.Equal(keyA, (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString());

        await AssignAsync(owner, sessionId, partyB);
        var b = await TvAssignmentAsync(cookie);
        Assert.Equal(partyB, b.GetProperty("albumId").GetGuid());
        Assert.NotEqual(keyA, b.GetProperty("assignmentKey").GetString());

        // Back to A — the same link, so the same party again.
        await AssignAsync(owner, sessionId, partyA);
        Assert.Equal(keyA, (await TvAssignmentAsync(cookie)).GetProperty("assignmentKey").GetString());
    }

    [Fact]
    public async Task Deleting_the_album_takes_the_television_back_to_general_with_its_grant()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);

        // REALLY general: the row, the presentation, and the capability.
        var assignment = await TvAssignmentAsync(cookie);
        Assert.Equal("general", assignment.GetProperty("kind").GetString());
        Assert.Equal("general", assignment.GetProperty("presentation").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyDisplayGrants.ToListAsync());
        var tv = await db.TvSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        Assert.Equal(TvDisplayAssignments.General, tv.DisplayAssignment);
        Assert.Null(tv.AssignedPartyAlbumLinkId);
    }

    [Fact]
    public async Task Reading_the_presentation_writes_nothing_and_the_heartbeat_writes_presence()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        await AssignAsync(owner, sessionId, await PartyAlbumAsync(owner, "Festa", game: true));
        var anHourAgo = DateTime.UtcNow.AddHours(-1);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().TvSessions
                .Where(x => x.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, anHourAgo));

        // The brisk control read, several times over.
        for (var i = 0; i < 3; i++) Assert.Equal("game", await PresentationAsync(cookie));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A television polling a party that has not begun must not begin it,
            // must not join it, and must not be counted as present by a read.
            Assert.Empty(await db.PartyGameSessions.ToListAsync());
            Assert.Empty(await db.PartyParticipants.ToListAsync());
            Assert.Empty(await db.PartyDisplayGrants.ToListAsync());
            var seen = (await db.TvSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId)).LastSeenAt;
            Assert.True(seen < anHourAgo.AddSeconds(1), "a read does not write presence");
        }

        // The heartbeat is the one read that does.
        var beat = await TvGet("/api/tv/session/heartbeat", cookie, post: true);
        beat.EnsureSuccessStatusCode();
        Assert.Equal("game", (await beat.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("assignment").GetProperty("presentation").GetString());
        using (var scope = _factory.Services.CreateScope())
        {
            var seen = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().TvSessions
                .AsNoTracking().SingleAsync(x => x.Id == sessionId)).LastSeenAt;
            Assert.True(seen > anHourAgo.AddMinutes(59));
        }
    }

    [Fact]
    public async Task An_assigned_television_can_show_its_party_even_when_the_album_is_not_on_tv()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookieA = await PairTvAsync(owner);
        var tvA = await SingleSessionIdAsync(ownerId);
        var cookieB = await PairTvAsync(owner);
        var album = await PartyAlbumAsync(owner, "Festa", game: false);
        var photo = await AddPngAsync(owner, album, "festa.png");
        // Party does not imply Show-on-TV. This album is NOT on the owner's
        // televisions.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/tv-settings", new { showOnTv = false }))
            .EnsureSuccessStatusCode();

        var items = $"/api/tv/albums/{album}/items";
        var messages = $"/api/tv/albums/{album}/party-messages";
        var thumbnail = $"/api/tv/media/{photo}/thumbnail";

        // Not assigned: not visible, exactly as before.
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(items, cookieA)).StatusCode);

        await AssignAsync(owner, tvA, album);
        Assert.Equal("slideshow", await PresentationAsync(cookieA));

        // The ASSIGNED television may show it: items, greetings, bytes.
        var listed = await TvJsonAsync(items, cookieA);
        Assert.Contains(listed.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetGuid() == photo);
        Assert.Equal(HttpStatusCode.OK, (await TvGet(messages, cookieA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGet(thumbnail, cookieA)).StatusCode);
        // It is not a way to browse: the album list is unchanged.
        Assert.DoesNotContain(album.ToString(),
            await (await TvGet("/api/tv/albums", cookieA)).Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        // The owner's OTHER television was assigned nothing and sees nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(items, cookieB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(messages, cookieB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(thumbnail, cookieB)).StatusCode);

        // The grant IS the assignment, re-read every request: general closes it…
        await AssignAsync(owner, tvA, null);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(items, cookieA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(thumbnail, cookieA)).StatusCode);

        // …and so does the party ending, even while the television still names it.
        await AssignAsync(owner, tvA, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(items, cookieA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(thumbnail, cookieA)).StatusCode);
    }

    [Fact]
    public async Task A_display_grant_is_minted_and_honoured_only_while_the_presentation_is_game()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var album = await PartyAlbumAsync(owner, "Festa", game: true, live: false);
        await AssignAsync(owner, await SingleSessionIdAsync(ownerId), album);
        Guid challenge;
        using (var scope = _factory.Services.CreateScope())
            challenge = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyChallenges
                .AsNoTracking().FirstAsync(c => c.AlbumId == album)).Id;
        // Every display route answers to the same capability.
        var routes = new[]
        {
            "/api/party-display/game", "/api/party-display/join-qr",
            $"/api/party-display/challenges/{challenge}/media",
        };

        // Party not live → slideshow → no grant.
        Assert.Equal("slideshow", await PresentationAsync(cookie));
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookie)).StatusCode);

        // Live → game → a grant.
        await TransitionAsync(owner, await PartyIdAsync(owner, album), "start-live");
        Assert.Equal("game", await PresentationAsync(cookie));
        var grant = await MintAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync(routes[0], grant.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync(routes[1], grant.Token)).StatusCode);

        // Game switched off → slideshow: the live grant stops on EVERY display
        // route at once, and no new grant is minted.
        await SetGameAsync(owner, album, enabled: false);
        Assert.Equal("slideshow", await PresentationAsync(cookie));
        foreach (var route in routes)
            Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync(route, grant.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookie)).StatusCode);

        // Game back on → game → mintable.
        await SetGameAsync(owner, album, enabled: true);
        grant = await MintAsync(cookie);

        // FINISHED, inside the first 15 s: the closing card's grant works, and
        // a remount in that window could still mint.
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        version = (await CommandAsync(owner, album, "finish", version)).GetProperty("version").GetInt32();
        Assert.Equal("game", await PresentationAsync(cookie));
        Assert.Equal("finished",
            (await DisplayJsonAsync(routes[0], grant.Token)).GetProperty("phase").GetString());
        grant = await MintAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync(routes[0], grant.Token)).StatusCode);

        // FINISHED past the dwell: slideshow. The grant is no longer valid and
        // a new mint is refused — while the game itself stays FINISHED.
        await AgeFinishedAtAsync(album, TvPartyPresentations.FinishedDwell + TimeSpan.FromSeconds(1));
        Assert.Equal("slideshow", await PresentationAsync(cookie));
        foreach (var route in routes)
            Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync(route, grant.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookie)).StatusCode);
        Assert.Equal("finished", (await OwnerGameAsync(owner, album)).GetProperty("phase").GetString());

        // restart_game → lobby → game → mintable again.
        await CommandAsync(owner, album, "restart_game", version);
        Assert.Equal("game", await PresentationAsync(cookie));
        var fresh = await MintAsync(cookie);
        Assert.Equal("lobby", (await DisplayJsonAsync(routes[0], fresh.Token)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task An_off_tv_party_album_closes_the_moment_its_party_stops_being_showable()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var tv = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa", game: false);
        var photo = await AddPngAsync(owner, album, "festa.png");
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/tv-settings", new { showOnTv = false }))
            .EnsureSuccessStatusCode();
        await AssignAsync(owner, tv, album);

        var items = $"/api/tv/albums/{album}/items";
        var messages = $"/api/tv/albums/{album}/party-messages";
        var thumbnail = $"/api/tv/media/{photo}/thumbnail";
        Assert.Equal("slideshow", await PresentationAsync(cookie));
        Assert.Equal(HttpStatusCode.OK, (await TvGet(items, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGet(messages, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGet(thumbnail, cookie)).StatusCode);

        // The party's guest window closes while it is still being held. Nothing
        // about the LINK changes — it stays enabled, unrevoked and unexpired, so
        // a check of the link alone would keep the album open — but the display
        // resolver refuses the party, and the control plane says so.
        var partyId = await PartyIdAsync(owner, album);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Parties.Where(p => p.Id == partyId).ExecuteUpdateAsync(
                u => u.SetProperty(p => p.GuestAccessExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1)));
            var link = await db.PartyAlbumLinks.AsNoTracking().SingleAsync(l => l.AlbumId == album);
            Assert.True(link.Enabled);
            Assert.Null(link.RevokedAt);
            Assert.True(link.ExpiresAt is null || link.ExpiresAt > DateTime.UtcNow);
        }
        Assert.Equal("unavailable", await PresentationAsync(cookie));

        // …and the native read of the album closes with it.
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(items, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(messages, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGet(thumbnail, cookie)).StatusCode);
        // The general list never showed it and still does not.
        Assert.DoesNotContain(album.ToString(),
            await (await TvGet("/api/tv/albums", cookie)).Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_display_grant_states_its_lifetime_as_a_server_measured_duration()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        await AssignAsync(owner, await SingleSessionIdAsync(ownerId),
            await PartyAlbumAsync(owner, "Festa", game: true));

        var grant = await MintAsync(cookie);
        // The shell schedules its renewal from the DURATION, so a television
        // whose clock is wrong neither renews in a loop nor lets the grant lapse.
        Assert.Equal((int)TimeSpan.FromHours(4).TotalSeconds, grant.ExpiresInSeconds);
        Assert.InRange(grant.ExpiresAt, DateTime.UtcNow.AddMinutes(239), DateTime.UtcNow.AddMinutes(241));
    }

    [Fact]
    public async Task Two_televisions_on_one_party_are_two_independent_displays()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookieA = await PairTvAsync(owner);
        var tvA = await SingleSessionIdAsync(ownerId);
        var cookieB = await PairTvAsync(owner);
        var tvB = await OtherSessionIdAsync(ownerId, tvA);
        var album = await PartyAlbumAsync(owner, "Festa", game: true);
        await AssignAsync(owner, tvA, album);
        await AssignAsync(owner, tvB, album);

        // Same party, same presentation; each screen has its own identity.
        var a = await TvAssignmentAsync(cookieA);
        var b = await TvAssignmentAsync(cookieB);
        Assert.Equal("game", a.GetProperty("presentation").GetString());
        Assert.Equal("game", b.GetProperty("presentation").GetString());
        Assert.NotEqual(a.GetProperty("assignmentKey").GetString(), b.GetProperty("assignmentKey").GetString());
        var keyB = b.GetProperty("assignmentKey").GetString();

        // Two grants, one per device, both live, both reading the same game.
        var grantA = await MintAsync(cookieA);
        var grantB = await MintAsync(cookieB);
        Assert.NotEqual(grantA.Token, grantB.Token);
        var snapA = await DisplayJsonAsync("/api/party-display/game", grantA.Token);
        var snapB = await DisplayJsonAsync("/api/party-display/game", grantB.Token);
        Assert.Equal(snapA.GetProperty("phase").GetString(), snapB.GetProperty("phase").GetString());
        Assert.Equal(snapA.GetProperty("version").GetInt32(), snapB.GetProperty("version").GetInt32());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var live = await db.PartyDisplayGrants.AsNoTracking().Where(g => g.RevokedAt == null).ToListAsync();
            Assert.Equal(2, live.Count);
            Assert.Equal([tvA, tvB], live.Select(g => g.TvSessionId).OrderBy(id => id == tvB).ToList());
            // Two screens are not two guests.
            Assert.Empty(await db.PartyParticipants.ToListAsync());
        }

        // Renewing A does not touch B.
        var renewedA = await MintAsync(cookieA);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync("/api/party-display/game", grantA.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync("/api/party-display/game", renewedA.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync("/api/party-display/game", grantB.Token)).StatusCode);

        // FINISHED hands BOTH back to the slideshow; restart takes BOTH again.
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        version = (await CommandAsync(owner, album, "finish", version)).GetProperty("version").GetInt32();
        Assert.Equal("game", await PresentationAsync(cookieA));
        Assert.Equal("game", await PresentationAsync(cookieB));
        await AgeFinishedAtAsync(album, TimeSpan.FromMinutes(1));
        Assert.Equal("slideshow", await PresentationAsync(cookieA));
        Assert.Equal("slideshow", await PresentationAsync(cookieB));
        // Both screens' grants stop with the game, and neither can mint another.
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync("/api/party-display/game", renewedA.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync("/api/party-display/game", grantB.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookieA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookieB)).StatusCode);
        await CommandAsync(owner, album, "restart_game", version);
        Assert.Equal("game", await PresentationAsync(cookieA));
        Assert.Equal("game", await PresentationAsync(cookieB));
        // The takeover mints afresh on each screen, as the shells do.
        var liveA = await MintAsync(cookieA);
        var liveB = await MintAsync(cookieB);

        // A moving to another party leaves B exactly where it was.
        await AssignAsync(owner, tvA, await PartyAlbumAsync(owner, "Altro", game: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisplayAsync("/api/party-display/game", liveA.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync("/api/party-display/game", liveB.Token)).StatusCode);
        Assert.Equal(keyB, (await TvAssignmentAsync(cookieB)).GetProperty("assignmentKey").GetString());

        // Unpairing A leaves B exactly where it was.
        (await owner.DeleteAsync($"/api/tv-devices/{tvA}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await TvGet("/api/tv/session", cookieA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DisplayAsync("/api/party-display/game", liveB.Token)).StatusCode);
        Assert.Equal("game", await PresentationAsync(cookieB));
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Grant(string Token, DateTime ExpiresAt, int ExpiresInSeconds);

    private async Task<Grant> MintAsync(string cookie)
    {
        var response = await RawMintAsync(cookie);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Grant(body.GetProperty("grant").GetString()!,
            body.GetProperty("expiresAt").GetDateTime(),
            body.GetProperty("expiresInSeconds").GetInt32());
    }

    private Task<HttpResponseMessage> RawMintAsync(string setCookie) =>
        TvGet("/api/tv/party-display/grant", setCookie, post: true);

    private Task<HttpResponseMessage> DisplayAsync(string url, string grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(PartyDisplayService.GrantHeader, grant);
        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<JsonElement> DisplayJsonAsync(string url, string grant)
    {
        var response = await DisplayAsync(url, grant);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var started = (await (await tvClient.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret,
            personalCode = "URDLSUDLR", personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    /// A party on its own album, LIVE by default: the game capability folds in
    /// the party's phase, so only a live party can take a television with a game.
    private static async Task<Guid> PartyAlbumAsync(HttpClient owner, string name, bool game, bool live = true)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        if (live) await TransitionAsync(owner, await PartyIdAsync(owner, album), "start-live");
        if (game)
        {
            await SetGameAsync(owner, album, enabled: true);
            await AddChallengesAsync(owner, album);
        }
        return album;
    }

    private static async Task<Guid> PartyIdAsync(HttpClient owner, Guid album) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/party-settings"))
            .GetProperty("partyId").GetGuid();

    private static async Task TransitionAsync(HttpClient owner, Guid partyId, string action)
    {
        var party = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/{action}",
            new { version = party.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
    }

    private static async Task SetGameAsync(HttpClient owner, Guid album, bool enabled) =>
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = enabled, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

    private static async Task AddChallengesAsync(HttpClient owner, Guid album)
    {
        foreach (var title in new[] { "Uno", "Due" })
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
            })).EnsureSuccessStatusCode();
    }

    private static async Task AssignAsync(HttpClient owner, Guid sessionId, Guid? albumId) =>
        (await owner.PatchAsJsonAsync($"/api/tv-devices/{sessionId}/assignment",
            albumId is null
                ? new { kind = "general", albumId = (Guid?)null }
                : new { kind = "party", albumId })).EnsureSuccessStatusCode();

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> OwnerGameAsync(HttpClient owner, Guid album) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/party-game");

    // Moves the finished game's timestamp into the past: the only way to step
    // over the closing card's dwell without waiting for it, and it touches
    // nothing the presentation does not already read.
    private async Task AgeFinishedAtAsync(Guid album, TimeSpan by)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finishedAt = DateTime.UtcNow - by;
        var updated = await db.PartyGameSessions
            .Where(s => s.AlbumId == album && s.Status == PartyGameStatuses.Finished)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FinishedAt, (DateTime?)finishedAt));
        Assert.Equal(1, updated);
    }

    private async Task<Guid> ActiveLinkIdAsync(Guid album)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyAlbumLinks
            .AsNoTracking().SingleAsync(l => l.AlbumId == album && l.RevokedAt == null)).Id;
    }

    private async Task<Guid> SingleSessionIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.OwnerUserId == ownerUserId)).Id;
    }

    private async Task<Guid> OtherSessionIdAsync(Guid ownerUserId, Guid known)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.OwnerUserId == ownerUserId && x.Id != known)).Id;
    }

    private async Task<string?> PresentationAsync(string cookie) =>
        (await TvAssignmentAsync(cookie)).GetProperty("presentation").GetString();

    private async Task<JsonElement> TvAssignmentAsync(string cookie) =>
        (await TvJsonAsync("/api/tv/session", cookie)).GetProperty("assignment");

    private async Task<JsonElement> TvJsonAsync(string url, string cookie)
    {
        var response = await TvGet(url, cookie);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> TvGet(string url, string setCookie, bool post = false)
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

    // Distinct bytes per name: storage is content-addressed, and two identical
    // PNGs would deduplicate to one blob.
    private static async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var upload = await owner.PostAsync("/api/files", multipart);
        upload.EnsureSuccessStatusCode();
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }
}

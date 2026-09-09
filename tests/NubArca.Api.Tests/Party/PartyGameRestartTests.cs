using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Playing the same party again.
///
/// A restart is the only owner command that DESTROYS, so these tests are as much
/// about what survives it as about what it removes. The dividing line is the
/// party link: the match belongs to the game, and everything else — the token on
/// the QR code, the guests holding cookies it issued, their photographs,
/// greetings, prints and quotas, and the deck the host prepared — belongs to the
/// party and must come through untouched.
///
/// The second thing under test is the VERSION. A restart that reset it to zero
/// would resurrect every stale command written during the game that just ended:
/// a second tab still holding version 27 would find 27 quotable again and move a
/// party it is no longer looking at. So the number only ever goes up, and a
/// command from the previous game stays refused for ever.
/// </summary>
public sealed class PartyGameRestartTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameRestartTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_restart_empties_the_match_and_leaves_a_clean_lobby_at_the_next_version()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var finished = await PlayToTheEndAsync(owner, album, token, guest);
        var finishedVersion = finished.GetProperty("version").GetInt32();
        Assert.Equal("finished", finished.GetProperty("status").GetString());

        var sessionId = finished.GetProperty("sessionId").GetGuid();
        var lobby = await CommandAsync(owner, album, "restart_game", finishedVersion);

        // The lobby a host would recognise: nothing running, nothing played, and
        // the whole deck waiting again.
        Assert.Equal("lobby", lobby.GetProperty("status").GetString());
        Assert.Equal("lobby", lobby.GetProperty("phase").GetString());
        Assert.Equal(0, lobby.GetProperty("roundNumber").GetInt32());
        Assert.Equal(0, lobby.GetProperty("playedRounds").GetInt32());
        Assert.Equal(2, lobby.GetProperty("totalChallenges").GetInt32());
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("currentChallenge").ValueKind);
        Assert.Equal("Canta", lobby.GetProperty("nextChallenge").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("finishedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("voting").ValueKind);
        Assert.Equal(["start", "finish"], Commands(lobby));

        // THE VERSION WENT UP, NOT BACK. It is the same session row, one
        // command further on.
        Assert.Equal(finishedVersion + 1, lobby.GetProperty("version").GetInt32());
        Assert.Equal(sessionId, lobby.GetProperty("sessionId").GetGuid());

        // And the runtime rows of the finished match are gone rather than
        // resolved: a completed round left behind would be an activity the new
        // game could never play.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyGameRounds.ToListAsync());
        Assert.Empty(await db.PartyGameVotes.ToListAsync());
        var session = await db.PartyGameSessions.SingleAsync();
        Assert.Equal(sessionId, session.Id);
        Assert.Null(session.CurrentRoundId);
        Assert.Equal(0, session.CurrentRoundNumber);
    }

    [Fact]
    public async Task A_restarted_party_plays_again_on_the_same_link_with_the_same_guests()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var finished = await PlayToTheEndAsync(owner, album, token, guest);
        var participantsBefore = await ParticipantIdsAsync();

        var version = (await CommandAsync(owner, album, "restart_game",
            finished.GetProperty("version").GetInt32())).GetProperty("version").GetInt32();

        // THE SAME URL. Not a new link, not a new token, not a new QR code on
        // the table — the host never leaves the control room.
        Assert.Equal(token, await ViewTokenAsync(owner, album));

        // THE SAME GUESTS. The cookie this browser was issued for the previous
        // game still resolves, so it votes without joining again.
        Assert.Equal(participantsBefore, await ParticipantIdsAsync());

        version = (await CommandAsync(owner, album, "start", version)).GetProperty("version").GetInt32();
        var replayed = await SnapshotAsync(guest, token);
        // The deck starts over from the top: the first activity is playable
        // again, and last game's answer is not carried into it.
        Assert.Equal("challenge_reveal", replayed.GetProperty("phase").GetString());
        Assert.Equal("Canta", replayed.GetProperty("challenge").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, replayed.GetProperty("myVote").ValueKind);

        foreach (var command in new[] { "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var round = (await SnapshotAsync(guest, token)).GetProperty("roundId").GetGuid();
        var voted = await VoteAsync(guest, token, round, "yes");
        Assert.Equal("yes", voted.GetProperty("myVote").GetString());
        Assert.Equal(1, voted.GetProperty("voting").GetProperty("received").GetInt32());

        // One vote in the new game, and none of the old ones came back with it.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vote = await db.PartyGameVotes.SingleAsync();
        Assert.Equal(round, vote.PartyGameRoundId);
    }

    [Fact]
    public async Task A_command_from_the_finished_game_is_still_stale_after_the_restart()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var finished = await PlayToTheEndAsync(owner, album);
        var spent = finished.GetProperty("version").GetInt32();

        await CommandAsync(owner, album, "restart_game", spent);

        // A second tab still holding the finished game's version. Every command
        // it could send quotes a number the server has spent — including the
        // restart it may have queued itself, which must not run twice.
        foreach (var command in new[] { "start", "finish", "restart_game" })
        {
            var stale = await RawCommandAsync(owner, album, command, spent);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("version_conflict", await CodeAsync(stale));
        }

        // The lobby the restart created is untouched by any of it.
        var current = await OwnerAsync(owner, album);
        Assert.Equal("lobby", current.GetProperty("phase").GetString());
        Assert.Equal(spent + 1, current.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_restart_is_refused_from_every_phase_but_finished()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);

        // The lobby, before there is a session row at all.
        var cold = await RawCommandAsync(owner, album, "restart_game", 0);
        Assert.Equal(HttpStatusCode.Conflict, cold.StatusCode);
        Assert.Equal("illegal_transition", await CodeAsync(cold));

        var version = 0;
        foreach (var command in new[]
            { "start", "start_challenge", "open_voting", "close_voting", "reveal_result" })
        {
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
            var refused = await RawCommandAsync(owner, album, "restart_game", version);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("illegal_transition", await CodeAsync(refused));
        }

        // A refusal carries the truth, and the game did not move.
        var mid = await OwnerAsync(owner, album);
        Assert.Equal("result", mid.GetProperty("phase").GetString());
        Assert.Equal(version, mid.GetProperty("version").GetInt32());
        Assert.Equal(1, await RoundCountAsync());
    }

    [Fact]
    public async Task A_restart_quoting_a_stale_version_is_refused_and_destroys_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);
        var finished = await PlayToTheEndAsync(owner, album, token, guest);
        var current = finished.GetProperty("version").GetInt32();

        var stale = await RawCommandAsync(owner, album, "restart_game", current - 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("code").GetString());

        // The refusal hands back the state it was measured against, and that
        // state is still the finished game — nothing was deleted on the way to
        // saying no.
        Assert.Equal("finished", body.GetProperty("snapshot").GetProperty("phase").GetString());
        Assert.Equal(current, body.GetProperty("snapshot").GetProperty("version").GetInt32());
        Assert.Equal(1, await RoundCountAsync());
        using var scope = _factory.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_restart_keeps_everything_that_belongs_to_the_party_rather_than_to_the_game()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var uploadToken = await UploadTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);
        (await guest.PostAsJsonAsync($"/api/party/{uploadToken}/messages",
            new { displayName = "Ada", text = "Auguri!" })).EnsureSuccessStatusCode();

        // A television has been watching. Restarting the game is not a reason to
        // tell the control room the screen went away: the heartbeat lives on the
        // link because a screen watches before there is a game to watch.
        (await _factory.CreateClient().GetAsync($"/api/party/{token}/game?display=1"))
            .EnsureSuccessStatusCode();

        var before = await PartySurfaceAsync();
        var finished = await PlayToTheEndAsync(owner, album, token, guest);
        await CommandAsync(owner, album, "restart_game", finished.GetProperty("version").GetInt32());
        var after = await PartySurfaceAsync();

        Assert.Equal(before, after);

        // And the same thing said through the product's own surfaces: the deck,
        // the greetings feed and the guest's own identity are all where the host
        // left them.
        Assert.Equal(2, (await OwnerAsync(owner, album)).GetProperty("totalChallenges").GetInt32());
        var messages = await (await owner.GetAsync($"/api/albums/{album}/party-messages"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Auguri!", messages.GetProperty("items").EnumerateArray()
            .Single().GetProperty("text").GetString());
        Assert.Equal(1, (await OwnerAsync(owner, album)).GetProperty("guestsPresent").GetInt32());
    }

    [Fact]
    public async Task Only_the_owner_of_an_enabled_game_can_restart_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var finished = await PlayToTheEndAsync(owner, album);
        var version = finished.GetProperty("version").GetInt32();

        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await RawCommandAsync(stranger, album, "restart_game", version)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RawCommandAsync(_factory.CreateClient(), album, "restart_game", version)).StatusCode);

        // The game switch is re-read on arrival, so turning it off closes this
        // door too rather than leaving one command behind that still works.
        await SetGameEnabledAsync(owner, album, enabled: false);
        var off = await RawCommandAsync(owner, album, "restart_game", version);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("game_disabled", await CodeAsync(off));

        await SetGameEnabledAsync(owner, album, enabled: true);
        Assert.Equal("lobby",
            (await CommandAsync(owner, album, "restart_game", version)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_restart_is_audited_with_the_rounds_it_discarded()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var finished = await PlayToTheEndAsync(owner, album);
        await CommandAsync(owner, album, "restart_game", finished.GetProperty("version").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.AuditLogs
            .Where(x => x.Action == AuditActions.PartyGameRestart && x.UserId == ownerId)
            .SingleAsync();
        Assert.Equal(album, entry.EntityId);
        // The count is the one taken BEFORE the command, because afterwards
        // there is nothing left to count.
        Assert.Contains("\"rounds\":2", entry.MetadataJson);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// Everything a restart must not touch, read straight from the database as
    /// one comparable value: the party link and its token, the participants and
    /// every counter that decides what they may still do, their greetings, and
    /// the activities the host prepared.
    /// </summary>
    private async Task<string> PartySurfaceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PartyAlbumLinks.AsNoTracking().SingleAsync();
        var participants = await db.PartyParticipants.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.PartyAlbumLinkId, x.TokenHash, x.AcceptedPhotoCount, x.AcceptedVideoCount,
                x.ChallengeVoteCount, x.AcceptedPhotoPrintCount, x.AcceptedStripPrintCount,
                x.SubmittedMessageCount, x.RetiredAt,
            }).ToListAsync();
        var messages = await db.PartyMessages.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.PartyAlbumLinkId, x.Body, x.Status, x.DisplayName })
            .ToListAsync();
        var challenges = await db.PartyChallenges.AsNoTracking().OrderBy(x => x.SortOrder)
            .Select(x => new { x.Id, x.Title, x.Body, x.IsEnabled, x.SortOrder }).ToListAsync();
        var uploads = await db.PartyUploadItems.AsNoTracking()
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.AlbumId, x.Status }).ToListAsync();

        // The host's printing state: the budgets and what has already been spent
        // against them, plus every request that has been made. A restart that
        // refunded a print — or forgot one — would be giving away consumables.
        var printProfiles = await db.PartyPrintProfiles.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.PartyAlbumId, x.Enabled, x.PhotoEnabled, x.PhotoMaxPrints,
                x.PhotoAcceptedCount, x.PhotoPrintsPerGuest, x.StripEnabled,
            }).ToListAsync();
        var printRequests = await db.PartyPrintRequests.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.PartyAlbumId, x.Product, x.PrintJobId }).ToListAsync();

        // The OLDER interval-driven challenge system, which shares the album and
        // the link with the game and is a different feature entirely. Restarting
        // the hosted game must not touch a single row of it — the two session
        // types were deliberately kept apart, and this is where that would break.
        var legacyVotes = await db.PartyChallengeVotes.AsNoTracking().CountAsync();
        var legacySessions = await db.PartyChallengeSessions.AsNoTracking().CountAsync();
        var legacyCompletions = await db.PartyChallengeCompletions.AsNoTracking().CountAsync();

        return JsonSerializer.Serialize(new
        {
            Link = new
            {
                link.Id, link.TokenHash, link.UploadTokenHash, link.PrintTokenHash,
                link.Enabled, link.GameEnabled, link.RevokedAt,
                // The display belongs to the PARTY, not to one run of the game.
                link.LastDisplaySeenAt,
                link.MaxPhotoUploadsPerParticipant, link.MaxMessagesPerParticipant,
            },
            participants, messages, challenges, uploads,
            printProfiles, printRequests,
            legacyVotes, legacySessions, legacyCompletions,
        });
    }

    private async Task<List<Guid>> ParticipantIdsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyParticipants.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
    }

    private async Task<int> RoundCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameRounds.CountAsync();
    }

    /// <summary>
    /// Run the deck to its natural end, optionally letting one guest vote on the
    /// first activity so the restart has real votes to discard.
    /// </summary>
    private static async Task<JsonElement> PlayToTheEndAsync(
        HttpClient owner, Guid album, string? token = null, HttpClient? guest = null)
    {
        var snapshot = await OwnerAsync(owner, album);
        var version = snapshot.GetProperty("version").GetInt32();
        while (snapshot.GetProperty("status").GetString() != "finished")
        {
            var command = Commands(snapshot)[0];
            snapshot = await CommandAsync(owner, album, command, version);
            version = snapshot.GetProperty("version").GetInt32();

            if (snapshot.GetProperty("phase").GetString() == "voting_open"
                && token is not null && guest is not null)
            {
                var round = (await SnapshotAsync(guest, token)).GetProperty("roundId").GetGuid();
                await VoteAsync(guest, token, round, "yes");
            }
        }
        return snapshot;
    }

    private async Task<Guid> SetUpAsync(HttpClient owner, string[] titles)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings",
            new { enabled = true, uploadEnabled = true })).EnsureSuccessStatusCode();
        await SetGameEnabledAsync(owner, album, enabled: true);
        foreach (var title in titles)
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
            })).EnsureSuccessStatusCode();
        return album;
    }

    private static async Task SetGameEnabledAsync(HttpClient owner, Guid album, bool enabled) =>
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = enabled, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album) =>
        (await SettingsAsync(owner, album)).GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static async Task<string> UploadTokenAsync(HttpClient owner, Guid album)
    {
        var url = (await SettingsAsync(owner, album)).GetProperty("uploadUrl").GetString()!;
        return url["/party/".Length..url.LastIndexOf('/')];
    }

    private static async Task<JsonElement> SettingsAsync(HttpClient owner, Guid album) =>
        await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> OwnerAsync(HttpClient owner, Guid album)
    {
        var response = await owner.GetAsync($"/api/albums/{album}/party-game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RawCommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion) =>
        owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await RawCommandAsync(owner, album, command, expectedVersion);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SnapshotAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/party/{token}/game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> JoinAsync(HttpClient guest, string token)
    {
        var response = await guest.PostAsync($"/api/party/{token}/game/join", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> VoteAsync(
        HttpClient guest, string token, Guid roundId, string value)
    {
        var response = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId, value });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static string[] Commands(JsonElement snapshot) =>
        snapshot.GetProperty("availableCommands").EnumerateArray().Select(x => x.GetString()!).ToArray();
}

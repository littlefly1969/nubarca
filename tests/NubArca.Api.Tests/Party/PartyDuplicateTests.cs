using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Duplicating a party copies the DECISIONS and none of the history.
///
/// <para>The line is the whole feature. Titles, windows, guest slots, the deck,
/// the slideshow timings, the quotas, the approval modes, the game switches and
/// the print budgets are what a host decided; participants, preferences, votes,
/// rounds, uploads, greetings, prints, face searches, televisions, display
/// grants, heartbeats and tokens are what an evening produced. The first travels
/// and the second never does — last year's guests did not attend this year's
/// party, and last year's QR must open nothing.</para>
///
/// <para>The other half is that the copy is INDEPENDENT. Media is shared through
/// ordinary album membership — new rows, the same files, the same blobs, no byte
/// written twice — so editing either party leaves the other exactly as it was.
/// That is asserted from both directions, because "it looked the same right
/// after copying" is not the same claim.</para>
/// </summary>
public sealed class PartyDuplicateTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyDuplicateTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_copy_carries_the_configuration_and_none_of_the_evening()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var source = await RunAPartyAsync(owner);

        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{source.PartyId}/duplicate", new { title = (string?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = await response.Content.ReadFromJsonAsync<JsonElement>();

        var clonePartyId = clone.GetProperty("id").GetGuid();
        var cloneAlbumId = clone.GetProperty("mediaSources")[0].GetProperty("albumId").GetGuid();
        Assert.NotEqual(source.PartyId, clonePartyId);
        Assert.NotEqual(source.AlbumId, cloneAlbumId);

        // Its capability travelled, and an active capability IS a published
        // party — the state every other path produces, and the one the owner
        // surface's "the guest link is live" relies on.
        Assert.Equal(PartyStatuses.Published, clone.GetProperty("status").GetString());
        Assert.Equal(1, clone.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, clone.GetProperty("liveStartedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, clone.GetProperty("liveEndedAt").ValueKind);
        Assert.Equal("Festa di Anna", clone.GetProperty("title").GetString());
        Assert.Equal("Con i colleghi", clone.GetProperty("description").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // --- WHAT TRAVELS -------------------------------------------------
        // The same photographs, through new membership rows: the FileItem ids
        // match, so nothing was re-uploaded and no blob was duplicated.
        var sourceFiles = await db.AlbumItems.Where(x => x.AlbumId == source.AlbumId)
            .Select(x => x.FileItemId).OrderBy(x => x).ToListAsync();
        var cloneFiles = await db.AlbumItems.Where(x => x.AlbumId == cloneAlbumId)
            .Select(x => x.FileItemId).OrderBy(x => x).ToListAsync();
        Assert.NotEmpty(sourceFiles);
        Assert.Equal(sourceFiles, cloneFiles);
        Assert.Equal(sourceFiles.Count, await db.FileItems.CountAsync(x => sourceFiles.Contains(x.Id)));

        // The deck, with new ids and every rule — including `kind`, which the
        // composer no longer asks for and the row still carries.
        var sourceDeck = await db.PartyChallenges.Where(x => x.AlbumId == source.AlbumId)
            .OrderBy(x => x.SortOrder).ToListAsync();
        var cloneDeck = await db.PartyChallenges.Where(x => x.AlbumId == cloneAlbumId)
            .OrderBy(x => x.SortOrder).ToListAsync();
        Assert.Equal(2, cloneDeck.Count);
        Assert.Empty(cloneDeck.Select(x => x.Id).Intersect(sourceDeck.Select(x => x.Id)));
        Assert.Equal(
            sourceDeck.Select(x => (x.Title, x.Body, x.Kind, x.IsEnabled, x.SortOrder,
                x.DurationSeconds, x.VotingMode, x.VoteQuestion, x.MediaFileItemId)),
            cloneDeck.Select(x => (x.Title, x.Body, x.Kind, x.IsEnabled, x.SortOrder,
                x.DurationSeconds, x.VotingMode, x.VoteQuestion, x.MediaFileItemId)));

        // The guest-facing slots, with their photograph reference and how it is
        // presented.
        var slot = await db.PartyGuestContents.SingleAsync(x => x.PartyId == clonePartyId);
        Assert.Equal(PartyGuestContentKinds.Menu, slot.Kind);
        Assert.True(slot.Enabled);
        Assert.Contains("Tagliatelle", slot.ContentJson);

        // The capability and every setting that lives on it.
        var sourceLink = await db.PartyAlbumLinks.SingleAsync(x => x.PartyId == source.PartyId);
        var cloneLink = await db.PartyAlbumLinks.SingleAsync(x => x.PartyId == clonePartyId);
        Assert.Equal(sourceLink.GameEnabled, cloneLink.GameEnabled);
        Assert.Equal(sourceLink.PriorityVotingEnabled, cloneLink.PriorityVotingEnabled);
        Assert.Equal(sourceLink.VotesPerGuest, cloneLink.VotesPerGuest);
        Assert.Equal(sourceLink.PhotoSlideSeconds, cloneLink.PhotoSlideSeconds);
        Assert.Equal(sourceLink.MaxPhotoUploadsPerParticipant, cloneLink.MaxPhotoUploadsPerParticipant);
        Assert.Equal(sourceLink.RequireUploadApproval, cloneLink.RequireUploadApproval);
        Assert.Equal(sourceLink.RequireMessageApproval, cloneLink.RequireMessageApproval);

        // --- WHAT DOES NOT ------------------------------------------------
        // New tokens, and a heartbeat that has never beaten.
        Assert.NotEqual(sourceLink.Id, cloneLink.Id);
        Assert.NotEqual(sourceLink.TokenHash, cloneLink.TokenHash);
        Assert.NotEqual(sourceLink.UploadTokenHash, cloneLink.UploadTokenHash);
        Assert.Null(cloneLink.LastDisplaySeenAt);
        Assert.NotNull(sourceLink.LastDisplaySeenAt);

        // Not one row of what happened.
        Assert.Empty(await db.PartyParticipants.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        Assert.Empty(await db.PartyChallengeVotes.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        Assert.Empty(await db.PartyGameSessions.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        Assert.Empty(await db.PartyMessages.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        Assert.Empty(await db.PartyUploadItems.Where(x => x.AlbumId == cloneAlbumId).ToListAsync());
        Assert.Empty(await db.PartyChallengeSessions.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        Assert.Empty(await db.PartyDisplayGrants.Where(x => x.PartyAlbumLinkId == cloneLink.Id).ToListAsync());
        // ...and the original still has every one of them.
        Assert.NotEmpty(await db.PartyParticipants.Where(x => x.PartyAlbumLinkId == sourceLink.Id).ToListAsync());
        Assert.NotEmpty(await db.PartyChallengeVotes.Where(x => x.PartyAlbumLinkId == sourceLink.Id).ToListAsync());
        Assert.NotEmpty(await db.PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task The_clones_guest_link_opens_the_clone_and_survives_the_original_being_deleted()
    {
        // What a host did in production: duplicate, delete the original at
        // once, then open the new guest link. A DRAFT clone answered that link
        // with "not found" — which read as a link into the party just deleted.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var source = await RunAPartyAsync(owner);

        // The host's alignment is a decision like any other, so it travels too.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PartyGuestContents.Where(x => x.PartyId == source.PartyId)
                .ExecuteUpdateAsync(u => u.SetProperty(
                    x => x.TextAlign, PartyGuestContentTextAligns.Center));
        }

        var clone = await (await owner.PostAsJsonAsync(
                $"/api/parties/{source.PartyId}/duplicate", new { title = (string?)null }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var cloneAlbumId = clone.GetProperty("mediaSources")[0].GetProperty("albumId").GetGuid();
        var settings = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{cloneAlbumId}/party-settings");
        Assert.True(settings.GetProperty("partyMode").GetBoolean());
        var cloneToken = settings.GetProperty("partyUrl").GetString()!["/party/".Length..];
        Assert.NotEqual(source.Token, cloneToken);

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{cloneToken}")).StatusCode);

        var original = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{source.PartyId}");
        (await owner.DeleteAsync(
                $"/api/parties/{source.PartyId}?version={original.GetProperty("version").GetInt32()}"))
            .EnsureSuccessStatusCode();

        // The original's link went with it; the copy's is untouched.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{source.Token}")).StatusCode);
        var opened = await guest.GetAsync($"/api/party/{cloneToken}");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var context = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Festa di Anna", context.GetProperty("title").GetString());
        Assert.Equal("center", context.GetProperty("content")[0].GetProperty("textAlign").GetString());
    }

    [Fact]
    public async Task The_clone_and_the_original_diverge_in_both_directions()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var source = await RunAPartyAsync(owner);
        var clone = await (await owner.PostAsJsonAsync(
                $"/api/parties/{source.PartyId}/duplicate", new { title = "Festa di Bruno" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var clonePartyId = clone.GetProperty("id").GetGuid();
        var cloneAlbumId = clone.GetProperty("mediaSources")[0].GetProperty("albumId").GetGuid();
        Assert.Equal("Festa di Bruno", clone.GetProperty("title").GetString());

        // Edit the COPY: a third activity, and one of the originals removed.
        (await owner.PostAsJsonAsync($"/api/albums/{cloneAlbumId}/party-challenges", new
        {
            title = "Karaoke", body = "Canta", mediaFileItemId = (Guid?)null, isEnabled = true,
        })).EnsureSuccessStatusCode();

        // Edit the ORIGINAL: rename one of its activities.
        Guid sourceChallengeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sourceChallengeId = await db.PartyChallenges
                .Where(x => x.AlbumId == source.AlbumId).OrderBy(x => x.SortOrder)
                .Select(x => x.Id).FirstAsync();
        }
        (await owner.PutAsJsonAsync(
            $"/api/albums/{source.AlbumId}/party-challenges/{sourceChallengeId}", new
            {
                title = "Rinominata", body = "Descrizione",
                mediaFileItemId = (Guid?)null, isEnabled = true,
            })).EnsureSuccessStatusCode();

        using var verify = _factory.Services.CreateScope();
        var final = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var sourceTitles = await final.PartyChallenges.Where(x => x.AlbumId == source.AlbumId)
            .OrderBy(x => x.SortOrder).Select(x => x.Title).ToListAsync();
        var cloneTitles = await final.PartyChallenges.Where(x => x.AlbumId == cloneAlbumId)
            .OrderBy(x => x.SortOrder).Select(x => x.Title).ToListAsync();

        Assert.Equal(["Rinominata", "Brindisi"], sourceTitles);
        Assert.Equal(["Canta", "Brindisi", "Karaoke"], cloneTitles);

        // The two parties are still two parties, and the PHOTOGRAPHS are still
        // one set of files shared by two albums.
        Assert.NotEqual(source.PartyId, clonePartyId);
        var sourceFiles = await final.AlbumItems.Where(x => x.AlbumId == source.AlbumId)
            .Select(x => x.FileItemId).OrderBy(x => x).ToListAsync();
        var cloneFiles = await final.AlbumItems.Where(x => x.AlbumId == cloneAlbumId)
            .Select(x => x.FileItemId).OrderBy(x => x).ToListAsync();
        Assert.Equal(sourceFiles, cloneFiles);
    }

    [Fact]
    public async Task Another_owners_party_is_the_same_generic_404_as_an_unknown_one()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var party = await RunAPartyAsync(alice);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/duplicate", new { title = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(
            $"/api/parties/{Guid.NewGuid()}/duplicate", new { title = (string?)null })).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Parties.CountAsync());
    }

    // --- helpers -----------------------------------------------------------

    private sealed record SeededParty(Guid PartyId, Guid AlbumId, string Token);

    /// <summary>
    /// A party that has actually HAPPENED: photographs in its album, a deck, a
    /// guest slot, guests with preferences, a played round with a live vote, a
    /// greeting and a television that looked at it. Everything the copy must
    /// carry, beside everything it must not.
    /// </summary>
    private async Task<SeededParty> RunAPartyAsync(HttpClient owner)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = "Festa di Anna" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var settings = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var partyId = settings.GetProperty("partyId").GetGuid();
        var token = settings.GetProperty("partyUrl").GetString()!["/party/".Length..];

        // The party's own data, so the copy has something to carry.
        var party = await (await owner.GetAsync($"/api/parties/{partyId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await owner.PatchAsJsonAsync($"/api/parties/{partyId}", new
        {
            title = "Festa di Anna", description = "Con i colleghi",
            eventStartsAt = (DateTime?)null, guestAccessExpiresAt = (DateTime?)null,
            libraryAccessExpiresAt = (DateTime?)null,
            version = party.GetProperty("version").GetInt32(),
        })).EnsureSuccessStatusCode();

        // One guest-facing slot.
        (await owner.PutAsJsonAsync($"/api/parties/{partyId}/guest-content/menu", new
        {
            enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
            content = new
            {
                intro = "A tavola",
                sections = new[] { new { title = "Primi", items = new[] { "Tagliatelle" } } },
            },
            mediaFileItemId = (Guid?)null, version = 0,
        })).EnsureSuccessStatusCode();

        await PartyTestHost.StartAsync(
            owner, await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
                .Content.ReadFromJsonAsync<JsonElement>());

        // Settings that must travel, on values that are not the defaults.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-slideshow-settings", new
        {
            photoSlideSeconds = 12, maxVideoSlideSeconds = 45,
            maxPhotoUploadsPerParticipant = 5, maxVideoUploadsPerParticipant = 2,
            maxMessagesPerParticipant = 3,
        })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 2, maxChallengesPerSession = (int?)null, priorityVotingEnabled = true,
        })).EnsureSuccessStatusCode();

        foreach (var title in new[] { "Canta", "Brindisi" })
        {
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", mediaFileItemId = (Guid?)null, isEnabled = true,
                durationSeconds = 60, votingMode = "binary", voteQuestion = "Ce l'ha fatta?",
            })).EnsureSuccessStatusCode();
        }

        // A photograph in the album, so the membership has something to copy.
        await SeedAlbumMediaAsync(owner, album);

        // A guest, a preference, a greeting, a television, a played round.
        var guest = _factory.CreateClient();
        var joined = await (await guest.PostAsync($"/api/party/{token}/game/join", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var challenge = joined.GetProperty("preferences").GetProperty("items")[0]
            .GetProperty("id").GetGuid();
        (await guest.PostAsJsonAsync($"/api/party/{token}/game/preferences",
            new { challengeId = challenge, selected = true })).EnsureSuccessStatusCode();
        (await _factory.CreateClient().GetAsync($"/api/party/{token}/game?display=1"))
            .EnsureSuccessStatusCode();

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
        {
            version = (await (await owner.PostAsJsonAsync(
                    $"/api/albums/{album}/party-game/commands", new { command, expectedVersion = version }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
        }
        var round = (await (await guest.GetAsync($"/api/party/{token}/game"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roundId").GetGuid();
        (await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" })).EnsureSuccessStatusCode();

        return new SeededParty(partyId, album, token);
    }

    /// One real file in the album, through the ordinary owner upload — so the
    /// membership the copy has to reproduce is a membership of a real FileItem.
    private static async Task SeedAlbumMediaAsync(HttpClient owner, Guid album)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync(
            "/api/files", new MultipartFormDataContent { { part, "file", "memoria.png" } });
        upload.EnsureSuccessStatusCode();
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{album}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
    }
}

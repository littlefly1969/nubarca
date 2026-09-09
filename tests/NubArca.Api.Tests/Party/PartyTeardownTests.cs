using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Tearing a party down, and the one question that decides everything: which
/// photographs survive it.
///
/// <para>The evening is over and the host wants it out of their way. What they
/// must not lose is the album — so the guest media is FINALIZED against the
/// moderation decisions that were made while the party ran, and only then is
/// any party row deleted. Owner-added media was never a guest contribution and
/// no party ever governed it. A guest upload survives if and only if its final
/// status is <c>approved</c>, whether that was automatic or the host's own
/// decision; everything else goes through NubArca's ORDINARY FileItem deletion
/// lifecycle, into Trash, where the sweeper and the janitor reclaim it. Nothing
/// here touches a blob.</para>
///
/// <para>Afterwards the album is SELF-CONTAINED: what is visible in it is
/// decided the way it is for every other album, and no party provenance has to
/// be consulted to answer it.</para>
/// </summary>
public sealed class PartyTeardownTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyTeardownTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] EveryPartyPermission =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    [Fact]
    public async Task Exactly_The_Approved_Guest_Photographs_And_The_Owners_Own_Survive()
    {
        var party = await SeedRunningPartyAsync();

        // The owner's own photograph: no moderation row, never governed by the
        // party, and never at risk.
        var ownerPhoto = await AddOwnerPhotoAsync(party, "owner.png");

        // Four guest uploads, one per outcome the host could have reached.
        var approved = await SeedGuestUploadAsync(party, PartyUploadStatuses.Approved);
        var pending = await SeedGuestUploadAsync(party, PartyUploadStatuses.Pending);
        var hidden = await SeedGuestUploadAsync(party, PartyUploadStatuses.Hidden);
        var rejected = await SeedGuestUploadAsync(party, PartyUploadStatuses.Rejected);
        var removed = await SeedGuestUploadAsync(party, PartyUploadStatuses.RemovedFromAlbum);

        await TearDownAsync(party);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // IDENTITY, not counts. Which files survive is the whole question.
        var active = await db.FileItems
            .Where(f => f.OwnerUserId == party.OwnerId && f.DeletedAt == null)
            .Select(f => f.Id)
            .ToListAsync();
        Assert.Equal(
            new[] { ownerPhoto, approved }.OrderBy(id => id),
            active.OrderBy(id => id));

        // The rest went to Trash — the ordinary lifecycle, restorable, with the
        // blob untouched until the janitor's own grace window.
        foreach (var doomed in new[] { pending, hidden, rejected, removed })
        {
            var file = await db.FileItems.IgnoreQueryFilters()
                .SingleAsync(f => f.Id == doomed);
            Assert.NotNull(file.DeletedAt);
        }
        Assert.Empty(await db.PendingBlobPurges.ToListAsync());

        // What the album SHOWS is exactly the survivors — and it says so the way
        // every other album does, by the files being active. The trashed items
        // keep their membership, which is the ordinary lifecycle: restoring one
        // from Trash puts it back where it was.
        var visible = await db.AlbumItems
            .Where(ai => ai.AlbumId == party.AlbumId)
            .Join(db.FileItems.Where(f => f.DeletedAt == null),
                ai => ai.FileItemId, f => f.Id, (ai, f) => f.Id)
            .ToListAsync();
        Assert.Equal(
            new[] { ownerPhoto, approved }.OrderBy(id => id),
            visible.OrderBy(id => id));

        // And NOTHING has to consult a party to reach that answer: the
        // provenance rows are gone, which is what self-contained means.
        Assert.Empty(await db.PartyUploadItems.ToListAsync());
    }

    [Fact]
    public async Task The_Album_Survives_Self_Contained_And_Every_Party_Row_Is_Gone()
    {
        var party = await SeedRunningPartyAsync();
        await AddOwnerPhotoAsync(party, "owner.png");
        await SeedGuestUploadAsync(party, PartyUploadStatuses.Approved);
        await SeedGameAsync(party);
        await SeedMessageAsync(party);
        await SeedPrintProfileAsync(party);
        await SeedFaceSearchAsync(party);
        await WriteContentAsync(party, "info");

        await TearDownAsync(party);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The album is still there, and still the owner's.
        Assert.True(await db.Albums.AnyAsync(a => a.Id == party.AlbumId));

        // The party is not, and neither is anything that belonged to it —
        // including the provenance rows, which is what makes the album
        // self-contained: visibility is decided by the files being active.
        Assert.Empty(await db.Parties.ToListAsync());
        Assert.Empty(await db.PartyMediaSources.ToListAsync());
        Assert.Empty(await db.PartyAlbumLinks.ToListAsync());
        Assert.Empty(await db.PartyUploadItems.ToListAsync());
        Assert.Empty(await db.PartyParticipants.ToListAsync());
        Assert.Empty(await db.PartyMessages.ToListAsync());
        Assert.Empty(await db.PartyGameSessions.ToListAsync());
        Assert.Empty(await db.PartyGameRounds.ToListAsync());
        Assert.Empty(await db.PartyGameVotes.ToListAsync());
        Assert.Empty(await db.PartyChallenges.ToListAsync());
        Assert.Empty(await db.PartyChallengeSessions.ToListAsync());
        Assert.Empty(await db.PartyFaceSearchSessions.ToListAsync());
        Assert.Empty(await db.PartyGuestContents.ToListAsync());
        // The print BUDGETS go with the party, so a new evening on the same
        // album never inherits a previous one's spent sheets.
        Assert.Empty(await db.PartyPrintProfiles.ToListAsync());
    }

    [Fact]
    public async Task The_Guest_Surface_Is_Gone_And_The_Owner_Keeps_Their_Album()
    {
        var party = await SeedRunningPartyAsync();
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);

        await TearDownAsync(party);

        // The same generic unavailable an unknown token gets.
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await party.Owner.GetAsync($"/api/parties/{party.PartyId}")).StatusCode);
        // And the album opens exactly as any other album does.
        (await party.Owner.GetAsync($"/api/albums/{party.AlbumId}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_Teardown_Is_Version_Checked_And_Never_Another_Owners()
    {
        var party = await SeedRunningPartyAsync();
        var (_, stranger) = await _factory.CreatePermissionClientAsync(
            $"stranger-{Guid.NewGuid():N}@example.com", EveryPartyPermission);

        // Never 403: a stranger must not learn that this party exists.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.DeleteAsync($"/api/parties/{party.PartyId}?version=1")).StatusCode);

        var current = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        var stale = current.GetProperty("version").GetInt32() - 1;
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await party.Owner.DeleteAsync($"/api/parties/{party.PartyId}?version={stale}")).StatusCode);

        // Nothing was destroyed by either attempt.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.Parties.ToListAsync());
    }

    [Fact]
    public async Task A_Party_That_Never_Ran_Tears_Down_With_Nothing_To_Finalize()
    {
        // Created and abandoned: no album, no capability, no guests. Teardown
        // must be as ordinary as the party was.
        var (_, owner) = await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", EveryPartyPermission);
        var created = await (await owner.PostAsJsonAsync(
            "/api/parties", new { title = "Mai successa" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var partyId = created.GetProperty("id").GetGuid();

        var response = await owner.DeleteAsync($"/api/parties/{partyId}?version=1");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Parties.ToListAsync());
    }

    // --- helpers ------------------------------------------------------------

    private sealed record RunningParty(
        HttpClient Owner, Guid OwnerId, Guid PartyId, Guid AlbumId, Guid LinkId, string Token);

    private async Task<RunningParty> SeedRunningPartyAsync()
    {
        var (ownerId, owner) = await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", EveryPartyPermission);
        var albumId = (await (await owner.PostAsJsonAsync("/api/albums", new { name = "Festa" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var enable = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();
        var partyId = settings.GetProperty("partyId").GetGuid();
        await PartyTestHost.StartAsync(owner, partyId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var linkId = await db.PartyAlbumLinks.Where(l => l.PartyId == partyId)
            .Select(l => l.Id).SingleAsync();

        return new RunningParty(
            owner, ownerId, partyId, albumId, linkId,
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private static async Task<Guid> AddOwnerPhotoAsync(RunningParty party, string name)
    {
        var part = new ByteArrayContent(Metadata.ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var upload = await party.Owner.PostAsync("/api/files", multipart);
        upload.EnsureSuccessStatusCode();
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await party.Owner.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    // A guest upload is an owner file PLUS a moderation row — the row is what
    // makes it a guest contribution, and its status is the whole decision.
    private async Task<Guid> SeedGuestUploadAsync(RunningParty party, string status)
    {
        var fileId = await AddOwnerPhotoAsync(party, $"guest-{Guid.NewGuid():N}.png");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyUploadItems.Add(new PartyUploadItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = party.OwnerId,
            AlbumId = party.AlbumId,
            PartyAlbumLinkId = party.LinkId,
            FileItemId = fileId,
            Status = status,
            UploadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return fileId;
    }

    private async Task SeedGameAsync(RunningParty party)
    {
        (await party.Owner.PostAsJsonAsync($"/api/albums/{party.AlbumId}/party-challenges", new
        {
            title = "Un brindisi", body = "Agli sposi", kind = "dare", isEnabled = true,
        })).EnsureSuccessStatusCode();
        (await party.Owner.PatchAsJsonAsync($"/api/albums/{party.AlbumId}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 300,
            maxChallengeIntervalSeconds = 540, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        var lobby = await party.Owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{party.AlbumId}/party-game");
        (await party.Owner.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-game/commands",
            new { command = "start", expectedVersion = lobby.GetProperty("version").GetInt32() }))
            .EnsureSuccessStatusCode();
    }

    private async Task SeedMessageAsync(RunningParty party)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyMessages.Add(new PartyMessage
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = party.LinkId,
            AlbumId = party.AlbumId,
            OwnerUserId = party.OwnerId,
            DisplayName = "Anna",
            Body = "Auguri!",
            Status = PartyMessageStatuses.Visible,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPrintProfileAsync(RunningParty party)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyPrintProfiles.Add(new Domain.Print.PartyPrintProfile
        {
            Id = Guid.NewGuid(),
            PartyAlbumId = party.AlbumId,
            OwnerUserId = party.OwnerId,
            Enabled = true,
            PhotoEnabled = true,
            PhotoMaxPrints = 10,
            PhotoAcceptedCount = 4,
            PublicSequenceNext = 5,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedFaceSearchAsync(RunningParty party)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyFaceSearchSessions.Add(new PartyFaceSearchSession
        {
            Id = Guid.NewGuid(),
            OwnerUserId = party.OwnerId,
            AlbumId = party.AlbumId,
            PartyAlbumLinkId = party.LinkId,
            Status = "ready",
            ResultCount = 0,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await db.SaveChangesAsync();
    }

    private static async Task WriteContentAsync(RunningParty party, string kind) =>
        (await party.Owner.PutAsJsonAsync(
            $"/api/parties/{party.PartyId}/guest-content/{kind}",
            new
            {
                enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
                content = new { title = "Parcheggio", body = "In fondo alla via" }, version = 0,
            })).EnsureSuccessStatusCode();

    private static async Task TearDownAsync(RunningParty party)
    {
        var dto = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        var response = await party.Owner.DeleteAsync(
            $"/api/parties/{party.PartyId}?version={dto.GetProperty("version").GetInt32()}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}

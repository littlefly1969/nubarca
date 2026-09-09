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
/// P3: one QR, three surfaces.
///
/// <para>The same code carries a guest from the invitation, through the party,
/// to the memories — so what is pinned here is that the SERVER decides which of
/// them they are looking at. Hiding the gallery in a browser is not a rule; a
/// route that answers 404 is. Every capability outside its phase is refused the
/// same generic way an unknown token is, and none of those refusals lives in an
/// endpoint's own <c>if</c>.</para>
///
/// <para>Three things stay separate throughout: the STATUS is the phase, the
/// LINK is the technical capability, and the two windows are product decisions.
/// A status never revokes a token; a token is never invalid merely because the
/// party has not started or has finished.</para>
/// </summary>
public sealed class PartyGuestExperienceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyGuestExperienceTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] EveryPartyPermission =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    // --- The three surfaces -------------------------------------------------

    [Fact]
    public async Task The_Same_Token_Becomes_The_Invitation_The_Party_And_The_Memories()
    {
        var party = await SeedPartyAsync();
        var guest = _factory.CreateClient();

        var invitation = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal("before", invitation.GetProperty("phase").GetString());
        Assert.Equal("full", invitation.GetProperty("accessMode").GetString());
        // The PARTY names itself, not its album — they have been separate things
        // since the host could rename either without the other.
        Assert.Equal("Festa di Marta", invitation.GetProperty("title").GetString());

        await StartAsync(party);
        var live = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal("live", live.GetProperty("phase").GetString());

        await EndAsync(party);
        var after = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal("after", after.GetProperty("phase").GetString());

        // The token never rotated, and was never revoked. One QR, three answers.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    [Fact]
    public async Task An_Invitation_Is_Not_An_Empty_Album()
    {
        var party = await SeedPartyAsync();
        var guest = _factory.CreateClient();

        // Not merely hidden: the routes themselves refuse. A link the frontend
        // does not draw must never be a way in.
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}/items")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{party.PhotoId}/preview")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}/game")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/challenges")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.PostAsync($"/api/party/{party.UploadToken}/upload-session", null)).StatusCode);

        // And the context offers none of them, rather than offering a tile that
        // would fail: absence IS the answer.
        var invitation = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        var capabilities = invitation.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("contributionUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("gameUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("printUrl").ValueKind);
        Assert.False(capabilities.GetProperty("faceSearch").GetBoolean());
        Assert.Equal(JsonValueKind.Null, invitation.GetProperty("albumName").ValueKind);
    }

    [Fact]
    public async Task The_Invitations_Hero_Is_The_Cover_And_Only_The_Cover()
    {
        // §16 wants an invitation to look like one, so the album's chosen cover
        // survives Before. It is the one file id that resolves there — a
        // photograph on an invitation is still not a gallery.
        var party = await SeedPartyAsync();
        var second = await AddPhotoAsync(party.Owner, party.AlbumId, "second.png");
        await SetCoverAsync(party.AlbumId, party.PhotoId);

        var guest = _factory.CreateClient();
        var invitation = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal(JsonValueKind.String, invitation.GetProperty("coverUrl").ValueKind);

        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{party.PhotoId}/preview")).StatusCode);
        // Every other photograph stays behind the party.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{second}/preview")).StatusCode);
    }

    [Fact]
    public async Task A_Party_That_Is_Over_Runs_None_Of_Its_Live_Features()
    {
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await EndAsync(party);

        var guest = _factory.CreateClient();
        var after = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        var capabilities = after.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("contributionUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("gameUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("printUrl").ValueKind);
        Assert.False(capabilities.GetProperty("faceSearch").GetBoolean());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.PostAsync($"/api/party/{party.UploadToken}/upload-session", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}/game")).StatusCode);

        // But the MEMORIES are there — that is the whole point of afterwards.
        Assert.True(after.GetProperty("library").GetProperty("available").GetBoolean());
        Assert.Equal(
            HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.Token}/items")).StatusCode);
    }

    // --- The two windows ----------------------------------------------------

    [Fact]
    public async Task When_Guest_Access_Ends_The_Memories_Can_Outlive_It()
    {
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await EndAsync(party);

        // Guest access closed yesterday; the memories were given until next month.
        await SetWindowsAsync(party, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var guest = _factory.CreateClient();
        var context = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal("after", context.GetProperty("phase").GetString());
        Assert.Equal("library-only", context.GetProperty("accessMode").GetString());
        Assert.True(context.GetProperty("library").GetProperty("available").GetBoolean());
        Assert.Equal(
            JsonValueKind.String,
            context.GetProperty("library").GetProperty("accessEndsAt").ValueKind);

        // The QR still works, and what it opens has narrowed to the album.
        Assert.Equal(
            HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.Token}/items")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.PostAsync($"/api/party/{party.UploadToken}/upload-session", null)).StatusCode);
    }

    [Fact]
    public async Task An_Unset_Library_End_Is_Not_A_Second_Window()
    {
        // Null does not extend anything: the memories last exactly as long as
        // guest access does, which is what every party before the column meant.
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await EndAsync(party);
        await SetWindowsAsync(party, DateTime.UtcNow.AddDays(-1), null);

        var guest = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    [Fact]
    public async Task Memories_That_Close_First_Leave_The_Thank_You_Standing()
    {
        // The other ordering: guest access is still open, the library is not.
        // The greeting stays and the page must not offer a dead button.
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await EndAsync(party);
        await SetWindowsAsync(party, null, DateTime.UtcNow.AddDays(-1));

        var guest = _factory.CreateClient();
        var context = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");
        Assert.Equal("after", context.GetProperty("phase").GetString());
        Assert.Equal("full", context.GetProperty("accessMode").GetString());
        Assert.False(context.GetProperty("library").GetProperty("available").GetBoolean());
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}/items")).StatusCode);
    }

    [Fact]
    public async Task Both_Windows_Closed_Is_The_Same_Generic_Unavailable_As_An_Unknown_Token()
    {
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await EndAsync(party);
        await SetWindowsAsync(party, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1));

        var guest = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
        Assert.Equal(
            (await guest.GetAsync("/api/party/not-a-real-token")).StatusCode,
            (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    [Fact]
    public async Task Guest_Access_Ending_Before_The_Party_Is_Over_Closes_Everything()
    {
        // The library is an AFTER idea. A window that closes while the party is
        // still being held produces no library-only state — the memories do not
        // exist yet.
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await SetWindowsAsync(party, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    // --- Nothing outranks the capability or the permission -------------------

    [Fact]
    public async Task A_Revoked_Link_Still_Outranks_Every_Phase()
    {
        var party = await SeedPartyAsync();
        await StartAsync(party);
        (await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    [Fact]
    public async Task Losing_Party_Access_Closes_Every_Phase_Including_The_Memories()
    {
        var (roleKey, owner) = await NewHostWithRoleAsync();
        var party = await SeedPartyAsync(owner);
        await StartAsync(party);
        await EndAsync(party);

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);

        await _factory.SetRolePermissionsAsync(roleKey);

        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    // --- Typed guest content -------------------------------------------------

    [Fact]
    public async Task Content_Reaches_The_Guest_Only_Where_The_Host_Put_It()
    {
        var party = await SeedPartyAsync();

        await WriteContentAsync(party, "location", new
        {
            enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
            content = new { venueName = "Villa Aurora", address = "Via Roma 1", note = (string?)null },
            version = 0,
        });
        // Written but turned OFF: it must be absent everywhere, not an empty card.
        await WriteContentAsync(party, "dress-code", new
        {
            enabled = false, visibleBefore = true, visibleLive = true, visibleAfter = true,
            content = new { headline = "Summer elegant" },
            version = 0,
        });
        await WriteContentAsync(party, "thank-you", new
        {
            enabled = true, visibleBefore = false, visibleLive = false, visibleAfter = true,
            content = new { headline = "Grazie!" },
            version = 0,
        });

        var guest = _factory.CreateClient();

        var before = await KindsAsync(guest, party.Token);
        Assert.Equal(["location"], before);

        await StartAsync(party);
        Assert.Equal(["location"], await KindsAsync(guest, party.Token));

        await EndAsync(party);
        // Location was not marked for afterwards; the thank-you was.
        Assert.Equal(["thank-you"], await KindsAsync(guest, party.Token));
    }

    [Fact]
    public async Task Content_Is_Ordered_By_The_PRODUCT_Not_By_When_It_Was_Written()
    {
        var party = await SeedPartyAsync();
        // Written back to front on purpose.
        await WriteContentAsync(party, "info", new
        {
            enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false,
            content = new { title = "Parcheggio", body = "In fondo alla via" }, version = 0,
        });
        await WriteContentAsync(party, "invitation", new
        {
            enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false,
            content = new { headline = "Siamo felici di averti" }, version = 0,
        });

        // The guest reads the welcome first. The order is a product decision, not
        // data somebody drags around.
        Assert.Equal(["invitation", "info"], await KindsAsync(_factory.CreateClient(), party.Token));
    }

    [Fact]
    public async Task A_Payload_Is_Validated_Against_Its_Kind_And_Canonicalised()
    {
        var party = await SeedPartyAsync();

        // A kind the product does not define is a not-found: there is no such
        // slot to talk about.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await party.Owner.PutAsJsonAsync(
                $"/api/parties/{party.PartyId}/guest-content/free-text",
                new { enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false, content = new { }, version = 0 })).StatusCode);

        // A required field missing, and an over-long one, are both refused.
        foreach (var bad in new object[]
        {
            new { address = "Via Roma 1" },
            new { venueName = new string('x', 401), address = "Via Roma 1" },
        })
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await party.Owner.PutAsJsonAsync(
                    $"/api/parties/{party.PartyId}/guest-content/location",
                    new { enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false, content = bad, version = 0 })).StatusCode);
        }

        // A field the shape does not declare is DROPPED rather than stored:
        // knowing the route is not permission to persist arbitrary documents.
        var saved = await WriteContentAsync(party, "location", new
        {
            enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false,
            content = new
            {
                venueName = "  Villa Aurora  ", address = "Via Roma 1",
                script = "<script>alert(1)</script>", mapUrl = "https://evil.example",
            },
            version = 0,
        });
        var content = saved.GetProperty("content");
        Assert.Equal("Villa Aurora", content.GetProperty("venueName").GetString());
        Assert.False(content.TryGetProperty("script", out _));
        Assert.False(content.TryGetProperty("mapUrl", out _));
    }

    [Fact]
    public async Task A_Slot_Has_Its_OWN_Version_And_A_Conflict_Returns_The_Current_One()
    {
        var party = await SeedPartyAsync();
        var first = await WriteContentAsync(party, "info", new
        {
            enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
            content = new { title = "Primo", body = "Testo" }, version = 0,
        });
        Assert.Equal(1, first.GetProperty("version").GetInt32());

        var stale = await party.Owner.PutAsJsonAsync(
            $"/api/parties/{party.PartyId}/guest-content/info",
            new
            {
                enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
                content = new { title = "Secondo", body = "Testo" }, version = 0,
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("error").GetString());
        Assert.Equal(
            "Primo",
            body.GetProperty("content").GetProperty("content").GetProperty("title").GetString());

        // Renaming the PARTY does not disturb the slot: unrelated decisions do
        // not contend for one version.
        var partyDto = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        (await party.Owner.PatchAsJsonAsync($"/api/parties/{party.PartyId}", new
        {
            title = "Un altro nome", version = partyDto.GetProperty("version").GetInt32(),
        })).EnsureSuccessStatusCode();

        var slots = await party.Owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.PartyId}/guest-content");
        var info = slots.EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "info");
        Assert.Equal(1, info.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Every_Kind_Comes_Back_With_The_Products_Own_Defaults()
    {
        var party = await SeedPartyAsync();
        var slots = (await party.Owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.PartyId}/guest-content")).EnumerateArray().ToList();

        Assert.Equal(
            ["invitation", "location", "dress-code", "menu", "info", "thank-you"],
            slots.Select(s => s.GetProperty("kind").GetString()).ToArray());
        // Untouched: version 0, off, and where the PRODUCT says each belongs.
        Assert.All(slots, s => Assert.Equal(0, s.GetProperty("version").GetInt32()));
        Assert.All(slots, s => Assert.False(s.GetProperty("enabled").GetBoolean()));

        var invitation = slots.Single(s => s.GetProperty("kind").GetString() == "invitation");
        Assert.True(invitation.GetProperty("visibleBefore").GetBoolean());
        Assert.False(invitation.GetProperty("visibleAfter").GetBoolean());

        var thankYou = slots.Single(s => s.GetProperty("kind").GetString() == "thank-you");
        Assert.False(thankYou.GetProperty("visibleBefore").GetBoolean());
        Assert.True(thankYou.GetProperty("visibleAfter").GetBoolean());

        var menu = slots.Single(s => s.GetProperty("kind").GetString() == "menu");
        Assert.True(menu.GetProperty("visibleBefore").GetBoolean());
        Assert.True(menu.GetProperty("visibleLive").GetBoolean());
    }

    [Fact]
    public async Task Guest_Content_Is_The_Owners_Own()
    {
        var party = await SeedPartyAsync();
        var (_, stranger) = await NewHostAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/parties/{party.PartyId}/guest-content")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.PutAsJsonAsync(
                $"/api/parties/{party.PartyId}/guest-content/info",
                new
                {
                    enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false,
                    content = new { title = "Mio", body = "Testo" }, version = 0,
                })).StatusCode);
    }

    [Fact]
    public async Task The_Guest_Context_Carries_No_Internals()
    {
        var party = await SeedPartyAsync();
        await StartAsync(party);
        await WriteContentAsync(party, "location", new
        {
            enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
            content = new { venueName = "Villa Aurora", address = "Via Roma 1" }, version = 0,
        });

        var raw = await (await _factory.CreateClient().GetAsync($"/api/party/{party.Token}"))
            .Content.ReadAsStringAsync();

        foreach (var forbidden in new[]
        {
            "ownerUserId", "partyId", "albumId", "tokenHash", "storageKey",
            "sha256", "blobObjectId", "latitude", "longitude", "embedding",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(party.Token, raw.Replace($"/party/{party.Token}", string.Empty));
    }

    // --- helpers ------------------------------------------------------------

    private sealed record SeededParty(
        HttpClient Owner, Guid PartyId, Guid AlbumId, Guid PhotoId, string Token, string UploadToken);

    private async Task<(Guid UserId, HttpClient Client)> NewHostAsync() =>
        await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", EveryPartyPermission);

    private async Task<(string RoleKey, HttpClient Client)> NewHostWithRoleAsync()
    {
        var email = $"host-{Guid.NewGuid():N}@example.com";
        var roleKey = await _factory.CreateRoleAsync($"Host {email}", EveryPartyPermission);
        var (_, client) = await _factory.CreateRoleClientAsync(roleKey, email);
        return (roleKey, client);
    }

    private async Task<SeededParty> SeedPartyAsync(HttpClient? host = null)
    {
        var owner = host ?? (await NewHostAsync()).Client;
        var albumId = (await (await owner.PostAsJsonAsync(
            "/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var photoId = await AddPhotoAsync(owner, albumId, "first.png");

        var enable = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();
        var partyId = settings.GetProperty("partyId").GetGuid();

        // The party is NAMED, because a party names itself: the title the guest
        // sees is the party's, and it is not the album's.
        var party = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        (await owner.PatchAsJsonAsync($"/api/parties/{partyId}", new
        {
            title = "Festa di Marta", version = party.GetProperty("version").GetInt32(),
        })).EnsureSuccessStatusCode();

        return new SeededParty(
            owner, partyId, albumId, photoId,
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..],
            UploadTokenFrom(settings));
    }

    private static async Task<Guid> AddPhotoAsync(HttpClient owner, Guid albumId, string name)
    {
        var part = new ByteArrayContent(Metadata.ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var upload = await owner.PostAsync("/api/files", multipart);
        upload.EnsureSuccessStatusCode();
        var fileId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static string UploadTokenFrom(JsonElement settings)
    {
        var url = settings.GetProperty("uploadUrl").GetString()!;
        return url["/party/".Length..^"/upload".Length];
    }

    private static async Task StartAsync(SeededParty party) =>
        await PartyTestHost.StartAsync(party.Owner, party.PartyId);

    private static async Task EndAsync(SeededParty party)
    {
        var dto = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        (await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/end-live",
            new { version = dto.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
    }

    // Written straight to the row: the windows are ordinary metadata, and these
    // tests are about what the POLICY does with them rather than about the form.
    // The album's CHOSEN cover, written straight to the row: these tests are
    // about which file a phase will serve, not about the cover-setting route.
    private async Task SetCoverAsync(Guid albumId, Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CoverFileItemId, fileItemId));
    }

    private async Task SetWindowsAsync(SeededParty party, DateTime? guest, DateTime? library)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Parties.Where(p => p.Id == party.PartyId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.GuestAccessExpiresAt, guest)
                .SetProperty(p => p.LibraryAccessExpiresAt, library));
    }

    private static async Task<JsonElement> WriteContentAsync(
        SeededParty party, string kind, object body)
    {
        var response = await party.Owner.PutAsJsonAsync(
            $"/api/parties/{party.PartyId}/guest-content/{kind}", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string[]> KindsAsync(HttpClient guest, string token)
    {
        var context = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}");
        return context.GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString()!)
            .ToArray();
    }
}

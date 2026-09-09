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
/// P1: Party is the root.
///
/// <para>Four properties, and they are the whole of what "root" means here.
/// A party EXISTS as a row of its own, separate from the album it draws on and
/// from the QR codes it hands out. Its media arrives through a media SOURCE, so
/// a second album would be a row rather than a migration. A public token
/// resolves through the party — token → link → party → main source → album —
/// and everything downstream still receives the (owner, album) pair it already
/// handled correctly. And what a party may offer is decided by the HOST's role
/// on every request, not by what was true when the QR was printed.</para>
/// </summary>
public sealed class PartyCoreCutoverTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyCoreCutoverTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // Every Party capability, which is what Member carries.
    private static readonly string[] EveryPartyPermission =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    // --- 1. The aggregate root -------------------------------------------

    [Fact]
    public async Task Enabling_Party_Mode_Creates_One_Party_With_One_Main_Media_Source()
    {
        var (ownerId, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Giulia & Matteo");

        var status = await EnablePartyAsync(owner, albumId);
        var partyId = status.GetProperty("partyId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var party = await db.Parties.SingleAsync();
        Assert.Equal(partyId, party.Id);
        Assert.Equal(ownerId, party.OwnerUserId);
        // The album's name is the only title the compatibility entry point can
        // honestly produce — there is no Party UI to ask for one yet.
        Assert.Equal("Giulia & Matteo", party.Title);
        // Enabling the public capability IS publishing the party.
        Assert.Equal(PartyStatuses.Published, party.Status);

        var source = await db.PartyMediaSources.SingleAsync();
        Assert.Equal(partyId, source.PartyId);
        Assert.Equal(albumId, source.AlbumId);
        Assert.Equal(PartyMediaSourceRoles.Main, source.Role);

        // The capability names the party. Its owner/album columns survive as a
        // projection, so every existing service keeps working — but the party
        // is what the token now resolves through.
        var link = await db.PartyAlbumLinks.SingleAsync();
        Assert.Equal(partyId, link.PartyId);
        Assert.Equal(ownerId, link.OwnerUserId);
        Assert.Equal(albumId, link.AlbumId);
    }

    [Fact]
    public async Task Re_Enabling_Rotates_The_Token_And_Keeps_The_Same_Party()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");

        var first = await EnablePartyAsync(owner, albumId);
        var firstParty = first.GetProperty("partyId").GetGuid();
        var firstToken = TokenFromUrl(first.GetProperty("partyUrl").GetString()!);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        var second = await EnablePartyAsync(owner, albumId);

        // A new QR, because a new link is a new capability — and the SAME
        // party, because the event is not the QR. This is also the migration's
        // rule: one party per album, however many links it has been through.
        Assert.NotEqual(firstToken, TokenFromUrl(second.GetProperty("partyUrl").GetString()!));
        Assert.Equal(firstParty, second.GetProperty("partyId").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.Parties.ToListAsync());
        Assert.Single(await db.PartyMediaSources.ToListAsync());
        Assert.Equal(2, await db.PartyAlbumLinks.CountAsync());
        Assert.All(
            await db.PartyAlbumLinks.ToListAsync(),
            link => Assert.Equal(firstParty, link.PartyId));
    }

    [Fact]
    public async Task A_Party_Whose_Main_Album_Is_Gone_Resolves_To_Nothing()
    {
        // The seam walks to the main media source and then checks the album is
        // still the party's owner's. Severing that link severs public access —
        // the same generic 404 an unknown token gets.
        var (ownerId, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var token = TokenFromUrl(
            (await EnableAndStartAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{token}")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stranger = await _factory.SeedUserAsync($"stranger-{Guid.NewGuid():N}@example.com");
            await db.Albums.Where(a => a.Id == albumId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.OwnerUserId, stranger));
            _ = ownerId;
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_The_Album_Takes_Its_Party_With_It()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await EnablePartyAsync(owner, albumId);

        (await owner.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // A party with no media source at all has nothing to show and no way to
        // acquire one, so it goes with the album — as do its links.
        Assert.Empty(await db.Parties.ToListAsync());
        Assert.Empty(await db.PartyMediaSources.ToListAsync());
        Assert.Empty(await db.PartyAlbumLinks.ToListAsync());
    }

    [Fact]
    public async Task A_Second_Album_Is_A_Row_Rather_Than_A_Migration()
    {
        // P1 gives semantics to `main` only, and creates exactly one. What is
        // being asserted here is the SHAPE: the schema already accepts another
        // source for the same party, and refuses the same album twice.
        var (ownerId, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var otherAlbumId = await CreateAlbumAsync(owner, "Fotografo");
        var partyId = (await EnablePartyAsync(owner, albumId)).GetProperty("partyId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = partyId,
            AlbumId = otherAlbumId,
            Role = "official",
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.PartyMediaSources.CountAsync(s => s.PartyId == partyId));
        _ = ownerId;

        // The key IS the uniqueness rule: one album contributes to one party
        // once. A second context so the refusal comes from the DATABASE rather
        // than from EF noticing the duplicate before it ever sends anything.
        using var second = _factory.Services.CreateScope();
        var otherDb = second.ServiceProvider.GetRequiredService<AppDbContext>();
        otherDb.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = partyId,
            AlbumId = otherAlbumId,
            Role = "guest-contributions",
            SortOrder = 2,
            CreatedAt = DateTime.UtcNow,
        });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => otherDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Re_Opening_A_Migrated_Party_Publishes_The_One_That_Is_Already_There()
    {
        // The state the migration leaves an album whose party was revoked: a
        // party in Draft, its main source, and dead links. Enabling party mode
        // again must adopt that party and publish it — not mint a second one,
        // and not leave a live QR hanging off something still marked Draft.
        var (ownerId, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa dell'anno scorso");
        var migratedPartyId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PartySeed.Party(db, migratedPartyId, ownerId, albumId,
                title: "Festa dell'anno scorso", status: PartyStatuses.Draft);
            await db.SaveChangesAsync();
        }

        var status = await EnablePartyAsync(owner, albumId);
        Assert.Equal(migratedPartyId, status.GetProperty("partyId").GetGuid());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var party = await db.Parties.SingleAsync();
            Assert.Equal(migratedPartyId, party.Id);
            Assert.Equal(PartyStatuses.Published, party.Status);
            Assert.Equal(2, party.Version);
            Assert.Single(await db.PartyMediaSources.ToListAsync());
        }

        // And the guests can walk in — to the INVITATION first, because that is
        // what a published party is, and then to the party once it starts.
        var guest = _factory.CreateClient();
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);
        var invitation = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}");
        Assert.Equal("before", invitation.GetProperty("phase").GetString());

        await PartyTestHost.StartAsync(owner, migratedPartyId);
        var live = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}");
        Assert.Equal("live", live.GetProperty("phase").GetString());
    }

    // --- 2. Lifecycle ------------------------------------------------------

    [Fact]
    public async Task The_Lifecycle_Runs_Draft_To_Published_To_Live_To_Ended()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var partyId = (await EnablePartyAsync(owner, albumId)).GetProperty("partyId").GetGuid();

        var published = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        Assert.Equal(PartyStatuses.Published, published.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, published.GetProperty("liveStartedAt").ValueKind);
        // The main media source travels with the party, named for the owner.
        var sources = published.GetProperty("mediaSources").EnumerateArray().Single();
        Assert.Equal(albumId, sources.GetProperty("albumId").GetGuid());
        Assert.Equal(PartyMediaSourceRoles.Main, sources.GetProperty("role").GetString());

        var live = await TransitionAsync(owner, partyId, "start-live", published.GetProperty("version").GetInt32());
        Assert.Equal(PartyStatuses.Live, live.GetProperty("status").GetString());
        // The timestamp records what HAPPENED, never what a clock said.
        Assert.NotEqual(JsonValueKind.Null, live.GetProperty("liveStartedAt").ValueKind);

        var ended = await TransitionAsync(owner, partyId, "end-live", live.GetProperty("version").GetInt32());
        Assert.Equal(PartyStatuses.Ended, ended.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, ended.GetProperty("liveEndedAt").ValueKind);
    }

    [Fact]
    public async Task A_Move_That_Does_Not_Exist_Is_Refused_And_Changes_Nothing()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var partyId = (await EnablePartyAsync(owner, albumId)).GetProperty("partyId").GetGuid();
        var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}"))
            .GetProperty("version").GetInt32();

        // Publishing something already published is not idempotent-and-fine: a
        // transition that silently succeeds having changed nothing makes "did
        // the host announce this party" unanswerable.
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/publish", new { version });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_transition", body.GetProperty("error").GetString());

        // Ending a party nobody started is refused for the same reason.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync($"/api/parties/{partyId}/end-live", new { version })).StatusCode);

        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        Assert.Equal(PartyStatuses.Published, after.GetProperty("status").GetString());
        Assert.Equal(version, after.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_Stale_Version_Conflicts_Rather_Than_Overwriting()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var partyId = (await EnablePartyAsync(owner, albumId)).GetProperty("partyId").GetGuid();
        var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}"))
            .GetProperty("version").GetInt32();

        await TransitionAsync(owner, partyId, "start-live", version);

        // The second host read the party before the first pressed start.
        var stale = await owner.PostAsJsonAsync($"/api/parties/{partyId}/start-live", new { version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("error").GetString());
        // The CURRENT state travels with the refusal, so a client can say what
        // actually happened rather than guess.
        Assert.Equal(PartyStatuses.Live, body.GetProperty("party").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Starting_And_Finishing_A_Game_Never_Moves_The_Party()
    {
        // Party lifecycle and game lifecycle are separate: a host may play three
        // rounds during one evening, and the evening is Live throughout.
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var partyId = (await EnablePartyAsync(owner, albumId)).GetProperty("partyId").GetGuid();
        await CreateChallengeAsync(owner, albumId);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-game-settings", new
        {
            gameEnabled = true,
            minChallengeIntervalSeconds = 300,
            maxChallengeIntervalSeconds = 540,
            votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");

        var lobby = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-game");
        var started = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/party-game/commands",
            new { command = PartyGameCommands.Start, expectedVersion = lobby.GetProperty("version").GetInt32() });
        started.EnsureSuccessStatusCode();

        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("version").GetInt32(), after.GetProperty("version").GetInt32());
    }

    // --- 3. Permissions: both directions ----------------------------------

    [Fact]
    public async Task Losing_Party_Access_Closes_The_Party_For_Guests_Already_At_It()
    {
        var (roleKey, _, owner) = await NewHostWithRoleAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAndStartAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        // The guest is at the party, holding a valid QR, in a browser nobody is
        // going to ask to sign in.
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{token}")).StatusCode);

        await _factory.SetRolePermissionsAsync(roleKey);

        // A token cannot outrank a permission the host no longer holds, and the
        // refusal is the same generic 404 an unknown token gets.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/items")).StatusCode);
    }

    [Fact]
    public async Task A_Permission_Cannot_Rescue_A_Revoked_Capability()
    {
        // The other direction, and it has to be tested from both ends: holding
        // every Party permission does not make a dead QR work.
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var token = TokenFromUrl(
            (await EnableAndStartAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{token}")).StatusCode);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_Feature_The_Host_May_Not_Run_Is_Absent_Rather_Than_Refused_Late()
    {
        // A host who may run parties but not games: the guest hub says there is
        // no game, and the game's own routes answer the generic unavailable —
        // while everything the host IS permitted to run keeps working.
        var (_, owner) = await NewHostAsync(
            Permissions.PartyAccess, Permissions.PartyContributions);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAndStartAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        var guest = _factory.CreateClient();
        var hub = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}");
        var capabilities = hub.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("gameUrl").ValueKind);
        // Contributions are permitted, so the hub still names where they go.
        Assert.Equal(JsonValueKind.String, capabilities.GetProperty("contributionUrl").ValueKind);

        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/game")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/challenges")).StatusCode);
        // Viewing the party is not a game.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{token}/items")).StatusCode);
    }

    [Fact]
    public async Task Losing_Contributions_Closes_The_Guest_Upload_Channel_Only()
    {
        var (roleKey, _, owner) = await NewHostWithRoleAsync();
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAndStartAsync(owner, albumId);
        var viewToken = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);
        var uploadToken = UploadTokenFromStatus(status);

        var guest = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.PostAsync($"/api/party/{uploadToken}/upload-session", null)).StatusCode);

        await _factory.SetRolePermissionsAsync(
            roleKey, Permissions.PartyAccess, Permissions.PartyGames,
            Permissions.PartyPrint, Permissions.PartyFaceSearch);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.PostAsync($"/api/party/{uploadToken}/upload-session", null)).StatusCode);
        // The party itself is untouched: the host may still hold it, and the
        // hub simply stops offering a place to contribute.
        var hub = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        Assert.Equal(
            JsonValueKind.Null,
            hub.GetProperty("capabilities").GetProperty("contributionUrl").ValueKind);
    }

    [Fact]
    public async Task A_Host_Without_Party_Access_Cannot_Enable_Party_Mode()
    {
        // The role EDITOR refuses to store a feature key without its parent, so
        // the row is written raw — the point is that the endpoint guard holds
        // anyway, against a shape the service would never have produced.
        var email = $"noparty-{Guid.NewGuid():N}@example.com";
        var roleKey = await _factory.CreateRoleAsync(
            $"Contributi {email}", Permissions.PartyAccess, Permissions.PartyContributions);
        await _factory.SetRolePermissionsRawAsync(roleKey, Permissions.PartyContributions);
        var (_, owner) = await _factory.CreateRoleClientAsync(roleKey, email);
        var albumId = await CreateAlbumAsync(owner, "Festa");

        // A feature permission alone opens nothing — the same parent rule the
        // catalogue states and the composite policies enforce.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PatchAsJsonAsync(
                $"/api/albums/{albumId}/party-settings", new { enabled = true })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.GetAsync($"/api/albums/{albumId}/party-settings")).StatusCode);
    }

    [Fact]
    public async Task A_Party_Is_Never_Another_Owners_To_Read()
    {
        var (_, alice) = await NewHostAsync();
        var (_, bob) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(alice, "Festa di Alice");
        var partyId = (await EnablePartyAsync(alice, albumId)).GetProperty("partyId").GetGuid();

        // Not 403: a stranger must not learn that this party exists.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/parties/{partyId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsJsonAsync($"/api/parties/{partyId}/start-live", new { version = 1 })).StatusCode);
    }

    // --- helpers ----------------------------------------------------------

    private async Task<(Guid UserId, HttpClient Client)> NewHostAsync(params string[] permissions) =>
        await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com",
            permissions.Length == 0 ? EveryPartyPermission : permissions);

    // The same, keeping the role key so the test can take a capability away
    // mid-party — which is the only way to observe live revocation.
    private async Task<(string RoleKey, Guid UserId, HttpClient Client)> NewHostWithRoleAsync()
    {
        var email = $"host-{Guid.NewGuid():N}@example.com";
        var roleKey = await _factory.CreateRoleAsync($"Host {email}", EveryPartyPermission);
        var (userId, client) = await _factory.CreateRoleClientAsync(roleKey, email);
        return (roleKey, userId, client);
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Enabling guest access PUBLISHES the party — an invitation, which is
    // deliberately not the party itself. A test about a guest AT the party
    // starts it, exactly as a host does; a test about the lifecycle does not,
    // because where enabling leaves the party is the thing it is checking.
    private static async Task<JsonElement> EnableAndStartAsync(HttpClient owner, Guid albumId)
    {
        var settings = await EnablePartyAsync(owner, albumId);
        await PartyTestHost.StartAsync(owner, settings);
        return settings;
    }

    private static async Task CreateChallengeAsync(HttpClient owner, Guid albumId) =>
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/party-challenges", new
        {
            title = "Un brindisi",
            body = "Fai un brindisi agli sposi",
            kind = "dare",
            isEnabled = true,
        })).EnsureSuccessStatusCode();

    private static async Task<JsonElement> TransitionAsync(
        HttpClient owner, Guid partyId, string action, int version)
    {
        var response = await owner.PostAsJsonAsync($"/api/parties/{partyId}/{action}", new { version });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string TokenFromUrl(string partyUrl) => partyUrl["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!; // "/party/{token}/upload"
        return url["/party/".Length..^"/upload".Length];
    }
}

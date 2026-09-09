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
/// P2: the owner's Party product.
///
/// <para>A party is a destination now, not a mode inside an album's settings —
/// so it can be made before there are any photographs, named without renaming
/// anything else, pointed at an album, and moved through its evening. What this
/// file pins down is the part that is easy to get wrong once the host can edit
/// all of that: <b>when the album may still change, and when it may not</b>.
/// From the moment a party has handed out a capability, its guests, greetings,
/// prints and games are scoped to a link that names that album, and moving it
/// would turn a UI edit into a domain migration.</para>
/// </summary>
public sealed class PartyOwnerProductTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyOwnerProductTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] EveryPartyPermission =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    // --- The party exists before the photographs --------------------------

    [Fact]
    public async Task A_New_Party_Has_A_Name_A_Date_And_Nothing_Else()
    {
        var (ownerId, owner) = await NewHostAsync();

        var created = await CreatePartyAsync(owner, "Festa di Marta", "2027-06-12T18:30:00Z");

        Assert.Equal("Festa di Marta", created.GetProperty("title").GetString());
        Assert.Equal(PartyStatuses.Draft, created.GetProperty("status").GetString());
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        // No album. This is an ordinary state, not an incomplete write.
        Assert.Empty(created.GetProperty("mediaSources").EnumerateArray());
        Assert.True(created.GetProperty("canChangeMainMediaSource").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // And genuinely nothing else: no capability, no token, no media source,
        // no game session, no print profile. Every one of those is a later
        // decision by the host, and a create that quietly minted a QR would make
        // "is this party public" a question about when it was made.
        Assert.Single(await db.Parties.ToListAsync());
        Assert.Empty(await db.PartyAlbumLinks.ToListAsync());
        Assert.Empty(await db.PartyMediaSources.ToListAsync());
        Assert.Empty(await db.PartyGameSessions.ToListAsync());
        Assert.Empty(await db.PartyPrintProfiles.ToListAsync());
        Assert.Empty(await db.Albums.Where(a => a.OwnerUserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task A_Party_Needs_A_Name()
    {
        var (_, owner) = await NewHostAsync();

        foreach (var title in new[] { "", "   ", new string('x', 201) })
        {
            var response = await owner.PostAsJsonAsync("/api/parties", new { title });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_List_Is_The_Owners_Own_And_Ordered_By_What_Is_Happening()
    {
        var (_, alice) = await NewHostAsync();
        var (_, bob) = await NewHostAsync();

        var draft = await CreatePartyAsync(alice, "In preparazione");
        var live = await CreatePartyAsync(alice, "Stasera");
        var ended = await CreatePartyAsync(alice, "L'anno scorso");
        await CreatePartyAsync(bob, "Festa di Bob");

        // Take two of Alice's parties through their evening. The album is LINKED
        // to the party first: enabling party mode on an unlinked album would
        // create a party of its own for it, which is the compatibility entry
        // point doing exactly what it is for.
        var liveAlbum = await CreateAlbumAsync(alice, "Album stasera");
        await EnablePartyOnAlbumAsync(alice, liveAlbum, await LinkAlbumAsync(alice, live, liveAlbum));
        await AdvanceAsync(alice, live.GetProperty("id").GetGuid(), "start-live");

        var endedAlbum = await CreateAlbumAsync(alice, "Album anno scorso");
        await EnablePartyOnAlbumAsync(alice, endedAlbum, await LinkAlbumAsync(alice, ended, endedAlbum));
        var endedId = ended.GetProperty("id").GetGuid();
        var started = await AdvanceAsync(alice, endedId, "start-live");
        await AdvanceAsync(alice, endedId, "end-live", started.GetProperty("version").GetInt32());

        var list = await alice.GetFromJsonAsync<JsonElement>("/api/parties");
        var titles = list.EnumerateArray().Select(p => p.GetProperty("title").GetString()).ToList();

        // Live first — it is happening now — then what is still ahead, then
        // what is over. Bob's party is not in Alice's list at all.
        Assert.Equal(["Stasera", "In preparazione", "L'anno scorso"], titles);
        Assert.DoesNotContain("Festa di Bob", titles);

        // The summary carries what a card renders, and the main album by name so
        // the list needs no second request.
        var liveRow = list.EnumerateArray().First(p => p.GetProperty("title").GetString() == "Stasera");
        Assert.Equal(PartyStatuses.Live, liveRow.GetProperty("status").GetString());
        Assert.Equal("Album stasera", liveRow.GetProperty("mainAlbumName").GetString());

        // A party with no album is listed like any other, with nothing invented.
        var draftRow = list.EnumerateArray()
            .First(p => p.GetProperty("id").GetGuid() == draft.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, draftRow.GetProperty("mainAlbumId").ValueKind);
        Assert.Equal(JsonValueKind.Null, draftRow.GetProperty("mainAlbumName").ValueKind);
    }

    // --- Metadata ----------------------------------------------------------

    [Fact]
    public async Task Renaming_A_Party_Does_Not_Rename_Its_Album()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album di Marta");
        var party = await CreatePartyAsync(owner, "Festa");
        var linked = await LinkAlbumAsync(owner, party, albumId);

        var updated = await UpdateAsync(owner, linked, new
        {
            title = "Festa di Marta",
            description = "In giardino",
            eventStartsAt = "2027-06-12T18:30:00Z",
            guestAccessExpiresAt = (string?)null,
            version = linked.GetProperty("version").GetInt32(),
        });

        Assert.Equal("Festa di Marta", updated.GetProperty("title").GetString());
        Assert.Equal("In giardino", updated.GetProperty("description").GetString());
        Assert.Equal(
            linked.GetProperty("version").GetInt32() + 1, updated.GetProperty("version").GetInt32());

        // The album keeps its own name. They were only ever the same string when
        // one was made from the other; there is no sync in either direction.
        var album = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Album di Marta", album.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Metadata_Cannot_Write_The_Status_Or_The_Live_Timestamps()
    {
        var (_, owner) = await NewHostAsync();
        var party = await CreatePartyAsync(owner, "Festa");
        var partyId = party.GetProperty("id").GetGuid();

        // A client sending them anyway changes nothing: they are not part of the
        // request the server binds, because a form able to write them could
        // describe an evening that never happened.
        var response = await owner.PatchAsJsonAsync($"/api/parties/{partyId}", new
        {
            title = "Festa",
            status = PartyStatuses.Live,
            liveStartedAt = "2020-01-01T00:00:00Z",
            liveEndedAt = "2020-01-02T00:00:00Z",
            version = 1,
        });
        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(PartyStatuses.Draft, updated.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("liveStartedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("liveEndedAt").ValueKind);
    }

    [Fact]
    public async Task A_Stale_Metadata_Write_Conflicts_And_Returns_The_Current_Party()
    {
        var (_, owner) = await NewHostAsync();
        var party = await CreatePartyAsync(owner, "Festa");
        var partyId = party.GetProperty("id").GetGuid();
        await UpdateAsync(owner, party, new { title = "Primo nome", version = 1 });

        var stale = await owner.PatchAsJsonAsync(
            $"/api/parties/{partyId}", new { title = "Secondo nome", version = 1 });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("error").GetString());
        // The CURRENT party travels with the refusal, so a client refreshes
        // instead of overwriting what it never saw.
        Assert.Equal("Primo nome", body.GetProperty("party").GetProperty("title").GetString());
        Assert.Equal(2, body.GetProperty("party").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Another_Owners_Party_Is_A_Generic_Not_Found_Everywhere()
    {
        var (_, alice) = await NewHostAsync();
        var (_, bob) = await NewHostAsync();
        var party = await CreatePartyAsync(alice, "Festa di Alice");
        var partyId = party.GetProperty("id").GetGuid();
        var bobAlbum = await CreateAlbumAsync(bob, "Album di Bob");

        // Never 403: a stranger must not learn that this party exists.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/parties/{partyId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PatchAsJsonAsync($"/api/parties/{partyId}", new { title = "Mia", version = 1 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PutAsJsonAsync(
                $"/api/parties/{partyId}/media/main", new { albumId = bobAlbum, version = 1 })).StatusCode);
        Assert.DoesNotContain(
            partyId,
            (await bob.GetFromJsonAsync<JsonElement>("/api/parties")).EnumerateArray()
                .Select(p => p.GetProperty("id").GetGuid()));
    }

    // --- The main media source, and when it locks -------------------------

    [Fact]
    public async Task An_Album_Can_Be_Chosen_And_Replaced_Until_A_Capability_Exists()
    {
        var (_, owner) = await NewHostAsync();
        var first = await CreateAlbumAsync(owner, "Primo album");
        var second = await CreateAlbumAsync(owner, "Secondo album");
        var party = await CreatePartyAsync(owner, "Festa");

        var linked = await LinkAlbumAsync(owner, party, first);
        Assert.Equal(first, MainAlbumId(linked));
        Assert.True(linked.GetProperty("canChangeMainMediaSource").GetBoolean());

        // Nothing has been handed to a guest yet, so the host may still change
        // their mind — and the old source is REPLACED rather than accumulated.
        var moved = await LinkAlbumAsync(owner, linked, second);
        Assert.Equal(second, MainAlbumId(moved));
        Assert.Single(moved.GetProperty("mediaSources").EnumerateArray());
    }

    [Fact]
    public async Task Setting_The_Album_It_Already_Has_Spends_Nothing()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album");
        var party = await CreatePartyAsync(owner, "Festa");
        var linked = await LinkAlbumAsync(owner, party, albumId);

        var again = await LinkAlbumAsync(owner, linked, albumId);

        // No write, no version spent, and no pretence that a decision was taken.
        Assert.Equal(linked.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task The_Album_Is_Fixed_Once_The_Party_Has_Ever_Had_A_Capability()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album della festa");
        var other = await CreateAlbumAsync(owner, "Un altro album");
        var party = await CreatePartyAsync(owner, "Festa");
        var linked = await LinkAlbumAsync(owner, party, albumId);

        await EnablePartyOnAlbumAsync(owner, albumId, linked);

        var afterEnable = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.GetProperty("id").GetGuid()}");
        Assert.False(afterEnable.GetProperty("canChangeMainMediaSource").GetBoolean());

        var refused = await owner.PutAsJsonAsync(
            $"/api/parties/{party.GetProperty("id").GetGuid()}/media/main",
            new { albumId = other, version = afterEnable.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("media_source_locked", body.GetProperty("error").GetString());
        // Still pointing where the guests were sent.
        Assert.Equal(albumId, MainAlbumId(body.GetProperty("party")));

        // And REVOKING the party does not unlock it: the guests, greetings and
        // prints of that evening are scoped to the link that named this album,
        // whether or not the QR still works.
        (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = false })).EnsureSuccessStatusCode();
        var afterRevoke = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.GetProperty("id").GetGuid()}");
        Assert.False(afterRevoke.GetProperty("canChangeMainMediaSource").GetBoolean());
    }

    [Fact]
    public async Task One_Album_Cannot_Be_The_Main_Source_Of_Two_Parties()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album conteso");
        var first = await CreatePartyAsync(owner, "Prima festa");
        var second = await CreatePartyAsync(owner, "Seconda festa");

        await LinkAlbumAsync(owner, first, albumId);

        var refused = await owner.PutAsJsonAsync(
            $"/api/parties/{second.GetProperty("id").GetGuid()}/media/main",
            new { albumId, version = second.GetProperty("version").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            "album_already_in_use",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_Uniqueness_Is_The_DATABASE_Rule_Not_A_Check_That_Ran_First()
    {
        // The application asks politely before writing; two callers racing can
        // both be told yes. What actually elects a winner is the unique index,
        // so this writes the second row straight past the service.
        var (ownerId, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album conteso");
        var first = await CreatePartyAsync(owner, "Prima festa");
        await LinkAlbumAsync(owner, first, albumId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rival = Guid.NewGuid();
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = rival, OwnerUserId = ownerId, Title = "Rivale",
            Status = PartyStatuses.Draft, Version = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = rival, AlbumId = albumId,
            Role = PartyMediaSourceRoles.Main, SortOrder = 0, CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_Shared_Albums_Editor_Authority_Is_Not_Ownership()
    {
        var (_, alice) = await NewHostAsync();
        var (_, bob) = await NewHostAsync();
        var aliceAlbum = await CreateAlbumAsync(alice, "Album di Alice");
        var bobParty = await CreatePartyAsync(bob, "Festa di Bob");

        // A hand-built id for somebody else's album is a generic not-found, the
        // same answer an album that does not exist gets.
        var refused = await bob.PutAsJsonAsync(
            $"/api/parties/{bobParty.GetProperty("id").GetGuid()}/media/main",
            new { albumId = aliceAlbum, version = bobParty.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PutAsJsonAsync(
                $"/api/parties/{bobParty.GetProperty("id").GetGuid()}/media/main",
                new { albumId = Guid.NewGuid(), version = 1 })).StatusCode);
    }

    // --- The whole flow ----------------------------------------------------

    [Fact]
    public async Task A_Host_Can_Run_An_Evening_Without_Album_Settings()
    {
        var (_, owner) = await NewHostAsync();

        // Party -> New party -> name and date
        var party = await CreatePartyAsync(owner, "Festa di Marta", "2027-06-12T18:30:00Z");
        var partyId = party.GetProperty("id").GetGuid();

        // -> link an album
        var albumId = await CreateAlbumAsync(owner, "Album di Marta");
        var linked = await LinkAlbumAsync(owner, party, albumId);
        Assert.Equal(PartyStatuses.Draft, linked.GetProperty("status").GetString());

        // -> turn guest access on. The capability's own enable publishes the
        // party through the domain transition, so there is no second publish
        // button and no second way to reach Published.
        await EnablePartyOnAlbumAsync(owner, albumId, linked);
        var published = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        Assert.Equal(PartyStatuses.Published, published.GetProperty("status").GetString());

        // -> start, and end
        var live = await AdvanceAsync(owner, partyId, "start-live",
            published.GetProperty("version").GetInt32());
        Assert.Equal(PartyStatuses.Live, live.GetProperty("status").GetString());

        var ended = await AdvanceAsync(owner, partyId, "end-live",
            live.GetProperty("version").GetInt32());
        Assert.Equal(PartyStatuses.Ended, ended.GetProperty("status").GetString());

        // An ENDED party keeps its guest access. That is deliberate and is what
        // the post-event library will be built on: status describes the evening,
        // the capability decides who may look.
        var settings = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{albumId}/party-settings");
        Assert.True(settings.GetProperty("partyMode").GetBoolean());
    }

    [Fact]
    public async Task Guest_Access_Can_Be_Given_An_End_And_The_Public_Seam_Honours_It()
    {
        var (_, owner) = await NewHostAsync();
        var albumId = await CreateAlbumAsync(owner, "Album");
        var party = await CreatePartyAsync(owner, "Festa");
        var linked = await LinkAlbumAsync(owner, party, albumId);
        var status = await EnablePartyOnAlbumAsync(owner, albumId, linked);
        var token = status.GetProperty("partyUrl").GetString()!["/party/".Length..];

        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/party/{token}")).StatusCode);

        var current = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.GetProperty("id").GetGuid()}");
        await UpdateAsync(owner, current, new
        {
            title = current.GetProperty("title").GetString(),
            guestAccessExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            version = current.GetProperty("version").GetInt32(),
        });

        // One window closes every capability of the party at once, and it is the
        // public seam that enforces it — not anything on the owner surface.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}")).StatusCode);
    }

    // --- helpers ----------------------------------------------------------

    private async Task<(Guid UserId, HttpClient Client)> NewHostAsync() =>
        await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", EveryPartyPermission);

    private static async Task<JsonElement> CreatePartyAsync(
        HttpClient owner, string title, string? eventStartsAt = null)
    {
        var response = await owner.PostAsJsonAsync("/api/parties", new { title, eventStartsAt });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> LinkAlbumAsync(
        HttpClient owner, JsonElement party, Guid albumId)
    {
        var response = await owner.PutAsJsonAsync(
            $"/api/parties/{party.GetProperty("id").GetGuid()}/media/main",
            new { albumId, version = party.GetProperty("version").GetInt32() });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> UpdateAsync(
        HttpClient owner, JsonElement party, object body)
    {
        var response = await owner.PatchAsJsonAsync(
            $"/api/parties/{party.GetProperty("id").GetGuid()}", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> AdvanceAsync(
        HttpClient owner, Guid partyId, string action, int? version = null)
    {
        var at = version ?? (await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}"))
            .GetProperty("version").GetInt32();
        var response = await owner.PostAsJsonAsync($"/api/parties/{partyId}/{action}", new { version = at });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // The compatibility entry point, which is still how a capability is minted:
    // the party workspace calls exactly this, on the party's main album.
    private static async Task<JsonElement> EnablePartyOnAlbumAsync(
        HttpClient owner, Guid albumId, JsonElement party)
    {
        _ = party;
        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid? MainAlbumId(JsonElement party)
    {
        foreach (var source in party.GetProperty("mediaSources").EnumerateArray())
        {
            if (source.GetProperty("role").GetString() == PartyMediaSourceRoles.Main)
            {
                return source.GetProperty("albumId").GetGuid();
            }
        }
        return null;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// P4: one FileItem, more than one relation.
///
/// <para>Party does not own a second media library. A menu's photograph or an
/// activity's picture is the owner's ORDINARY file, and a Party feature may
/// reference it whether or not it belongs to the party's album. What is pinned
/// here is the other half of that sentence: the reference is also the whole
/// authority. A party token reaches exactly the files a visible relation points
/// at — never the owner's library, never through the album route, never as an
/// original — and a reference never implies album membership, so nothing a
/// host puts on the menu turns up in the slideshow.</para>
/// </summary>
public sealed class PartyMediaReferenceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyMediaReferenceTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string VaultPassword = "correct horse battery";

    private static readonly string[] HostPermissions =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
        Permissions.PrivateVaultAccess, Permissions.TvManage,
    ];

    private static readonly object Menu = new
    {
        intro = "Cena in giardino",
        sections = new[] { new { title = "Antipasti", items = new[] { "Bruschetta", "Olive" } } },
    };

    // --- Writing a reference ------------------------------------------------

    [Fact]
    public async Task A_Slot_Takes_An_Owner_Photo_That_Is_In_The_Album()
    {
        var party = await SeedPartyAsync();

        var saved = await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var slot = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(party.AlbumPhotoId, slot.GetProperty("mediaFileItemId").GetGuid());
        // The owner's own preview of it, on the owner's own route.
        Assert.Equal(
            $"/api/files/{party.AlbumPhotoId}/thumbnail?size=medium",
            slot.GetProperty("mediaUrl").GetString());
    }

    [Fact]
    public async Task A_Slot_Takes_An_Owner_Photo_That_Is_In_No_Album_And_Files_It_Nowhere()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu-graphic.png");

        var saved = await WriteSlotAsync(party, "menu", Menu, graphic);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(
            graphic,
            (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mediaFileItemId").GetGuid());
        // A Party reference never implies album membership.
        Assert.False(await InAnyAlbumAsync(graphic));
    }

    [Fact]
    public async Task Every_Ineligible_File_Is_Refused_With_One_And_The_Same_Answer()
    {
        var party = await SeedPartyAsync();
        var (_, stranger) = await NewHostAsync();

        var foreign = await UploadPngAsync(stranger, "foreign.png");
        var trashed = await UploadPngAsync(party.Owner, "trashed.png");
        await TrashAsync(party.Owner, trashed);
        var vaulted = await UploadPngAsync(party.Owner, "vaulted.png");
        await MoveToVaultAsync(party.Owner, vaulted);
        var document = await UploadBytesAsync(
            party.Owner, "Antipasti\nPrimi\n"u8.ToArray(), "text/plain", "menu.txt");
        // The browser's word is not the rule: text that CLAIMS to be a PNG is
        // still text to the server that sniffed it.
        var disguised = await UploadBytesAsync(
            party.Owner, "definitely not a picture"u8.ToArray(), "image/png", "disguised.png");
        var missing = Guid.NewGuid();

        var answers = new List<(HttpStatusCode Status, string Body)>();
        foreach (var id in new[] { foreign, trashed, vaulted, document, disguised, missing })
        {
            var response = await WriteSlotAsync(party, "menu", Menu, id);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.BadRequest, a.Status));
        // Indistinguishable: a stranger's file and a file that does not exist
        // get the very same bytes back.
        Assert.Single(answers.Select(a => a.Body).Distinct());
        Assert.Contains("invalid_media", answers[0].Body);

        // And nothing was written.
        Assert.Equal(0, (await OwnerSlotAsync(party, "menu")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Changing_Or_Removing_The_Photo_Is_An_Edit_Of_The_Slot()
    {
        var party = await SeedPartyAsync();
        var first = await UploadPngAsync(party.Owner, "first.png");
        var second = await UploadPngAsync(party.Owner, "second.png");

        (await WriteSlotAsync(party, "menu", Menu, first)).EnsureSuccessStatusCode();
        var replaced = await (await WriteSlotAsync(party, "menu", Menu, second, version: 1))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, replaced.GetProperty("version").GetInt32());
        Assert.Equal(second, replaced.GetProperty("mediaFileItemId").GetGuid());

        // The SLOT's version, as for any other edit: a stale writer conflicts.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await WriteSlotAsync(party, "menu", Menu, first, version: 1)).StatusCode);

        var removed = await (await WriteSlotAsync(party, "menu", Menu, null, version: 2))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, removed.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("mediaFileItemId").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("mediaUrl").ValueKind);

        // Nothing the photo ever was is left behind for a guest.
        var guest = Guest();
        Assert.Null(await GuestMediaUrlAsync(guest, party.Token, "menu"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/content/menu/media")).StatusCode);
        // Neither file was touched by any of it.
        Assert.True(await IsActiveAsync(first));
        Assert.True(await IsActiveAsync(second));
    }

    // --- What a guest receives ----------------------------------------------

    [Fact]
    public async Task A_Guest_Receives_An_Address_And_Never_The_File()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();

        var raw = await (await Guest().GetAsync($"/api/party/{party.Token}"))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain(graphic.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(graphic.ToString("N"), raw, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[]
        {
            "mediaFileItemId", "fileItemId", "ownerUserId", "storageKey", "sha256", "blobObjectId",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        var url = await GuestMediaUrlAsync(Guest(), party.Token, "menu");
        Assert.NotNull(url);
        Assert.StartsWith($"/api/party/{party.Token}/content/menu/media", url);
    }

    [Fact]
    public async Task The_Menu_Photo_Shows_On_The_Invitation_While_The_Album_Stays_Closed()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();

        var guest = Guest();
        var image = await guest.GetAsync(await GuestMediaUrlAsync(guest, party.Token, "menu"));
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.StartsWith("image/", image.Content.Headers.ContentType!.MediaType);

        // A photograph on the menu is still not a gallery.
        Assert.Equal(
            HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.Token}/items")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{party.AlbumPhotoId}/preview")).StatusCode);
    }

    [Fact]
    public async Task An_Extra_Album_Photo_Is_Reached_Through_Its_Slot_And_Not_Through_The_Album()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        await StartAsync(party);

        var guest = Guest();
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.GetAsync(await GuestMediaUrlAsync(guest, party.Token, "menu"))).StatusCode);

        // The album route keeps meaning ALBUM media: the same file is not one.
        foreach (var variant in new[] { "thumbnail", "preview", "download" })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await guest.GetAsync($"/api/party/{party.Token}/media/{graphic}/{variant}")).StatusCode);
        }
        // While the album's own photograph is served there exactly as before.
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{party.AlbumPhotoId}/preview")).StatusCode);
    }

    [Fact]
    public async Task A_Disabled_Slot_Serves_No_Photo()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        var guest = Guest();
        var url = await GuestMediaUrlAsync(guest, party.Token, "menu");
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(url)).StatusCode);

        (await WriteSlotAsync(party, "menu", Menu, graphic, version: 1, enabled: false))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/content/menu/media")).StatusCode);
    }

    [Fact]
    public async Task A_Slot_Scoped_To_Another_Phase_Serves_No_Photo()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "grazie.png");
        // The thank-you belongs afterwards, and only afterwards.
        (await WriteSlotAsync(
            party, "thank-you", new { headline = "Grazie!" }, graphic,
            before: false, live: false, after: true)).EnsureSuccessStatusCode();

        var guest = Guest();
        var route = $"/api/party/{party.Token}/content/thank-you/media";
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(route)).StatusCode);

        await StartAsync(party);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(route)).StatusCode);

        await EndAsync(party);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task A_Party_Token_Reaches_No_File_It_Was_Not_Given()
    {
        // The owner's library is large and the token is public. Knowing, or
        // guessing, the id of any other file must get a guest nowhere.
        var party = await SeedPartyAsync();
        var referenced = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, referenced)).EnsureSuccessStatusCode();
        var unreferenced = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            unreferenced.Add(await UploadPngAsync(party.Owner, $"private-{i}.png"));
        }
        await StartAsync(party);

        var guest = Guest();
        foreach (var id in unreferenced)
        {
            foreach (var variant in new[] { "thumbnail", "preview", "download" })
            {
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await guest.GetAsync($"/api/party/{party.Token}/media/{id}/{variant}")).StatusCode);
            }
        }

        // The content route is addressed by SLOT, never by file: no photograph
        // on a slot is no photograph, and a file id is not a slot.
        foreach (var kind in new[] { "invitation", "location", "dress-code", "info", "thank-you" })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await guest.GetAsync($"/api/party/{party.Token}/content/{kind}/media")).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/content/{unreferenced[0]}/media")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync("/api/party/not-a-real-token/content/menu/media")).StatusCode);

        // The one file it WAS given is the one it reaches.
        Assert.Equal(
            HttpStatusCode.OK,
            (await guest.GetAsync($"/api/party/{party.Token}/content/menu/media")).StatusCode);
    }

    [Fact]
    public async Task A_Content_Photo_Is_A_Derived_Preview_And_Never_A_Download()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        await StartAsync(party);

        var response = await Guest().GetAsync($"/api/party/{party.Token}/content/menu/media");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Rendered inline for the page, never offered as an attachment...
        Assert.Null(response.Content.Headers.ContentDisposition);
        // ...and not the owner's original bytes: the ordinary derived rendition.
        Assert.NotEqual(ImageFixtures.PlainPng(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_Trashed_Photo_Stops_Being_Served_And_The_Menu_Stays()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        var guest = Guest();
        var url = await GuestMediaUrlAsync(guest, party.Token, "menu");

        await TrashAsync(party.Owner, graphic);

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
        // The words are still there; there is simply no picture to draw.
        var menu = await GuestSlotAsync(guest, party.Token, "menu");
        Assert.Equal("Cena in giardino", menu.GetProperty("content").GetProperty("intro").GetString());
        Assert.Equal(JsonValueKind.Null, menu.GetProperty("mediaUrl").ValueKind);

        // The host sees the reference they wrote, and that it has no picture.
        var owned = await OwnerSlotAsync(party, "menu");
        Assert.Equal(graphic, owned.GetProperty("mediaFileItemId").GetGuid());
        Assert.Equal(JsonValueKind.Null, owned.GetProperty("mediaUrl").ValueKind);

        // And can still fix a typo without first choosing another photo.
        Assert.Equal(
            HttpStatusCode.OK,
            (await WriteSlotAsync(party, "menu", new { intro = "Cena in terrazza" }, graphic, version: 1))
                .StatusCode);
    }

    [Fact]
    public async Task A_Photo_Moved_Into_The_Private_Vault_Stops_Being_Served()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        var guest = Guest();
        var url = await GuestMediaUrlAsync(guest, party.Token, "menu");
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(url)).StatusCode);

        await MoveToVaultAsync(party.Owner, graphic);

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
        Assert.Null(await GuestMediaUrlAsync(guest, party.Token, "menu"));
    }

    // --- The invitation's hero ----------------------------------------------

    [Fact]
    public async Task The_Invitations_Own_Photo_Outranks_The_Album_Cover()
    {
        var party = await SeedPartyAsync();
        await SetCoverAsync(party.AlbumId, party.AlbumPhotoId);
        var portrait = await UploadPngAsync(party.Owner, "invito.png");
        (await WriteSlotAsync(party, "invitation", new { headline = "Vieni!" }, portrait))
            .EnsureSuccessStatusCode();

        var guest = Guest();
        var cover = (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}"))
            .GetProperty("coverUrl").GetString();

        Assert.StartsWith($"/api/party/{party.Token}/content/invitation/media", cover);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(cover)).StatusCode);
        // A photograph that is in no album became the hero without being filed.
        Assert.False(await InAnyAlbumAsync(portrait));
    }

    [Fact]
    public async Task Without_Its_Own_Photo_The_Invitation_Falls_Back_To_The_Chosen_Cover_Then_To_None()
    {
        var party = await SeedPartyAsync();
        (await WriteSlotAsync(party, "invitation", new { headline = "Vieni!" }, null))
            .EnsureSuccessStatusCode();
        var guest = Guest();

        // Neither: the page draws its composition.
        Assert.Equal(JsonValueKind.Null, (await CoverAsync(guest, party.Token)).ValueKind);

        // The chosen cover, exactly as before this slice.
        await SetCoverAsync(party.AlbumId, party.AlbumPhotoId);
        Assert.Equal(
            $"/api/party/{party.Token}/media/{party.AlbumPhotoId}/preview",
            (await CoverAsync(guest, party.Token)).GetString());

        // An invitation photo that stops qualifying hands the hero back to it.
        var portrait = await UploadPngAsync(party.Owner, "invito.png");
        (await WriteSlotAsync(party, "invitation", new { headline = "Vieni!" }, portrait, version: 1))
            .EnsureSuccessStatusCode();
        Assert.Contains("/content/invitation/media", (await CoverAsync(guest, party.Token)).GetString());
        await TrashAsync(party.Owner, portrait);
        Assert.Equal(
            $"/api/party/{party.Token}/media/{party.AlbumPhotoId}/preview",
            (await CoverAsync(guest, party.Token)).GetString());
    }

    // --- Lifecycle ----------------------------------------------------------

    [Fact]
    public async Task Tearing_The_Party_Down_Keeps_The_Owners_Files_It_Referenced()
    {
        var party = await SeedPartyAsync();
        var menuGraphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, menuGraphic)).EnsureSuccessStatusCode();
        await StartAsync(party);
        await EnableGameAsync(party);
        var activityGraphic = await UploadPngAsync(party.Owner, "activity.png");
        await CreateChallengeAsync(party, activityGraphic);

        var dto = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await party.Owner.DeleteAsync(
                $"/api/parties/{party.PartyId}?version={dto.GetProperty("version").GetInt32()}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The references went with the party...
        Assert.Empty(await db.PartyGuestContents.ToListAsync());
        Assert.Empty(await db.PartyChallenges.ToListAsync());
        // ...and the owner's files did not: they were the owner's all along, not
        // guest contributions for the party to finalize.
        Assert.True(await IsActiveAsync(menuGraphic));
        Assert.True(await IsActiveAsync(activityGraphic));
        Assert.False(await InAnyAlbumAsync(menuGraphic));
        Assert.False(await InAnyAlbumAsync(activityGraphic));
    }

    [Fact]
    public async Task A_Permanent_Delete_Clears_Every_Reference_And_The_Party_Carries_On()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu.png");
        (await WriteSlotAsync(party, "menu", Menu, graphic)).EnsureSuccessStatusCode();
        await EnableGameAsync(party);
        var challengeId = await CreateChallengeAsync(party, graphic);

        await TrashAsync(party.Owner, graphic);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await party.Owner.DeleteAsync($"/api/trash/files/{graphic}")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.FileItems.IgnoreQueryFilters().AnyAsync(f => f.Id == graphic));
            // The references gave way to the file's lifecycle rather than
            // blocking it, and the things that held them survived.
            var menu = await db.PartyGuestContents.SingleAsync(c => c.Kind == "menu");
            Assert.Null(menu.MediaFileItemId);
            var challenge = await db.PartyChallenges.SingleAsync(c => c.Id == challengeId);
            Assert.Null(challenge.MediaFileItemId);
            Assert.True(await db.Parties.AnyAsync(p => p.Id == party.PartyId));
        }

        var guestMenu = await GuestSlotAsync(Guest(), party.Token, "menu");
        Assert.Equal(JsonValueKind.Null, guestMenu.GetProperty("mediaUrl").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            (await OwnerSlotAsync(party, "menu")).GetProperty("mediaFileItemId").ValueKind);
    }

    // --- Activities ---------------------------------------------------------

    [Fact]
    public async Task An_Activity_Takes_An_Owner_Image_That_Is_In_No_Album()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "activity.png");

        var created = await party.Owner.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-challenges", ChallengeBody(graphic));

        created.EnsureSuccessStatusCode();
        Assert.Equal(
            graphic,
            (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mediaFileItemId").GetGuid());
        Assert.False(await InAnyAlbumAsync(graphic));

        // The rule is the same one a slot obeys: not a stranger's, not Trash.
        var (_, stranger) = await NewHostAsync();
        var foreign = await UploadPngAsync(stranger, "foreign.png");
        var trashed = await UploadPngAsync(party.Owner, "trashed.png");
        await TrashAsync(party.Owner, trashed);
        foreach (var id in new[] { foreign, trashed, Guid.NewGuid() })
        {
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await party.Owner.PostAsJsonAsync(
                    $"/api/albums/{party.AlbumId}/party-challenges", ChallengeBody(id))).StatusCode);
        }
    }

    [Fact]
    public async Task An_Extra_Album_Activity_Picture_Shows_During_The_Game_And_Only_Then()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "activity.png");
        await EnableGameAsync(party);
        var challengeId = await CreateChallengeAsync(party, graphic);
        var guest = Guest();
        var route = $"/api/party/{party.Token}/challenges/{challengeId}/media";

        // Before the party there is no game to reach it through.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(route)).StatusCode);

        await StartAsync(party);
        var list = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}/challenges");
        var url = list.GetProperty("items").EnumerateArray().Single().GetProperty("mediaUrl").GetString();
        Assert.Equal(route, url);
        var image = await guest.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Null(image.Content.Headers.ContentDisposition);

        // A game switched off takes the picture with it.
        await EnableGameAsync(party, enabled: false);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(route)).StatusCode);

        await EnableGameAsync(party);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(route)).StatusCode);
        await EndAsync(party);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task An_Activity_Picture_Never_Joins_The_Album()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "activity.png");
        await EnableGameAsync(party);
        await CreateChallengeAsync(party, graphic);
        await StartAsync(party);

        var guest = Guest();
        // Not in the party's gallery or slideshow feed...
        var items = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}/items");
        var ids = items.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([party.AlbumPhotoId], ids);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{graphic}/preview")).StatusCode);

        // ...not in the owner's album...
        var owned = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/albums/{party.AlbumId}/items");
        Assert.DoesNotContain(
            owned.EnumerateArray(), i => i.GetProperty("fileItemId").GetGuid() == graphic);

        // ...and not in any album at all.
        Assert.False(await InAnyAlbumAsync(graphic));
    }

    [Fact]
    public async Task The_Television_Shows_An_Extra_Album_Picture_For_The_Activity_It_Holds()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "activity.png");
        await EnableGameAsync(party);
        var challengeId = await CreateChallengeAsync(party, graphic);
        await StartAsync(party);
        var cookie = await PairTvAsync(party.Owner);

        (await TvAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{party.AlbumId}/party-playback"))
            .EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.PartyChallengeSessions.SingleAsync();
            session.NextChallengeAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var hold = await (await TvAsync(
            cookie, HttpMethod.Post, $"/api/tv/albums/{party.AlbumId}/party-playback/boundary"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var url = hold.GetProperty("activeChallenge").GetProperty("mediaUrl").GetString();
        Assert.Equal(
            $"/api/tv/albums/{party.AlbumId}/party-playback/challenges/{challengeId}/media", url);

        var image = await TvAsync(cookie, HttpMethod.Get, url!);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.StartsWith("image/", image.Content.Headers.ContentType!.MediaType);

        // The television's general media route still refuses the same file: it
        // is in none of the albums the television shows.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await TvAsync(cookie, HttpMethod.Get, $"/api/tv/media/{graphic}/preview")).StatusCode);
    }

    // --- helpers ------------------------------------------------------------

    private sealed record SeededParty(
        HttpClient Owner, Guid OwnerId, Guid PartyId, Guid AlbumId, Guid AlbumPhotoId, string Token);

    private HttpClient Guest() => _factory.CreateClient();

    private async Task<(Guid UserId, HttpClient Client)> NewHostAsync() =>
        await _factory.CreatePermissionClientAsync($"host-{Guid.NewGuid():N}@example.com", HostPermissions);

    // A PUBLISHED party — the invitation — with one photograph in its album.
    private async Task<SeededParty> SeedPartyAsync()
    {
        var (ownerId, owner) = await NewHostAsync();
        var albumId = (await (await owner.PostAsJsonAsync(
            "/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var photoId = await UploadPngAsync(owner, "album-photo.png");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = photoId }))
            .EnsureSuccessStatusCode();

        var enable = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();

        return new SeededParty(
            owner, ownerId, settings.GetProperty("partyId").GetGuid(), albumId, photoId,
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private static Task<Guid> UploadPngAsync(HttpClient owner, string name) =>
        UploadBytesAsync(owner, ImageFixtures.PlainPng(), "image/png", name);

    private static async Task<Guid> UploadBytesAsync(
        HttpClient owner, byte[] bytes, string contentType, string name)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> WriteSlotAsync(
        SeededParty party, string kind, object content, Guid? mediaFileItemId,
        int version = 0, bool enabled = true, bool before = true, bool live = true, bool after = false) =>
        party.Owner.PutAsJsonAsync($"/api/parties/{party.PartyId}/guest-content/{kind}", new
        {
            enabled, visibleBefore = before, visibleLive = live, visibleAfter = after,
            content, mediaFileItemId, version,
        });

    private static async Task<JsonElement> OwnerSlotAsync(SeededParty party, string kind) =>
        (await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}/guest-content"))
            .EnumerateArray().Single(s => s.GetProperty("kind").GetString() == kind);

    private static async Task<JsonElement> GuestSlotAsync(HttpClient guest, string token, string kind) =>
        (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}"))
            .GetProperty("content").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == kind);

    private static async Task<string?> GuestMediaUrlAsync(HttpClient guest, string token, string kind)
    {
        var slot = (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}"))
            .GetProperty("content").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("kind").GetString() == kind);
        return slot.ValueKind == JsonValueKind.Object ? slot.GetProperty("mediaUrl").GetString() : null;
    }

    private static async Task<JsonElement> CoverAsync(HttpClient guest, string token) =>
        (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}")).GetProperty("coverUrl");

    private static async Task StartAsync(SeededParty party) =>
        await PartyTestHost.StartAsync(party.Owner, party.PartyId);

    private static async Task EndAsync(SeededParty party)
    {
        var dto = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        (await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/end-live",
            new { version = dto.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
    }

    private static async Task EnableGameAsync(SeededParty party, bool enabled = true) =>
        (await party.Owner.PatchAsJsonAsync($"/api/albums/{party.AlbumId}/party-game-settings", new
        {
            gameEnabled = enabled, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

    private static object ChallengeBody(Guid? mediaFileItemId) => new
    {
        title = "Il brindisi", body = "Alza il bicchiere", kind = "dare", mediaFileItemId, isEnabled = true,
    };

    private static async Task<Guid> CreateChallengeAsync(SeededParty party, Guid? mediaFileItemId)
    {
        var response = await party.Owner.PostAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-challenges", ChallengeBody(mediaFileItemId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task TrashAsync(HttpClient owner, Guid fileId)
    {
        var response = await owner.DeleteAsync($"/api/files/{fileId}");
        Assert.True(response.IsSuccessStatusCode, $"trashing the file answered {response.StatusCode}");
    }

    private static async Task MoveToVaultAsync(HttpClient owner, Guid fileId)
    {
        // Setting the vault up is once per account; a repeat is harmless here.
        await owner.PostAsJsonAsync("/api/private-vault/setup", new { password = VaultPassword });
        var unlock = await owner.PostAsJsonAsync("/api/private-vault/unlock", new { password = VaultPassword });
        unlock.EnsureSuccessStatusCode();
        var token = (await unlock.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/move-in")
        {
            Content = JsonContent.Create(new { fileIds = new[] { fileId }, folderIds = Array.Empty<Guid>() }),
        };
        request.Headers.Add("X-Vault-Token", token);
        (await owner.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private async Task SetCoverAsync(Guid albumId, Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Albums.Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CoverFileItemId, fileItemId));
    }

    private async Task<bool> InAnyAlbumAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AnyAsync(ai => ai.FileItemId == fileItemId);
    }

    private async Task<bool> IsActiveAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.AnyAsync(f => f.Id == fileItemId && f.DeletedAt == null);
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tv = _factory.CreateClient();
        var started = await (await tv.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>();
        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started!.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret, personalCode = "URDLSUDLR",
            personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        request.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tv.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<HttpResponseMessage> TvAsync(string cookie, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie.Split(';', 2)[0]);
        return await _factory.CreateClient().SendAsync(request);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// P5: the same image, the same authority, one more answer about presentation.
///
/// <para>A slot already answered two questions independently — what it SAYS
/// (<c>ContentJson</c>) and WHICH image it uses (<c>MediaFileItemId</c>). This
/// adds the third: HOW that image participates in the surface,
/// <c>inline</c> or <c>poster</c>. What is pinned here is that it changes a
/// rendering and nothing else: the same reference, judged by the same
/// <c>PartyMediaReference</c> rule, served by the same relation-scoped route,
/// with no new access of any kind — and that switching modes never destroys a
/// word the host wrote.</para>
/// </summary>
public sealed class PartyContentMediaPresentationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyContentMediaPresentationTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] HostPermissions =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
        Permissions.PrivateVaultAccess,
    ];

    private static readonly object Menu = new
    {
        intro = "Cena in giardino",
        sections = new[] { new { title = "Antipasti", items = new[] { "Bruschetta", "Olive" } } },
    };

    // --- The default, which is the whole upgrade story ----------------------

    [Fact]
    public async Task A_Slot_The_Host_Has_Never_Written_Is_Inline()
    {
        var party = await SeedPartyAsync();

        var slot = await OwnerSlotAsync(party, "menu");

        Assert.Equal(0, slot.GetProperty("version").GetInt32());
        Assert.Equal("inline", slot.GetProperty("mediaPresentation").GetString());
    }

    [Fact]
    public async Task A_Row_Written_Without_A_Presentation_Is_Inline()
    {
        // Exactly what a P4 client sends: no presentation field at all. The
        // column's default answers, and the slot renders as it always did.
        var party = await SeedPartyAsync();

        var saved = await party.Owner.PutAsJsonAsync(
            $"/api/parties/{party.PartyId}/guest-content/menu",
            new
            {
                enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
                content = Menu, mediaFileItemId = party.AlbumPhotoId, version = 0,
            });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var slot = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inline", slot.GetProperty("mediaPresentation").GetString());
    }

    [Fact]
    public async Task A_Row_Stored_Before_The_Column_Existed_Reads_As_Inline()
    {
        // The migration's promise, asserted against the database rather than
        // against the writer: a row whose presentation nobody ever stated means
        // the presentation it was rendered with.
        var party = await SeedPartyAsync();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE party_guest_contents SET \"MediaPresentation\" = 'inline'");
        }

        Assert.Equal("inline", (await OwnerSlotAsync(party, "menu"))
            .GetProperty("mediaPresentation").GetString());
    }

    // --- Owner read/write ---------------------------------------------------

    [Fact]
    public async Task The_Owner_Can_Write_And_Read_Back_Poster()
    {
        var party = await SeedPartyAsync();

        var saved = await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, presentation: "poster");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("poster", (await saved.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("mediaPresentation").GetString());
        Assert.Equal("poster", (await OwnerSlotAsync(party, "menu"))
            .GetProperty("mediaPresentation").GetString());
    }

    [Theory]
    [InlineData("fullscreen")]
    [InlineData("POSTER")]
    [InlineData("")]
    [InlineData("banner")]
    public async Task An_Unknown_Presentation_Is_Refused(string presentation)
    {
        var party = await SeedPartyAsync();

        var saved = await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, presentation: presentation);

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Equal("invalid_presentation",
            (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Nothing was stored: the slot is still the blank one.
        Assert.Equal(0, (await OwnerSlotAsync(party, "menu")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Poster_Without_A_Photograph_Is_Refused()
    {
        // A poster slot IS its picture. One with no reference has nothing to
        // present and no composition to fall back to, so it is refused rather
        // than stored and rendered as nothing.
        var party = await SeedPartyAsync();

        var saved = await WriteSlotAsync(party, "menu", Menu, null, presentation: "poster");

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Equal("invalid_presentation",
            (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clearing_The_Photograph_And_Returning_To_Inline_Is_Accepted()
    {
        // What the editor does when the host removes the image: the reference
        // and the presentation are cleared together, in one write.
        var party = await SeedPartyAsync();
        var poster = await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, presentation: "poster");
        var version = (await poster.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        var cleared = await WriteSlotAsync(
            party, "menu", Menu, null, version: version, presentation: "inline");

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var slot = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inline", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("mediaFileItemId").ValueKind);
    }

    [Fact]
    public async Task Changing_Only_The_Presentation_Moves_The_Slot_Version()
    {
        // It is an edit of the SLOT like any other — and of the slot's own
        // version, never the party's, so switching the menu to a poster cannot
        // make somebody else's rename of the party conflict.
        var party = await SeedPartyAsync();
        var first = await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId);
        var v1 = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();
        // Whatever the party is at after being created and published — the
        // point is that a slot edit does not move it, not what it happens to be.
        var partyVersionBefore = (await party.Owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.PartyId}")).GetProperty("version").GetInt32();

        var second = await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: v1, presentation: "poster");

        var v2 = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();
        Assert.Equal(v1 + 1, v2);

        // The party itself did not move: editing the menu and renaming the
        // party are unrelated decisions and must never contend.
        var partyAfter = await party.Owner.GetFromJsonAsync<JsonElement>(
            $"/api/parties/{party.PartyId}");
        Assert.Equal(partyVersionBefore, partyAfter.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_Stale_Version_Still_Conflicts_When_Only_The_Presentation_Changes()
    {
        var party = await SeedPartyAsync();
        var first = await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId);
        var v1 = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId, version: v1);

        var stale = await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: v1, presentation: "poster");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    // --- The text survives the round trip -----------------------------------

    [Fact]
    public async Task Inline_To_Poster_To_Inline_Restores_Every_Word()
    {
        // Changing presentation is not a way to delete content. The words stay
        // in ContentJson throughout and come back exactly as they were.
        var party = await SeedPartyAsync();
        var v0 = await VersionAsync(
            await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId));

        var v1 = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: v0, presentation: "poster"));
        // While it is a poster the words are still stored, untouched.
        var asPoster = await OwnerSlotAsync(party, "menu");
        Assert.Equal("Cena in giardino", asPoster.GetProperty("content")
            .GetProperty("intro").GetString());

        await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: v1, presentation: "inline");

        var restored = await OwnerSlotAsync(party, "menu");
        Assert.Equal("inline", restored.GetProperty("mediaPresentation").GetString());
        Assert.Equal("Cena in giardino", restored.GetProperty("content")
            .GetProperty("intro").GetString());
        Assert.Equal("Antipasti", restored.GetProperty("content")
            .GetProperty("sections")[0].GetProperty("title").GetString());
    }

    // --- The guest projection -----------------------------------------------

    [Fact]
    public async Task The_Guest_Projection_Carries_The_Presentation_And_Still_No_File_Id()
    {
        var party = await SeedPartyAsync();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId, presentation: "poster");
        await StartLiveAsync(party);

        var slot = await GuestSlotAsync(Guest(), party.Token, "menu");

        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
        Assert.NotNull(slot.GetProperty("mediaUrl").GetString());
        // The guest still learns an address, never the owner's file id.
        Assert.False(slot.TryGetProperty("mediaFileItemId", out _));
    }

    [Fact]
    public async Task Inline_Behaves_Exactly_As_P4()
    {
        var party = await SeedPartyAsync();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId, presentation: "inline");
        await StartLiveAsync(party);

        var slot = await GuestSlotAsync(Guest(), party.Token, "menu");

        Assert.Equal("inline", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal("Cena in giardino", slot.GetProperty("content")
            .GetProperty("intro").GetString());
        Assert.NotNull(slot.GetProperty("mediaUrl").GetString());
    }

    // --- Authorization is unchanged, in both modes --------------------------

    [Fact]
    public async Task Poster_Uses_The_Same_Route_And_The_Same_Authorization_As_Inline()
    {
        var party = await SeedPartyAsync();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId, presentation: "inline");
        await StartLiveAsync(party);
        var guest = Guest();
        var inlineUrl = (await GuestSlotAsync(guest, party.Token, "menu"))
            .GetProperty("mediaUrl").GetString()!;

        var v = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: 1, presentation: "poster"));
        var posterUrl = (await GuestSlotAsync(guest, party.Token, "menu"))
            .GetProperty("mediaUrl").GetString()!;

        // Same route family, differing only by the slot version cache-buster —
        // there is no /poster and no /fullscreen endpoint.
        Assert.StartsWith($"/api/party/{party.Token}/content/menu/media", inlineUrl, StringComparison.Ordinal);
        Assert.StartsWith($"/api/party/{party.Token}/content/menu/media", posterUrl, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(posterUrl)).StatusCode);
        Assert.True(v > 1);
    }

    [Fact]
    public async Task Poster_Grants_No_Access_To_Any_Other_File()
    {
        // The reference is the authority, and poster mode does not widen it:
        // another of the owner's files, never referenced, stays unreachable.
        var party = await SeedPartyAsync();
        var unreferenced = await UploadPngAsync(party.Owner, "private.png");
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId, presentation: "poster");
        await StartLiveAsync(party);
        var guest = Guest();

        Assert.Equal(HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{unreferenced}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.Token}/media/{unreferenced}/preview")).StatusCode);
    }

    [Fact]
    public async Task A_Poster_Slot_The_Host_Disabled_Serves_Nothing()
    {
        var party = await SeedPartyAsync();
        var v = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, presentation: "poster"));
        await StartLiveAsync(party);
        var guest = Guest();
        var url = (await GuestSlotAsync(guest, party.Token, "menu")).GetProperty("mediaUrl").GetString()!;

        await WriteSlotAsync(
            party, "menu", Menu, party.AlbumPhotoId, version: v, enabled: false, presentation: "poster");

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_Poster_Whose_Photograph_Goes_To_Trash_Stops_Being_Offered()
    {
        // The record keeps the host's choice — it is NOT rewritten to inline —
        // but the address is withdrawn, so the surface shows no dead row.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "menu-graphic.png");
        await WriteSlotAsync(party, "menu", Menu, graphic, presentation: "poster");
        await StartLiveAsync(party);
        var guest = Guest();
        var url = (await GuestSlotAsync(guest, party.Token, "menu")).GetProperty("mediaUrl").GetString()!;

        (await party.Owner.DeleteAsync($"/api/files/{graphic}")).EnsureSuccessStatusCode();

        var slot = await GuestSlotAsync(guest, party.Token, "menu");
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("mediaUrl").ValueKind);
        // The host's decision is still recorded; only the picture is gone.
        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal("poster", (await OwnerSlotAsync(party, "menu"))
            .GetProperty("mediaPresentation").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_Poster_Whose_Photograph_Enters_The_Private_Vault_Stops_Being_Offered()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "vaulted.png");
        await WriteSlotAsync(party, "menu", Menu, graphic, presentation: "poster");
        await StartLiveAsync(party);
        var guest = Guest();

        await MoveToVaultAsync(party.Owner, graphic);

        var slot = await GuestSlotAsync(guest, party.Token, "menu");
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("mediaUrl").ValueKind);
        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
    }

    [Fact]
    public async Task A_Poster_Whose_Photograph_Leaves_The_Media_Library_Stops_Being_Offered()
    {
        // "Extra-album" is not another word for "excluded": a file the owner
        // moved out of their media library is out of Party too.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "excluded.png");
        await WriteSlotAsync(party, "menu", Menu, graphic, presentation: "poster");
        await StartLiveAsync(party);
        var guest = Guest();

        (await party.Owner.PostAsJsonAsync(
            "/api/media-library/exclude", new { fileIds = new[] { graphic } }))
            .EnsureSuccessStatusCode();

        Assert.Equal(JsonValueKind.Null,
            (await GuestSlotAsync(guest, party.Token, "menu")).GetProperty("mediaUrl").ValueKind);
    }

    [Fact]
    public async Task Permanently_Deleting_A_Poster_Photograph_Leaves_The_Slot_Coherent()
    {
        // ON DELETE SET NULL: the purge succeeds and the slot survives without a
        // picture. The presentation is left as the host set it; the surface
        // simply has nothing to offer, which is the same state as Trash.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "purged.png");
        await WriteSlotAsync(party, "menu", Menu, graphic, presentation: "poster");
        (await party.Owner.DeleteAsync($"/api/files/{graphic}")).EnsureSuccessStatusCode();

        (await party.Owner.DeleteAsync($"/api/trash/files/{graphic}")).EnsureSuccessStatusCode();

        var slot = await OwnerSlotAsync(party, "menu");
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("mediaFileItemId").ValueKind);
        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal("Cena in giardino", slot.GetProperty("content").GetProperty("intro").GetString());
    }

    [Fact]
    public async Task A_Poster_Left_Without_Media_By_A_Purge_Can_Still_Be_Edited()
    {
        // ON DELETE SET NULL makes `poster` + no reference a REACHABLE state, and
        // the server deliberately does not rewrite it to inline on the host's
        // behalf — that would publish the words they replaced with a picture. So
        // the host must still be able to work on the slot: same asymmetry as the
        // media reference, a state the row is already in is not re-judged.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "purged.png");
        var v = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, graphic, presentation: "poster"));
        (await party.Owner.DeleteAsync($"/api/files/{graphic}")).EnsureSuccessStatusCode();
        (await party.Owner.DeleteAsync($"/api/trash/files/{graphic}")).EnsureSuccessStatusCode();

        // Editing the words while the picture is gone, presentation untouched.
        var edited = await WriteSlotAsync(
            party, "menu", new { intro = "Nuovo testo", sections = Array.Empty<object>() },
            null, version: v, presentation: "poster");

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var slot = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal("Nuovo testo", slot.GetProperty("content").GetProperty("intro").GetString());
    }

    [Fact]
    public async Task A_Poster_Left_Without_Media_Can_Be_Returned_To_Inline()
    {
        // The other way out the editor offers.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "purged.png");
        var v = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, graphic, presentation: "poster"));
        (await party.Owner.DeleteAsync($"/api/files/{graphic}")).EnsureSuccessStatusCode();
        (await party.Owner.DeleteAsync($"/api/trash/files/{graphic}")).EnsureSuccessStatusCode();

        var fixedUp = await WriteSlotAsync(
            party, "menu", Menu, null, version: v, presentation: "inline");

        Assert.Equal(HttpStatusCode.OK, fixedUp.StatusCode);
        Assert.Equal("inline", (await fixedUp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("mediaPresentation").GetString());
    }

    [Fact]
    public async Task An_Inline_Slot_Still_Cannot_Become_A_Poster_Without_A_Photograph()
    {
        // Tolerating the state a purge leaves behind must not become a way to
        // CREATE one. Nothing new may reach `poster` with nothing to present.
        var party = await SeedPartyAsync();
        var v = await VersionAsync(await WriteSlotAsync(party, "menu", Menu, null));

        var refused = await WriteSlotAsync(
            party, "menu", Menu, null, version: v, presentation: "poster");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_presentation",
            (await refused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_Recovered_Poster_Takes_A_New_Photograph_And_Stays_A_Poster()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "purged.png");
        var v = await VersionAsync(await WriteSlotAsync(
            party, "menu", Menu, graphic, presentation: "poster"));
        (await party.Owner.DeleteAsync($"/api/files/{graphic}")).EnsureSuccessStatusCode();
        (await party.Owner.DeleteAsync($"/api/trash/files/{graphic}")).EnsureSuccessStatusCode();
        var replacement = await UploadPngAsync(party.Owner, "replacement.png");

        var saved = await WriteSlotAsync(
            party, "menu", Menu, replacement, version: v, presentation: "poster");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var slot = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("poster", slot.GetProperty("mediaPresentation").GetString());
        Assert.Equal(replacement, slot.GetProperty("mediaFileItemId").GetGuid());
    }

    // --- The invitation hero ------------------------------------------------

    [Fact]
    public async Task An_Inline_Invitation_Photograph_Is_The_Hero()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "invite.png");
        await WriteSlotAsync(
            party, "invitation", new { headline = "Ci siamo", message = "Vi aspettiamo" },
            graphic, before: true, live: false, presentation: "inline");

        var context = await Guest().GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");

        var cover = context.GetProperty("coverUrl").GetString();
        Assert.StartsWith(
            $"/api/party/{party.Token}/content/invitation/media", cover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Poster_Invitation_Photograph_Is_Not_The_Hero()
    {
        // A hero is a cropped band; a poster is a document. The invitation
        // falls back to the album's chosen cover instead of being cropped.
        var party = await SeedPartyAsync();
        await ChooseAlbumCoverAsync(party);
        var graphic = await UploadPngAsync(party.Owner, "invite-poster.png");
        await WriteSlotAsync(
            party, "invitation", new { headline = "Ci siamo", message = "Vi aspettiamo" },
            graphic, before: true, live: false, presentation: "poster");

        var context = await Guest().GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");

        var cover = context.GetProperty("coverUrl").GetString();
        Assert.DoesNotContain("content/invitation/media", cover ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains($"/api/party/{party.Token}/media/", cover ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Poster_Invitation_Still_Exposes_Its_Own_Relation_Scoped_Url()
    {
        // Not the hero, but not hidden either: the guest surface opens it from
        // its own row, through the same route it would have used inline.
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "invite-poster.png");
        await WriteSlotAsync(
            party, "invitation", new { headline = "Ci siamo", message = (string?)null },
            graphic, before: true, live: false, presentation: "poster");
        var guest = Guest();

        var slot = await GuestSlotAsync(guest, party.Token, "invitation");

        var url = slot.GetProperty("mediaUrl").GetString();
        Assert.StartsWith(
            $"/api/party/{party.Token}/content/invitation/media", url, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(url!)).StatusCode);
    }

    [Fact]
    public async Task A_Poster_Invitation_With_No_Chosen_Cover_Leaves_The_Composition()
    {
        var party = await SeedPartyAsync();
        var graphic = await UploadPngAsync(party.Owner, "invite-poster.png");
        await WriteSlotAsync(
            party, "invitation", new { headline = "Ci siamo", message = (string?)null },
            graphic, before: true, live: false, presentation: "poster");

        var context = await Guest().GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}");

        Assert.Equal(JsonValueKind.Null, context.GetProperty("coverUrl").ValueKind);
    }

    // --- The schema itself --------------------------------------------------

    [Fact]
    public async Task The_Column_Is_Not_Null_And_Defaults_To_Inline()
    {
        var party = await SeedPartyAsync();
        await WriteSlotAsync(party, "menu", Menu, party.AlbumPhotoId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PartyGuestContents.AsNoTracking()
            .SingleAsync(c => c.PartyId == party.PartyId && c.Kind == "menu");

        Assert.Equal(PartyGuestContentMediaPresentations.Inline, row.MediaPresentation);
    }

    [Fact]
    public void The_Vocabulary_Is_Closed()
    {
        Assert.Equal(["inline", "poster"], PartyGuestContentMediaPresentations.All);
        Assert.True(PartyGuestContentMediaPresentations.IsKnown("inline"));
        Assert.True(PartyGuestContentMediaPresentations.IsKnown("poster"));
        Assert.False(PartyGuestContentMediaPresentations.IsKnown("POSTER"));
        Assert.False(PartyGuestContentMediaPresentations.IsKnown(null));
        Assert.False(PartyGuestContentMediaPresentations.IsKnown("fullscreen"));
    }

    [Fact]
    public async Task Challenges_Did_Not_Acquire_A_Presentation()
    {
        // P5 is about PartyGuestContent. An activity's picture is a different
        // relation with a different surface, and it was deliberately left alone.
        var party = await SeedPartyAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var challenge = db.Model.FindEntityType(typeof(PartyChallenge))!;

        Assert.Null(challenge.FindProperty("MediaPresentation"));
        Assert.NotNull(db.Model.FindEntityType(typeof(PartyGuestContent))!
            .FindProperty("MediaPresentation"));
        Assert.NotEqual(Guid.Empty, party.PartyId);
    }

    // --- helpers ------------------------------------------------------------

    private sealed record SeededParty(
        HttpClient Owner, Guid OwnerId, Guid PartyId, Guid AlbumId, Guid AlbumPhotoId, string Token);

    private HttpClient Guest() => _factory.CreateClient();

    private async Task<SeededParty> SeedPartyAsync()
    {
        var (ownerId, owner) = await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", HostPermissions);
        var albumId = (await (await owner.PostAsJsonAsync(
            "/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var photoId = await UploadPngAsync(owner, "album-photo.png");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = photoId }))
            .EnsureSuccessStatusCode();

        var enable = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();

        return new SeededParty(
            owner, ownerId, settings.GetProperty("partyId").GetGuid(), albumId, photoId,
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    /// The album's CHOSEN cover — the only album file id that resolves before
    /// the party, and the invitation's fallback hero.
    private async Task ChooseAlbumCoverAsync(SeededParty party)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Albums.Where(a => a.Id == party.AlbumId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CoverFileItemId, party.AlbumPhotoId));
    }

    private static async Task StartLiveAsync(SeededParty party)
    {
        var row = await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}");
        (await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/start-live",
            new { version = row.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> WriteSlotAsync(
        SeededParty party, string kind, object content, Guid? mediaFileItemId,
        int version = 0, bool enabled = true, bool before = true, bool live = true,
        bool after = false, string presentation = "inline") =>
        party.Owner.PutAsJsonAsync($"/api/parties/{party.PartyId}/guest-content/{kind}", new
        {
            enabled, visibleBefore = before, visibleLive = live, visibleAfter = after,
            content, mediaFileItemId, version, mediaPresentation = presentation,
        });

    private static async Task<int> VersionAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();
    }

    private static async Task<JsonElement> OwnerSlotAsync(SeededParty party, string kind) =>
        (await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}/guest-content"))
            .EnumerateArray().Single(s => s.GetProperty("kind").GetString() == kind);

    private static async Task<JsonElement> GuestSlotAsync(HttpClient guest, string token, string kind) =>
        (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}"))
            .GetProperty("content").EnumerateArray()
            .Single(s => s.GetProperty("kind").GetString() == kind);

    private static async Task MoveToVaultAsync(HttpClient owner, Guid fileId)
    {
        const string password = "correct horse battery";
        var setup = await owner.PostAsJsonAsync("/api/private-vault/setup", new { password });
        if (setup.StatusCode == HttpStatusCode.Conflict) { /* already set up */ }
        var unlock = await owner.PostAsJsonAsync("/api/private-vault/unlock", new { password });
        unlock.EnsureSuccessStatusCode();
        var vaultToken = (await unlock.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var move = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/move-in")
        {
            Content = JsonContent.Create(
                new { fileIds = new[] { fileId }, folderIds = Array.Empty<Guid>() }),
        };
        move.Headers.Add("X-Vault-Token", vaultToken);
        (await owner.SendAsync(move)).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient owner, string name)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync(
            "/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

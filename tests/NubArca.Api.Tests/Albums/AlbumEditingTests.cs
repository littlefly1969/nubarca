using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-ALBUM-03: the Editor role, collaborative curation, and the optimistic
// concurrency that makes two people editing one album safe.
//
// Two invariants run through every test here:
//   * an Editor CURATES but never GOVERNS — no invites, no roles, no revoke,
//     no allowDownload, no deleting the album, and never another user's file;
//   * a losing writer changes NOTHING. A version conflict leaves no partial
//     mutation and no audit entry behind.
public sealed class AlbumEditingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumEditingTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string EditorEmail = "bob@example.com";
    private const string OtherEmail = "carol@example.com";

    // ── Editor curation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Editor_Can_Change_The_Title_And_Description()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var version = await VersionAsync(editor, albumId);
        var response = await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = version, name = "Trip 2026", description = "Liguria" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Trip 2026", body.GetProperty("name").GetString());
        Assert.Equal("Liguria", body.GetProperty("description").GetString());
        // The new version comes back so a client can chain edits.
        Assert.Equal(version + 1, body.GetProperty("version").GetInt32());

        // And the OWNER sees it through their own route.
        var owned = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Trip 2026", owned.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Editor_Can_Reorder_And_Every_Surface_Follows()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png", "c.png" })
        {
            await AddOwnPngAsync(owner, albumId, n);
        }
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");

        var before = await SharedItemIdsAsync(editor, albumId);
        Assert.Equal(3, before.Count);
        var reversed = before.AsEnumerable().Reverse().ToList();

        var response = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/order",
            new { expectedVersion = await VersionAsync(editor, albumId), albumItemIds = reversed });
        response.EnsureSuccessStatusCode();

        // The curated order is what the members see…
        Assert.Equal(reversed, await SharedItemIdsAsync(editor, albumId));
        Assert.Equal(reversed, await SharedItemIdsAsync(carol, albumId));
        // …and what the curator's moderation view shows.
        var content = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/content");
        Assert.Equal(reversed, content.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("albumItemId").GetGuid()).ToList());

        // Normalized to a contiguous 1..n regardless of what was sent.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orders = await db.AlbumItems.Where(ai => ai.AlbumId == albumId)
            .Select(ai => ai.SortOrder).OrderBy(o => o).ToListAsync();
        Assert.Equal([1, 2, 3], orders);
    }

    [Fact]
    public async Task A_Reorder_Must_Be_Exactly_The_Current_Items()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png" }) await AddOwnPngAsync(owner, albumId, n);
        var otherAlbum = await CreateAlbumAsync(owner, "Other");
        await AddOwnPngAsync(owner, otherAlbum, "x.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var ids = await SharedItemIdsAsync(editor, albumId);
        var foreignId = (await ItemIdsOfAsync(otherAlbum)).Single();

        // Partial, duplicated, and foreign lists are all refused rather than
        // interpreted — guessing the rest is how two reorders silently produce
        // a third order nobody asked for.
        foreach (var bad in new[]
                 {
                     new[] { ids[0] },                      // omission
                     new[] { ids[0], ids[0] },              // duplicate
                     new[] { ids[0], foreignId },           // foreign album
                     Array.Empty<Guid>(),                   // empty
                 })
        {
            var response = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/order",
                new { expectedVersion = await VersionAsync(editor, albumId), albumItemIds = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The order and the version are untouched by every refusal.
        Assert.Equal(ids, await SharedItemIdsAsync(editor, albumId));
    }

    [Fact]
    public async Task Editor_Can_Set_And_Clear_The_Cover()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var first = await AddOwnPngAsync(owner, albumId, "a.png");
        var second = await AddOwnPngAsync(owner, albumId, "b.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var set = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = await VersionAsync(editor, albumId), fileItemId = second });
        set.EnsureSuccessStatusCode();
        Assert.Equal(second,
            (await set.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("coverFileItemId").GetGuid());

        var content = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/content");
        var covered = content.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("isCover").GetBoolean())
            .Select(i => i.GetProperty("fileItemId").GetGuid()).ToList();
        Assert.Equal([second], covered);

        var cleared = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = await VersionAsync(editor, albumId), fileItemId = (Guid?)null });
        cleared.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("coverFileItemId").ValueKind);
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public async Task A_Cover_Must_Be_A_Current_Servable_Member()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        // A file that exists but is NOT in this album — naming it must not work,
        // or the cover would become a way to point at arbitrary media.
        var outside = await UploadPngAsync(owner, "outside.png");

        var response = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = await VersionAsync(editor, albumId), fileItemId = outside });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Removing_The_Cover_Item_Clears_The_Choice_And_Compacts_The_Order()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png", "c.png" }) await AddOwnPngAsync(owner, albumId, n);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var items = await SharedItemsAsync(editor, albumId);
        var coverFile = items[1].GetProperty("fileItemId").GetGuid();
        var coverItemId = items[1].GetProperty("albumItemId").GetGuid();
        (await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = await VersionAsync(editor, albumId), fileItemId = coverFile }))
            .EnsureSuccessStatusCode();

        var removed = await editor.DeleteAsync(
            $"/api/shared-albums/{albumId}/items/{coverItemId}" +
            $"?expectedVersion={await VersionAsync(editor, albumId)}");
        removed.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var album = await db.Albums.AsNoTracking().FirstAsync(a => a.Id == albumId);
        // Cleared in the same transaction — no permanently dangling reference.
        Assert.Null(album.CoverFileItemId);
        // …and the order has no hole.
        var orders = await db.AlbumItems.Where(ai => ai.AlbumId == albumId)
            .Select(ai => ai.SortOrder).OrderBy(o => o).ToListAsync();
        Assert.Equal([1, 2], orders);
    }

    [Fact]
    public async Task Editor_Removes_Any_Item_But_Never_The_Source_File()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");
        var carolFile = await UploadPngAsync(carol, "carol.png");
        (await carol.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = carolFile })).EnsureSuccessStatusCode();

        // The Editor removes BOTH the owner's item and another user's
        // contribution.
        foreach (var itemId in await ItemIdsOfAsync(albumId))
        {
            (await editor.DeleteAsync($"/api/shared-albums/{albumId}/items/{itemId}" +
                $"?expectedVersion={await VersionAsync(editor, albumId)}")).EnsureSuccessStatusCode();
        }
        Assert.Empty(await ItemIdsOfAsync(albumId));

        // Both source files survive, each in its own owner's library.
        (await owner.GetAsync($"/api/files/{ownerFile}/content")).EnsureSuccessStatusCode();
        (await carol.GetAsync($"/api/files/{carolFile}/content")).EnsureSuccessStatusCode();
    }

    // ── Editor may NOT govern ───────────────────────────────────────────────

    [Fact]
    public async Task Editor_Cannot_Administer_Members_Or_Delete_The_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAsync(owner, albumId, EditorEmail, "editor");
        await AcceptAsync(editor, membershipId);

        // Governance: every one of these is the Owner's alone.
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.GetAsync($"/api/albums/{albumId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PostAsJsonAsync($"/api/albums/{albumId}/members",
                new { email = OtherEmail, role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
                new { role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
                new { allowOriginalDownload = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}")).StatusCode);

        // Lifecycle: no deleting the album, and no public/TV surfaces.
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.DeleteAsync($"/api/albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings",
                new { showOnTv = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings",
                new { enabled = true })).StatusCode);

        // Nothing changed, and the album still exists.
        Assert.Equal(1, await CountMembershipsAsync(albumId));
        (await owner.GetAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Viewer_And_Contributor_Cannot_Curate()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, bob, albumId, EditorEmail, "viewer");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");

        var ids = await ItemIdsOfAsync(albumId);
        foreach (var client in new[] { bob, carol })
        {
            var v = await VersionAsync(client, albumId);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
                    new { expectedVersion = v, name = "Hijacked" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync($"/api/shared-albums/{albumId}/order",
                    new { expectedVersion = v, albumItemIds = ids })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
                    new { expectedVersion = v, fileItemId = (Guid?)null })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.DeleteAsync(
                    $"/api/shared-albums/{albumId}/items/{ids[0]}?expectedVersion={v}")).StatusCode);
            // And no curator moderation view.
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/albums/{albumId}/content")).StatusCode);
        }

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Trip", detail.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Non_Member_Gets_Not_Found_Rather_Than_Forbidden()
    {
        // 403 would confirm the album exists. A stranger must not learn that.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");

        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
                new { expectedVersion = 1, name = "Hijacked" })).StatusCode);
    }

    [Fact]
    public async Task Owner_Curates_Through_The_Same_Concurrency_Model()
    {
        // The owner is not a special case with its own path: on the
        // collaborative surface they carry a version like everybody else.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");

        var stale = await VersionAsync(owner, albumId);
        (await owner.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = stale, name = "Trip 2026" })).EnsureSuccessStatusCode();

        var conflict = await owner.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = stale, name = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    // ── Concurrency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_Editors_Editing_From_The_Same_Version_Produce_One_Winner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, bob, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "editor");

        var shared = await VersionAsync(bob, albumId);

        var first = await bob.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = shared, name = "Bob's title" });
        var second = await carol.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = shared, name = "Carol's title" });

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The loser learns the CURRENT state, so the client can refresh and
        // explain rather than blindly retry.
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bob's title", body.GetProperty("name").GetString());
        Assert.Equal(shared + 1, body.GetProperty("version").GetInt32());

        // The loser's title was NOT applied.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Bob's title", detail.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Reorder_Loses_To_A_Concurrent_Add_And_Changes_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png" }) await AddOwnPngAsync(owner, albumId, n);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var stale = await VersionAsync(editor, albumId);
        var order = (await ItemIdsOfAsync(albumId)).AsEnumerable().Reverse().ToList();

        // Somebody adds an item after the editor read the version.
        await AddOwnPngAsync(owner, albumId, "c.png");

        var response = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/order",
            new { expectedVersion = stale, albumItemIds = order });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Nothing partial: the album still has three items in their original
        // order, and no reorder audit was written.
        Assert.Equal(3, (await ItemIdsOfAsync(albumId)).Count);
        Assert.Equal(0, await CountAuditAsync("album.edit_reorder"));
    }

    [Fact]
    public async Task A_Removal_Loses_To_A_Concurrent_Removal_And_Changes_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png" }) await AddOwnPngAsync(owner, albumId, n);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var stale = await VersionAsync(editor, albumId);
        var ids = await ItemIdsOfAsync(albumId);

        (await editor.DeleteAsync(
            $"/api/shared-albums/{albumId}/items/{ids[0]}?expectedVersion={stale}"))
            .EnsureSuccessStatusCode();

        // The same command again, with the version it originally read: a retry
        // must not silently remove a second item.
        var retry = await editor.DeleteAsync(
            $"/api/shared-albums/{albumId}/items/{ids[1]}?expectedVersion={stale}");
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Single(await ItemIdsOfAsync(albumId));
    }

    [Fact]
    public async Task Choosing_A_Cover_That_Is_Being_Withdrawn_Fails_Cleanly()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");
        var carolFile = await UploadPngAsync(carol, "carol.png");
        (await carol.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = carolFile })).EnsureSuccessStatusCode();

        var stale = await VersionAsync(editor, albumId);
        // Carol takes it back while the editor still has it on screen.
        (await carol.DeleteAsync($"/api/shared-albums/{albumId}/contributions/{carolFile}"))
            .EnsureSuccessStatusCode();

        var response = await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new { expectedVersion = stale, fileItemId = carolFile });
        // Refused — and specifically NOT with a stale cover written first.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"got {response.StatusCode}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.Albums.AsNoTracking().FirstAsync(a => a.Id == albumId)).CoverFileItemId);
    }

    [Fact]
    public async Task A_Revoked_Or_Downgraded_Editor_Cannot_Complete_A_Pending_Edit()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAsync(owner, albumId, EditorEmail, "editor");
        await AcceptAsync(editor, membershipId);

        // The editor opens a form and reads the version…
        var version = await VersionAsync(editor, albumId);

        // …then the owner demotes them.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
                new { expectedVersion = version, name = "Too late" })).StatusCode);

        // …and then revokes them entirely.
        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
                new { expectedVersion = version, name = "Too late" })).StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Trip", detail.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Membership_Changes_Do_Not_Move_The_Content_Version()
    {
        // Inviting or changing a role changes WHO MAY LOOK, not what is there.
        // Bumping the content version for them would invalidate every open
        // editor's form for a change that does not affect what they are editing.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAsync(owner, albumId, EditorEmail, "editor");
        await AcceptAsync(editor, membershipId);

        var before = await VersionAsync(editor, albumId);
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();

        Assert.Equal(before, await VersionAsync(editor, albumId));
        // The editor's pending edit still applies.
        (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = before, name = "Still valid" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Adding_Or_Withdrawing_An_Item_Does_Move_The_Content_Version()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");

        var v0 = await VersionAsync(owner, albumId);
        await AddOwnPngAsync(owner, albumId, "a.png");
        var v1 = await VersionAsync(owner, albumId);
        Assert.True(v1 > v0, $"add: {v0} → {v1}");

        var carolFile = await UploadPngAsync(carol, "carol.png");
        (await carol.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = carolFile })).EnsureSuccessStatusCode();
        var v2 = await VersionAsync(owner, albumId);
        Assert.True(v2 > v1, $"contribute: {v1} → {v2}");

        (await carol.DeleteAsync($"/api/shared-albums/{albumId}/contributions/{carolFile}"))
            .EnsureSuccessStatusCode();
        Assert.True(await VersionAsync(owner, albumId) > v2, "withdraw did not move the version");
    }

    // ── Audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_Records_The_Editor_As_The_Actor_Not_The_Album_Owner()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (editorId, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        foreach (var n in new[] { "a.png", "b.png" }) await AddOwnPngAsync(owner, albumId, n);
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var items = await SharedItemsAsync(editor, albumId);
        (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = await VersionAsync(editor, albumId), name = "Edited" }))
            .EnsureSuccessStatusCode();
        (await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/cover",
            new
            {
                expectedVersion = await VersionAsync(editor, albumId),
                fileItemId = items[0].GetProperty("fileItemId").GetGuid(),
            })).EnsureSuccessStatusCode();
        (await editor.PutAsJsonAsync($"/api/shared-albums/{albumId}/order",
            new
            {
                expectedVersion = await VersionAsync(editor, albumId),
                albumItemIds = (await ItemIdsOfAsync(albumId)).AsEnumerable().Reverse().ToList(),
            })).EnsureSuccessStatusCode();
        (await editor.DeleteAsync($"/api/shared-albums/{albumId}/items/" +
            $"{(await ItemIdsOfAsync(albumId))[0]}?expectedVersion={await VersionAsync(editor, albumId)}"))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.AuditLogs.Where(a => a.Action.StartsWith("album.edit_")).ToListAsync();

        Assert.Equal(4, logs.Count);
        // The EDITOR is the actor for all four — never the album owner.
        Assert.All(logs, a => Assert.Equal(editorId, a.UserId));
        Assert.All(logs, a => Assert.Equal(albumId, a.EntityId));
        Assert.Contains(logs, a => a.Action == "album.edit_details");
        Assert.Contains(logs, a => a.Action == "album.edit_cover");
        Assert.Contains(logs, a => a.Action == "album.edit_reorder");
        Assert.Contains(logs, a => a.Action == "album.edit_remove_item");

        foreach (var log in logs)
        {
            var payload = log.MetadataJson ?? string.Empty;
            Assert.Contains("version", payload, StringComparison.OrdinalIgnoreCase);
            // Never a file name, an address, or storage internals.
            Assert.DoesNotContain(".png", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EditorEmail, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ownerId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_Conflict_Writes_No_Audit_And_No_Mutation()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var stale = await VersionAsync(editor, albumId);
        (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = stale, name = "First" })).EnsureSuccessStatusCode();
        var auditsAfterWin = await CountAuditAsync("album.edit_details");

        var conflict = await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = stale, name = "Second" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        Assert.Equal(auditsAfterWin, await CountAuditAsync("album.edit_details"));
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("First", detail.GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_Audit_Entry_Is_Written_Inside_The_Mutation_Transaction()
    {
        // The guarantee: a curation change can never commit without the entry
        // that explains it. Asserted by observing that the mutation and its
        // audit become visible TOGETHER — the album's new version and the
        // matching audit row are both present the instant the call returns, and
        // the audit's recorded version equals the album's.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (editorId, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        var response = await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = await VersionAsync(editor, albumId), name = "Edited" });
        response.EnsureSuccessStatusCode();
        var newVersion = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var album = await db.Albums.AsNoTracking().FirstAsync(a => a.Id == albumId);
        var log = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == "album.edit_details");

        Assert.Equal(newVersion, album.Version);
        Assert.Equal(editorId, log.UserId);
        // The audit records the version the mutation produced — so the two
        // cannot have been written from different states.
        Assert.Contains($"\"version\":{newVersion}", log.MetadataJson!, StringComparison.Ordinal);
    }

    // ── Party / TV stay out of it ───────────────────────────────────────────

    [Fact]
    public async Task An_Editors_Curation_Does_Not_Reach_Party_Or_TV_Settings()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await AddOwnPngAsync(owner, albumId, "a.png");
        await InviteAcceptAsync(owner, editor, albumId, EditorEmail, "editor");

        (await editor.PatchAsJsonAsync($"/api/shared-albums/{albumId}",
            new { expectedVersion = await VersionAsync(editor, albumId), name = "Edited" }))
            .EnsureSuccessStatusCode();

        // Editing the album never turns on a public surface.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var album = await db.Albums.AsNoTracking().FirstAsync(a => a.Id == albumId);
        Assert.False(album.ShowOnTv);
        Assert.Empty(await db.PartyAlbumLinks.Where(p => p.AlbumId == albumId).ToListAsync());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddOwnPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> InviteAsync(
        HttpClient owner, Guid albumId, string email, string role)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
    }

    private static async Task AcceptAsync(HttpClient member, Guid membershipId) =>
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();

    private static async Task InviteAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string role) =>
        await AcceptAsync(member, await InviteAsync(owner, albumId, email, role));

    private static async Task<int> VersionAsync(HttpClient client, Guid albumId)
    {
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}");
        return detail.GetProperty("version").GetInt32();
    }

    private static async Task<List<JsonElement>> SharedItemsAsync(HttpClient client, Guid albumId)
    {
        var items = await client.GetFromJsonAsync<JsonElement>(
            $"/api/shared-albums/{albumId}/items");
        return items.EnumerateArray().ToList();
    }

    private static async Task<List<Guid>> SharedItemIdsAsync(HttpClient client, Guid albumId) =>
        (await SharedItemsAsync(client, albumId))
            .Select(i => i.GetProperty("albumItemId").GetGuid()).ToList();

    private async Task<List<Guid>> ItemIdsOfAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder).ThenBy(ai => ai.FileItemId)
            .Select(ai => ai.Id).ToListAsync();
    }

    private async Task<int> CountMembershipsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumMemberships.CountAsync(m => m.AlbumId == albumId);
    }

    private async Task<int> CountAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.CountAsync(a => a.Action == action);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.MediaLists;

// Owner media lists and the account's current selection: references only,
// deduplicated per list, idempotent, owner-isolated, and never showing trashed,
// Vault or foreign files.
public sealed class MediaListEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MediaListEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Summary(Guid Id, string? Name, string Kind, int Version, int Count, string? Stamp = null);
    private sealed record ItemsResult(int Requested, int Succeeded, int Skipped, int Version, int Count);
    private sealed record IdsPage(List<Guid> Ids, string? NextCursor, int Version, int Count);
    private sealed record Item(Guid Id, string Kind, string ThumbnailUrl);
    private sealed record ItemsPage(List<Item> Items, string? NextCursor, int Version, int Count);
    private sealed record GalleryPage(List<Item> Items, string? NextCursor, int Total);

    private async Task<Guid> PhotoAsync(Guid owner, int size)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var f = await files.CreateAsync(owner, null, $"p{size}.png", "image/png",
            new MemoryStream(ImageFixtures.PlainPng(size, size)));
        return f.Id;
    }

    private async Task<Guid[]> PhotosAsync(Guid owner, int count, int first = 8)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++) ids[i] = await PhotoAsync(owner, first + i);
        return ids;
    }

    private async Task InDbAsync(Func<AppDbContext, Task> work)
    {
        using var scope = _factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<Summary> CreateAsync(HttpClient client, string name, params Guid[] ids)
    {
        var r = await client.PostAsJsonAsync("/api/lists", new { name, fileItemIds = ids });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<Summary>())!;
    }

    private static async Task<ItemsResult> AddAsync(HttpClient client, Guid listId, params Guid[] ids)
    {
        var r = await client.PostAsJsonAsync($"/api/lists/{listId}/items", new { fileItemIds = ids });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ItemsResult>())!;
    }

    private static async Task<ItemsResult> RemoveAsync(HttpClient client, Guid listId, params Guid[] ids)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/lists/{listId}/items")
        {
            Content = JsonContent.Create(new { fileItemIds = ids }),
        };
        var r = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ItemsResult>())!;
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient client, Guid listId)
    {
        var all = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<IdsPage>(
                $"/api/lists/{listId}/ids?limit=2" + (cursor is null ? "" : $"&cursor={cursor}")))!;
            all.AddRange(page.Ids);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }

    [Fact]
    public async Task Unauthenticated_Is_401()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/lists")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/selection")).StatusCode);
    }

    [Fact]
    public async Task Create_Rename_Duplicate_Delete_Keep_References_Not_Files()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var photos = await PhotosAsync(owner, 3);
        var blobs = 0;
        await InDbAsync(async db => blobs = await db.BlobObjects.CountAsync());

        var list = await CreateAsync(client, "  Da stampare  ", photos[0], photos[1]);
        Assert.Equal("Da stampare", list.Name);
        Assert.Equal("named", list.Kind);
        Assert.Equal(2, list.Count);

        var renamed = await client.PatchAsJsonAsync($"/api/lists/{list.Id}",
            new { name = "Per la nonna", expectedVersion = list.Version });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var afterRename = (await renamed.Content.ReadFromJsonAsync<Summary>())!;
        Assert.Equal("Per la nonna", afterRename.Name);
        Assert.True(afterRename.Version > list.Version);

        var dup = await client.PostAsJsonAsync($"/api/lists/{list.Id}/duplicate", new { });
        Assert.Equal(HttpStatusCode.Created, dup.StatusCode);
        var copy = (await dup.Content.ReadFromJsonAsync<Summary>())!;
        Assert.Equal("Per la nonna (2)", copy.Name);
        Assert.Equal(await IdsAsync(client, list.Id), await IdsAsync(client, copy.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/lists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/lists/{list.Id}")).StatusCode);
        // The copy and every file survive the deletion of the original.
        Assert.Equal(2, (await client.GetFromJsonAsync<Summary>($"/api/lists/{copy.Id}"))!.Count);
        await InDbAsync(async db =>
        {
            Assert.Equal(3, await db.FileItems.CountAsync(f => photos.Contains(f.Id) && f.DeletedAt == null));
            Assert.Equal(blobs, await db.BlobObjects.CountAsync());
        });

        var lists = await client.GetFromJsonAsync<List<Summary>>("/api/lists");
        Assert.Equal(copy.Id, Assert.Single(lists!).Id);
    }

    [Fact]
    public async Task Rename_On_A_Stale_Version_Is_A_Conflict_And_Bad_Names_Are_Refused()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var list = await CreateAsync(client, "A");
        await AddAsync(client, list.Id, await PhotoAsync(owner, 9));

        var stale = await client.PatchAsJsonAsync($"/api/lists/{list.Id}", new { name = "B", expectedVersion = list.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("A", (await client.GetFromJsonAsync<Summary>($"/api/lists/{list.Id}"))!.Name);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/lists", new { name = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/lists", new { name = new string('x', 121) })).StatusCode);
    }

    [Fact]
    public async Task Adds_Are_Deduplicated_Idempotent_And_Keep_Pick_Order()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 4);
        var list = await CreateAsync(client, "L");

        var first = await AddAsync(client, list.Id, p[2], p[0], p[2]);
        Assert.Equal(new ItemsResult(3, 2, 1, first.Version, 2), first);
        var again = await AddAsync(client, list.Id, p[0], p[2]);
        Assert.Equal(0, again.Succeeded);
        Assert.Equal(first.Version, again.Version); // nothing changed, no version move
        await AddAsync(client, list.Id, p[3], p[1]);

        Assert.Equal(new[] { p[2], p[0], p[3], p[1] }, await IdsAsync(client, list.Id));

        var removed = await RemoveAsync(client, list.Id, p[0], p[0], Guid.NewGuid());
        Assert.Equal(1, removed.Succeeded);
        Assert.Equal(3, removed.Count);
        Assert.Equal(0, (await RemoveAsync(client, list.Id, p[0])).Succeeded);
    }

    [Fact]
    public async Task One_Photo_In_Many_Lists_Is_Never_Copied()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 3);
        var blobs = 0;
        await InDbAsync(async db => blobs = await db.BlobObjects.CountAsync());
        var a = await CreateAsync(client, "A", p[0], p[1]);
        var b = await CreateAsync(client, "B", p[1], p[2]);
        var c = await CreateAsync(client, "C", p[1]);

        await InDbAsync(async db =>
        {
            Assert.Equal(3, await db.FileItems.CountAsync());
            Assert.Equal(blobs, await db.BlobObjects.CountAsync());
            Assert.Equal(3, await db.MediaListItems.CountAsync(i => i.FileItemId == p[1]));
        });

        await RemoveAsync(client, a.Id, p[1]);
        Assert.Equal(new[] { p[1], p[2] }, await IdsAsync(client, b.Id));
        Assert.Equal(new[] { p[1] }, await IdsAsync(client, c.Id));
    }

    [Fact]
    public async Task Lists_Are_Owner_Private_And_Foreign_Files_Never_Enter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var mine = await PhotoAsync(owner, 9);
        var list = await CreateAsync(client, "Mine", mine);

        var (other, otherClient) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        var theirs = await PhotoAsync(other, 11);

        // Nothing about my list is reachable to another account.
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/lists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/lists/{list.Id}/ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/lists/{list.Id}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/lists/{list.Id}/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PostAsJsonAsync($"/api/lists/{list.Id}/items", new { fileItemIds = new[] { theirs } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PatchAsJsonAsync($"/api/lists/{list.Id}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/lists/{list.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.PostAsJsonAsync($"/api/lists/{list.Id}/duplicate", new { name = "x" })).StatusCode);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<Summary>>("/api/lists"))!);

        // Somebody else's file id is dropped silently, never added.
        var r = await AddAsync(client, list.Id, theirs);
        Assert.Equal(0, r.Succeeded);
        Assert.Equal(new[] { mine }, await IdsAsync(client, list.Id));
    }

    [Fact]
    public async Task Trashed_And_Vault_Files_Drop_Out_And_Come_Back_On_Restore()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 3);
        var list = await CreateAsync(client, "L", p);

        await InDbAsync(async db =>
        {
            var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = owner, CreatedAt = DateTime.UtcNow };
            db.PrivateVaults.Add(vault);
            var vaulted = await db.FileItems.FirstAsync(f => f.Id == p[1]);
            vaulted.PrivateVaultId = vault.Id;
            var trashed = await db.FileItems.FirstAsync(f => f.Id == p[2]);
            trashed.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        Assert.Equal(new[] { p[0] }, await IdsAsync(client, list.Id));
        Assert.Equal(1, (await client.GetFromJsonAsync<Summary>($"/api/lists/{list.Id}"))!.Count);
        var items = (await client.GetFromJsonAsync<ItemsPage>($"/api/lists/{list.Id}/items"))!;
        Assert.Equal(new[] { p[0] }, items.Items.Select(i => i.Id));
        var media = (await client.GetFromJsonAsync<GalleryPage>($"/api/lists/{list.Id}/media"))!;
        Assert.Equal(new[] { p[0] }, media.Items.Select(i => i.Id));

        // Trashed / vaulted ids cannot be added either.
        var fresh = await CreateAsync(client, "Fresh", p[1], p[2]);
        Assert.Equal(0, fresh.Count);

        // Restoring the file brings it back where it was; nothing was rewritten.
        await InDbAsync(async db =>
        {
            var trashed = await db.FileItems.FirstAsync(f => f.Id == p[2]);
            trashed.DeletedAt = null;
            await db.SaveChangesAsync();
        });
        Assert.Equal(new[] { p[0], p[2] }, await IdsAsync(client, list.Id));
    }

    // Trash, Vault and restore change what a list SHOWS without writing it, so
    // its version stays put. The stamp moves, and moves even when one file
    // leaves as another comes back at the same count: that is what other
    // devices follow to drop or regain a pick.
    [Fact]
    public async Task The_Stamp_Follows_Trash_Vault_And_Restore_When_The_Version_Does_Not()
    {
        var (owner, laptop) = await _factory.CreateAuthenticatedClientAsync();
        var phone = await _factory.LoginAsync();
        var p = await PhotosAsync(owner, 3);
        var selection = (await laptop.GetFromJsonAsync<Summary>("/api/selection"))!;
        await AddAsync(laptop, selection.Id, p);
        var start = (await phone.GetFromJsonAsync<Summary>("/api/selection"))!;
        Assert.False(string.IsNullOrEmpty(start.Stamp));
        Assert.Equal(start.Stamp, (await laptop.GetFromJsonAsync<Summary>($"/api/lists/{selection.Id}"))!.Stamp);

        Guid vaultId = Guid.Empty;
        await InDbAsync(async db =>
        {
            var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = owner, CreatedAt = DateTime.UtcNow };
            vaultId = vault.Id;
            db.PrivateVaults.Add(vault);
            (await db.FileItems.FirstAsync(f => f.Id == p[2])).DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });
        var trashed = (await phone.GetFromJsonAsync<Summary>("/api/selection"))!;
        Assert.Equal(start.Version, trashed.Version);
        Assert.Equal(2, trashed.Count);
        Assert.NotEqual(start.Stamp, trashed.Stamp);

        // p[0] goes to the Vault as p[2] comes back from the Trash: still two.
        await InDbAsync(async db =>
        {
            (await db.FileItems.FirstAsync(f => f.Id == p[0])).PrivateVaultId = vaultId;
            (await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == p[2])).DeletedAt = null;
            await db.SaveChangesAsync();
        });
        var swapped = (await phone.GetFromJsonAsync<Summary>("/api/selection"))!;
        Assert.Equal(start.Version, swapped.Version);
        Assert.Equal(2, swapped.Count);
        Assert.NotEqual(trashed.Stamp, swapped.Stamp);
        Assert.Equal(new[] { p[1], p[2] }, await IdsAsync(phone, selection.Id));

        // Back to the same visible picks, back to the same stamp.
        await InDbAsync(async db =>
        {
            (await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == p[0])).PrivateVaultId = null;
            await db.SaveChangesAsync();
        });
        Assert.Equal(start.Stamp, (await phone.GetFromJsonAsync<Summary>("/api/selection"))!.Stamp);

        // A write answers with the stamp too, and it never carries a file id.
        var write = await laptop.PostAsJsonAsync($"/api/lists/{selection.Id}/items", new { fileItemIds = new[] { p[0] } });
        var body = await write.Content.ReadAsStringAsync();
        Assert.Contains("\"stamp\"", body);
        foreach (var id in p) Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Permanently_Deleting_A_File_Removes_Its_Entries()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 2);
        var list = await CreateAsync(client, "L", p);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.SoftDeleteAsync(owner, p[0]));
            Assert.True(await files.PermanentDeleteAsync(owner, p[0]));
        }
        await InDbAsync(async db => Assert.Equal(0, await db.MediaListItems.CountAsync(i => i.FileItemId == p[0])));
        Assert.Equal(new[] { p[1] }, await IdsAsync(client, list.Id));
    }

    [Fact]
    public async Task Excluded_Media_Stays_Visible_In_A_List()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 2);
        var list = await CreateAsync(client, "L", p);
        await InDbAsync(async db =>
        {
            var f = await db.FileItems.FirstAsync(x => x.Id == p[1]);
            f.MediaLibraryState = MediaLibraryState.Excluded;
            await db.SaveChangesAsync();
        });

        var items = (await client.GetFromJsonAsync<ItemsPage>($"/api/lists/{list.Id}/items"))!;
        Assert.Equal(new[] { p[0], p[1] }, items.Items.Select(i => i.Id));
        var media = (await client.GetFromJsonAsync<GalleryPage>($"/api/lists/{list.Id}/media"))!;
        Assert.Equal(2, media.Total);
        var active = (await client.GetFromJsonAsync<GalleryPage>($"/api/lists/{list.Id}/media?scope=active"))!;
        Assert.Equal(new[] { p[0] }, active.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task List_Media_Uses_The_Gallery_Contract_With_A_Stable_Cursor()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotosAsync(owner, 5);
        var list = await CreateAsync(client, "L", p[0], p[1], p[2], p[3]);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<GalleryPage>(
                $"/api/lists/{list.Id}/media?limit=3" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")))!;
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
        Assert.DoesNotContain(p[4], seen);

        // A cursor from the whole library is not accepted for the list.
        var library = (await client.GetFromJsonAsync<GalleryPage>("/api/media?limit=2"))!;
        var mismatched = await client.GetAsync(
            $"/api/lists/{list.Id}/media?limit=2&cursor={Uri.EscapeDataString(library.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    [Fact]
    public async Task Selection_Is_One_Per_Account_And_Syncs_Between_Two_Sessions()
    {
        var (owner, laptop) = await _factory.CreateAuthenticatedClientAsync();
        var phone = await _factory.LoginAsync();
        var p = await PhotosAsync(owner, 3);

        var fromLaptop = (await laptop.GetFromJsonAsync<Summary>("/api/selection"))!;
        var fromPhone = (await phone.GetFromJsonAsync<Summary>("/api/selection"))!;
        Assert.Equal(fromLaptop.Id, fromPhone.Id);
        Assert.Equal("selection", fromLaptop.Kind);
        Assert.Null(fromLaptop.Name);

        var added = await AddAsync(laptop, fromLaptop.Id, p[0], p[1]);
        // The phone notices the version moved and reads the same picks.
        var seen = (await phone.GetFromJsonAsync<Summary>("/api/selection"))!;
        Assert.True(seen.Version > fromPhone.Version);
        Assert.Equal(added.Version, seen.Version);
        Assert.Equal(new[] { p[0], p[1] }, await IdsAsync(phone, seen.Id));

        await RemoveAsync(phone, seen.Id, p[0]);
        await AddAsync(phone, seen.Id, p[2]);
        Assert.Equal(new[] { p[1], p[2] }, await IdsAsync(laptop, fromLaptop.Id));

        // The selection is not a named list, cannot be renamed or deleted, and
        // is saved as a list by duplicating it under a name.
        Assert.Empty((await laptop.GetFromJsonAsync<List<Summary>>("/api/lists"))!);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await laptop.PatchAsJsonAsync($"/api/lists/{seen.Id}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await laptop.DeleteAsync($"/api/lists/{seen.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await laptop.PostAsJsonAsync($"/api/lists/{seen.Id}/duplicate", new { })).StatusCode);
        var saved = await laptop.PostAsJsonAsync($"/api/lists/{seen.Id}/duplicate", new { name = "Scelte" });
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var savedList = (await saved.Content.ReadFromJsonAsync<Summary>())!;
        Assert.Equal("named", savedList.Kind);
        Assert.Equal(new[] { p[1], p[2] }, await IdsAsync(laptop, savedList.Id));

        var cleared = await phone.PostAsync($"/api/lists/{seen.Id}/clear", null);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0, (await laptop.GetFromJsonAsync<Summary>("/api/selection"))!.Count);
        // Clearing the selection never touches the saved list.
        Assert.Equal(2, (await laptop.GetFromJsonAsync<Summary>($"/api/lists/{savedList.Id}"))!.Count);

        // Another account gets its own, separate selection.
        var (_, other) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        Assert.NotEqual(fromLaptop.Id, (await other.GetFromJsonAsync<Summary>("/api/selection"))!.Id);
    }

    [Fact]
    public async Task Requests_Are_Bounded_And_Malformed_Input_Refused()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var list = await CreateAsync(client, "L");
        var tooMany = Enumerable.Range(0, 3001).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/lists/{list.Id}/items", new { fileItemIds = tooMany })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/lists/{list.Id}/items", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/lists/{list.Id}/ids?cursor=../../etc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/lists/{list.Id}/ids?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/lists/{list.Id}/items?limit=101")).StatusCode);
    }

    [Fact]
    public async Task Responses_Carry_No_Storage_Internals_And_Named_Lists_Are_Audited()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var p = await PhotoAsync(owner, 9);
        var list = await CreateAsync(client, "Segreta", p);
        string blobId = "", sha = "", storageKey = "";
        await InDbAsync(async db =>
        {
            var f = await db.FileItems.FirstAsync(x => x.Id == p);
            var blob = await db.BlobObjects.FirstAsync(b => b.Id == f.BlobObjectId);
            blobId = blob.Id.ToString();
            sha = blob.Sha256;
            storageKey = blob.StorageKey;
        });

        foreach (var url in new[]
                 {
                     "/api/lists", $"/api/lists/{list.Id}", $"/api/lists/{list.Id}/ids",
                     $"/api/lists/{list.Id}/items", $"/api/lists/{list.Id}/media", "/api/selection",
                 })
        {
            var body = await client.GetStringAsync(url);
            Assert.DoesNotContain(blobId, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sha, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storageKey, body, StringComparison.OrdinalIgnoreCase);
        }

        var selection = (await client.GetFromJsonAsync<Summary>("/api/selection"))!;
        await AddAsync(client, selection.Id, p);
        await InDbAsync(async db =>
        {
            var rows = await db.AuditLogs.Where(a => a.EntityType == AuditEntityTypes.MediaList).ToListAsync();
            Assert.Contains(rows, r => r.Action == AuditActions.MediaListCreate && r.EntityId == list.Id);
            // Selection changes are personal UI state, not audited; list names never land in metadata.
            Assert.DoesNotContain(rows, r => r.EntityId == selection.Id);
            Assert.DoesNotContain(rows, r => (r.MetadataJson ?? "").Contains("Segreta"));
        });
    }
}

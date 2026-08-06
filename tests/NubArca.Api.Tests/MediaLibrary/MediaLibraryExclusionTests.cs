using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.MediaLibrary;

// Slice 3 (media organization): per-file media-library exclusion. Covers state
// transitions + idempotency, data preservation, scoping of the media surfaces
// (photo/video gallery, album content), file-browser invariance, AI candidate
// eligibility, and the Private-Vault interaction.
public sealed class MediaLibraryExclusionTests
{
    private static async Task<(Guid UserId, HttpClient Client)> AuthAsync(
        SqliteWebApplicationFactory factory, string email = "owner@example.com")
    {
        factory.EnsureDatabaseCreated();
        return await factory.CreateAuthenticatedClientAsync(email);
    }

    private static async Task<FileItem> UploadImageAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, Guid? folderId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, folderId, name, "image/png",
            new MemoryStream(ImageFixtures.PlainPng()));
    }

    private static async Task<FileItem> UploadVideoAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, Guid? folderId, string name, string signature)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, folderId, name, "video/mp4",
            new MemoryStream(ImageFixtures.MinimalMp4(signature)));
    }

    private static async Task<MediaLibraryBulkResult> ExcludeAsync(HttpClient client, params Guid[] ids)
    {
        var resp = await client.PostAsJsonAsync("/api/media-library/exclude", new { fileIds = ids });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MediaLibraryBulkResult>())!;
    }

    private static async Task<MediaLibraryBulkResult> RestoreAsync(HttpClient client, params Guid[] ids)
    {
        var resp = await client.PostAsJsonAsync("/api/media-library/restore", new { fileIds = ids });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MediaLibraryBulkResult>())!;
    }

    private static async Task<string> RawAsync(HttpClient client, string url)
        => await (await client.GetAsync(url)).Content.ReadAsStringAsync();

    private static async Task<MediaLibraryState> StateAsync(SqliteWebApplicationFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == fileId))
            .MediaLibraryState;
    }

    // ── state + idempotency ──────────────────────────────────────────────────

    [Fact]
    public async Task New_Files_Default_To_Active()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, _) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "a.png");
        Assert.Equal(MediaLibraryState.Active, await StateAsync(factory, file.Id));
    }

    [Fact]
    public async Task Exclude_Then_Restore_Round_Trips_And_Is_Idempotent()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "a.png");

        var first = await ExcludeAsync(client, file.Id);
        Assert.Equal(new MediaLibraryBulkResult(1, 1, 0, 0), first);
        Assert.Equal(MediaLibraryState.Excluded, await StateAsync(factory, file.Id));

        // Idempotent: excluding again changes nothing.
        var again = await ExcludeAsync(client, file.Id);
        Assert.Equal(new MediaLibraryBulkResult(1, 0, 1, 0), again);

        var restored = await RestoreAsync(client, file.Id);
        Assert.Equal(new MediaLibraryBulkResult(1, 1, 0, 0), restored);
        Assert.Equal(MediaLibraryState.Active, await StateAsync(factory, file.Id));

        var restoreAgain = await RestoreAsync(client, file.Id);
        Assert.Equal(new MediaLibraryBulkResult(1, 0, 1, 0), restoreAgain);
    }

    [Fact]
    public async Task Bulk_Mixed_States_And_Duplicate_Ids_Are_Counted_Correctly()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var active = await UploadImageAsync(factory, userId, null, "active.png");
        var alreadyExcluded = await UploadImageAsync(factory, userId, null, "excluded.png");
        await ExcludeAsync(client, alreadyExcluded.Id);
        var missing = Guid.NewGuid();

        // Duplicate ids collapse; one active→excluded, one already excluded, one missing.
        var result = await ExcludeAsync(client, active.Id, active.Id, alreadyExcluded.Id, missing);
        Assert.Equal(3, result.Requested); // de-duplicated
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, result.NotFoundOrNotOwned);
    }

    [Fact]
    public async Task Exclude_Is_Owner_Scoped()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (aliceId, alice) = await AuthAsync(factory, "alice@example.com");
        await factory.SeedUserAsync("bob@example.com");
        var bob = await factory.LoginAsync("bob@example.com");
        var aliceFile = await UploadImageAsync(factory, aliceId, null, "alice.png");

        // Bob cannot exclude Alice's file.
        var result = await ExcludeAsync(bob, aliceFile.Id);
        Assert.Equal(new MediaLibraryBulkResult(1, 0, 0, 1), result);
        Assert.Equal(MediaLibraryState.Active, await StateAsync(factory, aliceFile.Id));
    }

    [Fact]
    public async Task Empty_And_Missing_Body_Are_Handled()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (_, client) = await AuthAsync(factory);

        var empty = await ExcludeAsync(client);
        Assert.Equal(new MediaLibraryBulkResult(0, 0, 0, 0), empty);

        var missingBody = await client.PostAsJsonAsync("/api/media-library/exclude", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);
    }

    // ── preservation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Exclusion_Preserves_Location_Blob_Metadata_Tags_And_Album()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var folderResp = await client.PostAsJsonAsync("/api/folders", new { name = "Trip" });
        folderResp.EnsureSuccessStatusCode();
        var folderId = JsonDocument.Parse(await folderResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        var file = await UploadImageAsync(factory, userId, folderId, "keep.png");

        (await client.PatchAsJsonAsync($"/api/files/{file.Id}/metadata", new
        {
            title = "My title", tags = new[] { "trip" }, rating = 5, favorite = true,
        })).EnsureSuccessStatusCode();

        var albumResp = await client.PostAsJsonAsync("/api/albums", new { name = "Album" });
        var albumId = JsonDocument.Parse(await albumResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/albums/{albumId}/items/bulk",
            new { fileItemIds = new[] { file.Id } })).EnsureSuccessStatusCode();

        Guid blobBefore;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobBefore = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id)).BlobObjectId;
        }

        await ExcludeAsync(client, file.Id);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var after = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == file.Id);
            Assert.Equal(folderId, after.ParentFolderId);
            Assert.Equal(blobBefore, after.BlobObjectId);
            Assert.Null(after.PrivateVaultId);

            var meta = await db.FileItemUserMetadata.AsNoTracking().SingleAsync(m => m.FileItemId == file.Id);
            Assert.Equal("My title", meta.Title);
            Assert.Contains("trip", meta.TagsJson);
            Assert.Equal(5, meta.Rating);
            Assert.True(meta.IsFavorite);

            // Album membership row is preserved.
            Assert.True(await db.AlbumItems.AsNoTracking().AnyAsync(a => a.AlbumId == albumId && a.FileItemId == file.Id));
        }
    }

    // ── media-surface scoping ─────────────────────────────────────────────────

    [Fact]
    public async Task Excluded_Photo_Leaves_Active_Gallery_And_Appears_In_Excluded_Tab()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var keep = await UploadImageAsync(factory, userId, null, "keep.png");
        var hide = await UploadImageAsync(factory, userId, null, "hide.png");

        await ExcludeAsync(client, hide.Id);

        var active = await RawAsync(client, "/api/images?limit=100");
        Assert.Contains(keep.Id.ToString(), active, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hide.Id.ToString(), active, StringComparison.OrdinalIgnoreCase);

        var excluded = await RawAsync(client, "/api/images?limit=100&mediaScope=excluded");
        Assert.Contains(hide.Id.ToString(), excluded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(keep.Id.ToString(), excluded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Excluded_Video_Leaves_Active_Video_Gallery_And_Appears_In_Excluded_Tab()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var keep = await UploadVideoAsync(factory, userId, null, "keep.mp4", "kk01");
        var hide = await UploadVideoAsync(factory, userId, null, "hide.mp4", "hh01");

        await ExcludeAsync(client, hide.Id);

        var active = await RawAsync(client, "/api/videos?limit=100");
        Assert.Contains(keep.Id.ToString(), active, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hide.Id.ToString(), active, StringComparison.OrdinalIgnoreCase);

        var excluded = await RawAsync(client, "/api/videos?limit=100&mediaScope=excluded");
        Assert.Contains(hide.Id.ToString(), excluded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_Brings_The_File_Back_To_The_Active_Gallery()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "a.png");

        await ExcludeAsync(client, file.Id);
        Assert.DoesNotContain(file.Id.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);

        await RestoreAsync(client, file.Id);
        Assert.Contains(file.Id.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Excluded_File_Is_Hidden_From_Album_Content_But_Row_Persists()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "a.png");
        var albumResp = await client.PostAsJsonAsync("/api/albums", new { name = "Album" });
        var albumId = JsonDocument.Parse(await albumResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/albums/{albumId}/items/bulk",
            new { fileItemIds = new[] { file.Id } })).EnsureSuccessStatusCode();

        await ExcludeAsync(client, file.Id);

        // Album CONTENT excludes it.
        Assert.DoesNotContain(file.Id.ToString(), await RawAsync(client, $"/api/albums/{albumId}/items"),
            StringComparison.OrdinalIgnoreCase);

        // Restoring brings it back into the album content (row was never removed).
        await RestoreAsync(client, file.Id);
        Assert.Contains(file.Id.ToString(), await RawAsync(client, $"/api/albums/{albumId}/items"),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── file browser invariance ───────────────────────────────────────────────

    [Fact]
    public async Task File_Browser_Shows_Active_And_Excluded_But_Not_Vault()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var folderResp = await client.PostAsJsonAsync("/api/folders", new { name = "F" });
        var folderId = JsonDocument.Parse(await folderResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        var active = await UploadImageAsync(factory, userId, folderId, "active.png");
        var excluded = await UploadImageAsync(factory, userId, folderId, "excluded.png");
        var vaulted = await UploadImageAsync(factory, userId, folderId, "vaulted.png");

        await ExcludeAsync(client, excluded.Id);

        // Move the third file into the Private Vault.
        (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = "correct horse" }))
            .EnsureSuccessStatusCode();
        var unlock = await client.PostAsJsonAsync("/api/private-vault/unlock", new { password = "correct horse" });
        var token = JsonDocument.Parse(await unlock.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/move-in"))
        {
            req.Content = JsonContent.Create(new { fileIds = new[] { vaulted.Id }, folderIds = Array.Empty<Guid>() });
            req.Headers.Add("X-Vault-Token", token);
            (await client.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var browser = await RawAsync(client, $"/api/folders/{folderId}/children");
        Assert.Contains(active.Id.ToString(), browser, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(excluded.Id.ToString(), browser, StringComparison.OrdinalIgnoreCase);  // Excluded IS visible here
        Assert.DoesNotContain(vaulted.Id.ToString(), browser, StringComparison.OrdinalIgnoreCase); // Vault is NOT
    }

    // ── AI candidate eligibility ──────────────────────────────────────────────

    [Fact]
    public async Task Excluded_Blob_Is_Not_An_Ai_Embedding_Candidate()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "ai.png");
        await ExcludeAsync(client, file.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = (await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == file.Id))
            .BlobObjectId;
        // The candidate rule ("at least one active, non-excluded, non-vault file
        // references the blob") must not match once the only reference is excluded.
        var referencedByActive = await db.FileItems
            .AnyAsync(f => f.BlobObjectId == blobId
                && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active);
        Assert.False(referencedByActive);
    }

    // ── Private Vault interaction ─────────────────────────────────────────────

    [Fact]
    public async Task Moving_An_Excluded_File_Into_Personal_Preserves_Its_Excluded_State()
    {
        using var factory = new SqliteWebApplicationFactory();
        var (userId, client) = await AuthAsync(factory);
        var file = await UploadImageAsync(factory, userId, null, "secret.png");
        await ExcludeAsync(client, file.Id);

        (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = "correct horse" }))
            .EnsureSuccessStatusCode();
        var unlock = await client.PostAsJsonAsync("/api/private-vault/unlock", new { password = "correct horse" });
        var token = JsonDocument.Parse(await unlock.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/move-in"))
        {
            req.Content = JsonContent.Create(new { fileIds = new[] { file.Id }, folderIds = Array.Empty<Guid>() });
            req.Headers.Add("X-Vault-Token", token);
            (await client.SendAsync(req)).EnsureSuccessStatusCode();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == file.Id);
        Assert.NotNull(after.PrivateVaultId);                       // now in the vault
        Assert.Equal(MediaLibraryState.Excluded, after.MediaLibraryState); // state preserved
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.PhotoExport;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Vault;

// Private Vault (v0): exclusion-first + password-unlocked owner-private area.
// Covers the required security, functional, exclusion, and no-leak behaviours
// over the real HTTP + upload + query-filter stack.
public sealed class PrivateVaultTests
{
    private const string Password = "correct horse battery";

    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "TokenHash", "tokenHash", "PasswordHash", "passwordHash",
        "/storage/objects/", "PrivateVaultId", "privateVaultId", "EncryptionMetadata",
    };

    private static SqliteWebApplicationFactory NewFactory()
    {
        var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int dim, Guid? folderId = null)
    {
        var part = new ByteArrayContent(Png(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var url = folderId is null ? "/api/files" : $"/api/folders/{folderId}/files";
        var resp = await client.PostAsync(url, multipart);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task<Guid> CreateFolderAsync(HttpClient client, string name, Guid? parentId = null)
    {
        var url = parentId is null ? "/api/folders" : $"/api/folders/{parentId}/folders";
        var resp = await client.PostAsJsonAsync(url, new { name });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FolderSummary>())!.Id;
    }

    private static async Task<(HttpStatusCode Status, string? Token)> UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/private-vault/unlock", new { password });
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            return (resp.StatusCode, null);
        }
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (resp.StatusCode, doc.RootElement.GetProperty("token").GetString());
    }

    private static async Task<string> SetupAndUnlockAsync(HttpClient client)
    {
        (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = Password }))
            .EnsureSuccessStatusCode();
        var (status, token) = await UnlockAsync(client, Password);
        Assert.Equal(HttpStatusCode.OK, status);
        return token!;
    }

    private static async Task<HttpResponseMessage> VaultGetAsync(HttpClient client, string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Vault-Token", token);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> VaultMoveAsync(
        HttpClient client, string url, string token, IEnumerable<Guid>? fileIds, IEnumerable<Guid>? folderIds)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                fileIds = (fileIds ?? Array.Empty<Guid>()).ToArray(),
                folderIds = (folderIds ?? Array.Empty<Guid>()).ToArray(),
            }),
        };
        req.Headers.Add("X-Vault-Token", token);
        return await client.SendAsync(req);
    }

    private static async Task<string> RawAsync(HttpClient client, string url)
        => await (await client.GetAsync(url)).Content.ReadAsStringAsync();

    // ── functional ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_Reveals_Only_Configured_Flag_When_Not_Set_Up()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var raw = await RawAsync(client, "/api/private-vault");
        var doc = JsonDocument.Parse(raw);
        Assert.False(doc.RootElement.GetProperty("configured").GetBoolean());
        // No content signal: no counts / names / file / folder anywhere.
        foreach (var needle in new[] { "count", "Count", "files", "folders", "items" })
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Setup_Then_Unlock_Issues_Working_Token()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var token = await SetupAndUnlockAsync(client);

        var resp = await VaultGetAsync(client, "/api/private-vault/root", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Setup_Twice_Is_Conflict()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = Password }))
            .EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync("/api/private-vault/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Setup_Rejects_Short_Password()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsJsonAsync("/api/private-vault/setup", new { password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unlock_With_Wrong_Password_Is_Generic_Failure()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = Password }))
            .EnsureSuccessStatusCode();

        var (wrongStatus, wrongToken) = await UnlockAsync(client, "not the password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongStatus);
        Assert.Null(wrongToken);
    }

    [Fact]
    public async Task Unlock_When_No_Vault_Is_Same_Generic_Failure_As_Wrong_Password()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        // No vault configured at all.
        var resp = await client.PostAsJsonAsync("/api/private-vault/unlock", new { password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        // The message must not reveal that the vault does not exist.
        Assert.DoesNotContain("not configured", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not exist", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_File_In_Then_It_Appears_In_Vault_After_Unlock()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "secret.png", 10);
        var token = await SetupAndUnlockAsync(client);

        var move = await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { fileId }, null);
        move.EnsureSuccessStatusCode();

        var rootRaw = await (await VaultGetAsync(client, "/api/private-vault/root", token)).Content.ReadAsStringAsync();
        Assert.Contains(fileId.ToString(), rootRaw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_Folder_In_Marks_All_Descendants_Private()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();

        var trip = await CreateFolderAsync(client, "Trip");
        var day1 = await CreateFolderAsync(client, "Day1", trip);
        var topFile = await UploadPngAsync(client, "top.png", 10, trip);
        var deepFile = await UploadPngAsync(client, "deep.png", 12, day1);

        var token = await SetupAndUnlockAsync(client);
        var move = await VaultMoveAsync(client, "/api/private-vault/move-in", token, null, new[] { trip });
        move.EnsureSuccessStatusCode();

        // Every descendant folder + file is now in the vault (DB-level truth).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var fid in new[] { topFile, deepFile })
        {
            var f = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == fid);
            Assert.NotNull(f.PrivateVaultId);
        }
        foreach (var folderId in new[] { trip, day1 })
        {
            var f = await db.Folders.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == folderId);
            Assert.NotNull(f.PrivateVaultId);
        }

        // The deep file is reachable by opening the vault folder tree.
        var deepListingRaw = await (await VaultGetAsync(
            client, $"/api/private-vault/folders/{day1}", token)).Content.ReadAsStringAsync();
        Assert.Contains(deepFile.ToString(), deepListingRaw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_Out_Restores_Content_To_Normal_Library()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "restore-me.png", 10);
        var token = await SetupAndUnlockAsync(client);

        (await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { fileId }, null))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain(fileId.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);

        (await VaultMoveAsync(client, "/api/private-vault/move-out", token, new[] { fileId }, null))
            .EnsureSuccessStatusCode();

        // Back in the normal gallery, gone from the vault.
        Assert.Contains(fileId.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);
        var rootRaw = await (await VaultGetAsync(client, "/api/private-vault/root", token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(fileId.ToString(), rootRaw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lock_Invalidates_The_Token()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var token = await SetupAndUnlockAsync(client);

        using (var lockReq = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/lock"))
        {
            lockReq.Headers.Add("X-Vault-Token", token);
            (await client.SendAsync(lockReq)).EnsureSuccessStatusCode();
        }

        var afterLock = await VaultGetAsync(client, "/api/private-vault/root", token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLock.StatusCode);
    }

    // ── exclusion ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vault_Content_Is_Excluded_From_Gallery_Files_And_Search()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "hidden-name.png", 10);
        var token = await SetupAndUnlockAsync(client);

        // Present before moving.
        Assert.Contains(fileId.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);

        (await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { fileId }, null))
            .EnsureSuccessStatusCode();

        // Gallery, normal folder listing, and search all exclude it.
        Assert.DoesNotContain(fileId.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileId.ToString(), await RawAsync(client, "/api/folders/children"),
            StringComparison.OrdinalIgnoreCase);
        var search = await RawAsync(client, "/api/search?q=hidden-name");
        Assert.DoesNotContain(fileId.ToString(), search, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden-name", search, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Vault_Content_Is_Excluded_From_Photo_Export_Eligibility()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "keep.png", 10);
        var hide = await UploadPngAsync(client, "hide.png", 12);
        var token = await SetupAndUnlockAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await PhotoExportEligibility.EligiblePhotos(db, ownerId).CountAsync());

        (await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { hide }, null))
            .EnsureSuccessStatusCode();

        Assert.Equal(1, await PhotoExportEligibility.EligiblePhotos(db, ownerId).CountAsync());
    }

    [Fact]
    public async Task Vault_Content_Is_Excluded_From_Ai_Embedding_Candidates()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var hide = await UploadPngAsync(client, "ai.png", 10);
        var token = await SetupAndUnlockAsync(client);
        (await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { hide }, null))
            .EnsureSuccessStatusCode();

        // The AI backfill's candidate rule ("at least one active non-vault
        // FileItem references the blob") must not match a blob whose only
        // reference is now a vault file. The global query filter enforces this.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = (await db.FileItems.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(f => f.Id == hide)).BlobObjectId;
        var referencedByVisible = await db.FileItems
            .AnyAsync(f => f.BlobObjectId == blobId && f.DeletedAt == null);
        Assert.False(referencedByVisible);
    }

    [Fact]
    public async Task Public_Share_Cannot_Reach_A_File_Moved_Into_The_Vault()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "shared.png", 10);

        // Create a public share BEFORE moving it in.
        var shareResp = await client.PostAsJsonAsync($"/api/files/{fileId}/share-links", new { });
        shareResp.EnsureSuccessStatusCode();
        var shareToken = JsonDocument.Parse(await shareResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("token").GetString()!;

        // Public link works now.
        using (var anon = factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/s/{shareToken}")).StatusCode);
        }

        var token = await SetupAndUnlockAsync(client);
        (await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { fileId }, null))
            .EnsureSuccessStatusCode();

        // After moving into the vault, the public link no longer resolves.
        using (var anon = factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/s/{shareToken}")).StatusCode);
        }
    }

    // ── access control / security ───────────────────────────────────────────

    [Fact]
    public async Task Browse_And_Move_Require_A_Valid_Token()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await SetupAndUnlockAsync(client);

        // No token header at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/private-vault/root")).StatusCode);
        // Garbage token.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(client, "/api/private-vault/root", "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultMoveAsync(client, "/api/private-vault/move-in", "garbage", Array.Empty<Guid>(), null)).StatusCode);
    }

    [Fact]
    public async Task Token_Is_Rejected_Anywhere_Query_String()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var token = await SetupAndUnlockAsync(client);
        // A token supplied ONLY via the query string must not authorize.
        var resp = await client.GetAsync($"/api/private-vault/root?token={token}&X-Vault-Token={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Expired_Token_Cannot_Access()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var token = await SetupAndUnlockAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PrivateVaultAccessTokens.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(client, "/api/private-vault/root", token)).StatusCode);
    }

    [Fact]
    public async Task Foreign_Owner_Cannot_Use_Another_Owners_Token_Or_See_Their_Content()
    {
        using var factory = NewFactory();
        await factory.SeedUserAsync("alice@example.com");
        var alice = await factory.LoginAsync("alice@example.com");
        await factory.SeedUserAsync("bob@example.com");
        var bob = await factory.LoginAsync("bob@example.com");

        var aliceFile = await UploadPngAsync(alice, "alice-secret.png", 10);
        var aliceToken = await SetupAndUnlockAsync(alice);
        (await VaultMoveAsync(alice, "/api/private-vault/move-in", aliceToken, new[] { aliceFile }, null))
            .EnsureSuccessStatusCode();

        // Bob presents Alice's token → rejected (token is owner-bound).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(bob, "/api/private-vault/root", aliceToken)).StatusCode);

        // Bob has his own vault; its root never contains Alice's file.
        var bobToken = await SetupAndUnlockAsync(bob);
        var bobRoot = await (await VaultGetAsync(bob, "/api/private-vault/root", bobToken)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(aliceFile.ToString(), bobRoot, StringComparison.OrdinalIgnoreCase);

        // Bob cannot move Alice's file into his vault (owner-scoped move → no-op).
        (await VaultMoveAsync(bob, "/api/private-vault/move-in", bobToken, new[] { aliceFile }, null))
            .EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillAlices = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == aliceFile);
        // Unchanged: still in Alice's vault, not Bob's.
        Assert.NotNull(stillAlices.PrivateVaultId);
    }

    [Fact]
    public async Task Password_And_Token_Hashes_Are_Never_Returned()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "n.png", 10);

        var statusRaw = await RawAsync(client, "/api/private-vault");
        var setupRaw = await (await client.PostAsJsonAsync("/api/private-vault/setup",
            new { password = Password })).Content.ReadAsStringAsync();
        var unlockRaw = await (await client.PostAsJsonAsync("/api/private-vault/unlock",
            new { password = Password })).Content.ReadAsStringAsync();
        var token = JsonDocument.Parse(unlockRaw).RootElement.GetProperty("token").GetString()!;
        var moveRaw = await (await VaultMoveAsync(
            client, "/api/private-vault/move-in", token, new[] { fileId }, null)).Content.ReadAsStringAsync();
        var rootRaw = await (await VaultGetAsync(client, "/api/private-vault/root", token)).Content.ReadAsStringAsync();

        // The raw unlock token appears ONLY in the unlock response.
        Assert.Contains(token, unlockRaw, StringComparison.Ordinal);
        foreach (var body in new[] { statusRaw, setupRaw, moveRaw, rootRaw })
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
        foreach (var body in new[] { statusRaw, setupRaw, unlockRaw, moveRaw, rootRaw })
        {
            foreach (var needle in ForbiddenNeedles)
            {
                Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            }
            Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
        }
    }

    // ── Slice 2: bulk move-in from the galleries — data preservation + edges ──

    private async Task<int> AuditCountAsync(SqliteWebApplicationFactory factory, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == action);
    }

    [Fact]
    public async Task Move_In_Bulk_Mixed_Photo_And_Video_Preserves_All_Associated_Data()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();

        var trip = await CreateFolderAsync(client, "Trip");
        var photoId = await UploadPngAsync(client, "photo.png", 10, trip);

        Guid videoId;
        using (var scope = factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var video = await files.CreateAsync(
                ownerId, trip, "clip.mp4", "video/mp4",
                new MemoryStream(ImageFixtures.MinimalMp4("vv01")));
            videoId = video.Id;
        }

        (await client.PatchAsJsonAsync($"/api/files/{photoId}/metadata", new
        {
            title = "My photo",
            tags = new[] { "trip", "summer" },
            rating = 4,
            favorite = true,
        })).EnsureSuccessStatusCode();

        var albumResp = await client.PostAsJsonAsync("/api/albums", new { name = "Trip album" });
        albumResp.EnsureSuccessStatusCode();
        var albumId = JsonDocument.Parse(await albumResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/albums/{albumId}/items/bulk",
            new { fileItemIds = new[] { photoId, videoId } })).EnsureSuccessStatusCode();

        Guid photoBlobId, videoBlobId;
        int thumbsBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            photoBlobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == photoId)).BlobObjectId;
            videoBlobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == videoId)).BlobObjectId;
            // The post-upload pipeline already derived thumbnail(s) for the photo
            // inline; the move must leave every derived row exactly as it was.
            thumbsBefore = await db.FileThumbnails.AsNoTracking().CountAsync(t => t.FileItemId == photoId);
            Assert.True(thumbsBefore > 0, "expected the upload pipeline to have derived at least one thumbnail");
        }

        var token = await SetupAndUnlockAsync(client);
        var auditBefore = await AuditCountAsync(factory, AuditActions.PrivateVaultMoveIn);

        var move = await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { photoId, videoId }, null);
        move.EnsureSuccessStatusCode();
        var moveResult = JsonDocument.Parse(await move.Content.ReadAsStringAsync());
        Assert.Equal(2, moveResult.RootElement.GetProperty("movedFiles").GetInt32());

        Assert.Equal(auditBefore + 1, await AuditCountAsync(factory, AuditActions.PrivateVaultMoveIn));

        // Gone from the normal photo + video listings.
        Assert.DoesNotContain(photoId.ToString(), await RawAsync(client, "/api/images?limit=100"),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(videoId.ToString(), await RawAsync(client, "/api/videos?limit=100"),
            StringComparison.OrdinalIgnoreCase);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var photo = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == photoId);
            var video = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == videoId);
            Assert.NotNull(photo.PrivateVaultId);
            Assert.NotNull(video.PrivateVaultId);
            // The move is a pure flag flip: original location and blob are untouched.
            Assert.Equal(trip, photo.ParentFolderId);
            Assert.Equal(trip, video.ParentFolderId);
            Assert.Equal(photoBlobId, photo.BlobObjectId);
            Assert.Equal(videoBlobId, video.BlobObjectId);

            var meta = await db.FileItemUserMetadata.AsNoTracking().SingleAsync(m => m.FileItemId == photoId);
            Assert.Equal("My photo", meta.Title);
            Assert.Contains("trip", meta.TagsJson);
            Assert.Equal(4, meta.Rating);
            Assert.True(meta.IsFavorite);

            var albumItems = await db.AlbumItems.AsNoTracking()
                .Where(a => a.AlbumId == albumId).Select(a => a.FileItemId).ToListAsync();
            Assert.Contains(photoId, albumItems);
            Assert.Contains(videoId, albumItems);

            Assert.Equal(thumbsBefore, await db.FileThumbnails.AsNoTracking()
                .CountAsync(t => t.FileItemId == photoId));
        }

        var rootRaw = await (await VaultGetAsync(client, "/api/private-vault/root", token)).Content.ReadAsStringAsync();
        Assert.Contains(photoId.ToString(), rootRaw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(videoId.ToString(), rootRaw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Move_In_Ignores_Nonexistent_And_Foreign_Ids()
    {
        using var factory = NewFactory();
        await factory.SeedUserAsync("owner@example.com");
        var owner = await factory.LoginAsync("owner@example.com");
        await factory.SeedUserAsync("other@example.com");
        var other = await factory.LoginAsync("other@example.com");

        var ownFile = await UploadPngAsync(owner, "mine.png", 10);
        var otherFile = await UploadPngAsync(other, "theirs.png", 10);
        var missing = Guid.NewGuid();

        var token = await SetupAndUnlockAsync(owner);
        var move = await VaultMoveAsync(owner, "/api/private-vault/move-in", token,
            new[] { ownFile, otherFile, missing }, null);
        move.EnsureSuccessStatusCode();
        Assert.Equal(1, JsonDocument.Parse(await move.Content.ReadAsStringAsync())
            .RootElement.GetProperty("movedFiles").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == otherFile);
        Assert.Null(untouched.PrivateVaultId);
    }

    [Fact]
    public async Task Move_In_Does_Not_Recount_An_Already_Vaulted_Item()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var first = await UploadPngAsync(client, "first.png", 10);
        var second = await UploadPngAsync(client, "second.png", 10);
        var token = await SetupAndUnlockAsync(client);

        var move1 = await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { first }, null);
        move1.EnsureSuccessStatusCode();
        Assert.Equal(1, JsonDocument.Parse(await move1.Content.ReadAsStringAsync())
            .RootElement.GetProperty("movedFiles").GetInt32());

        // Re-requesting the already-vaulted file alongside a genuinely new one must
        // count only the new one — the global filter excludes the vaulted id from
        // the "currently normal" match set, so it can never be double-counted.
        var move2 = await VaultMoveAsync(client, "/api/private-vault/move-in", token, new[] { first, second }, null);
        move2.EnsureSuccessStatusCode();
        Assert.Equal(1, JsonDocument.Parse(await move2.Content.ReadAsStringAsync())
            .RootElement.GetProperty("movedFiles").GetInt32());
    }
}

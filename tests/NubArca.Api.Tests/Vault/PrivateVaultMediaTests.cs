using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Vault;

// Private Vault secure media browser (slice 4). Covers the authorized
// derived-media endpoints: owner + valid unlock token + file-in-THIS-vault gate,
// derived bytes ONLY (no originals, no generation, no jobs), sanitized info DTO,
// generic 404s, owner isolation, and state transitions (lock / move-out).
public sealed class PrivateVaultMediaTests
{
    private const string Password = "correct horse battery";

    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "TokenHash", "tokenHash", "PasswordHash", "passwordHash",
        "/storage/objects/", "PrivateVaultId", "privateVaultId", "embedding", "Embedding",
        "GpsLatitude", "gpsLatitude", "RawMetadataJson", "faceCluster",
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

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int dim)
    {
        var part = new ByteArrayContent(Png(dim));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
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

    private static async Task<HttpResponseMessage> VaultSendAsync(
        HttpClient client, HttpMethod method, string url, string? token, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            req.Headers.Add("X-Vault-Token", token);
        }
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> VaultGetAsync(HttpClient client, string url, string? token)
        => VaultSendAsync(client, HttpMethod.Get, url, token);

    private static Task<HttpResponseMessage> MoveInAsync(HttpClient client, string token, IEnumerable<Guid> fileIds)
        => VaultSendAsync(client, HttpMethod.Post, "/api/private-vault/move-in", token,
            new { fileIds = fileIds.ToArray(), folderIds = Array.Empty<Guid>() });

    private static Task<HttpResponseMessage> MoveOutAsync(HttpClient client, string token, IEnumerable<Guid> fileIds)
        => VaultSendAsync(client, HttpMethod.Post, "/api/private-vault/move-out", token,
            new { fileIds = fileIds.ToArray(), folderIds = Array.Empty<Guid>() });

    // Generates the medium (preview) derivative for a NORMAL owned file so the
    // vault endpoint has something existing to serve. Guarded so a broken setup
    // fails loudly.
    private static async Task GenerateMediumAsync(SqliteWebApplicationFactory factory, Guid ownerId, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var thumbs = scope.ServiceProvider.GetRequiredService<IFileThumbnailService>();
        var content = await thumbs.EnsureAsync(fileId, ownerId, ThumbnailSizes.Medium);
        Assert.NotNull(content);
        content!.Content.Dispose();
    }

    // Creates a video via the file service and generates its synthetic poster
    // (test host uses SyntheticVideoPosterProvider — no real FFmpeg).
    private static async Task<Guid> CreateVideoWithPosterAsync(SqliteWebApplicationFactory factory, Guid ownerId)
    {
        Guid videoId;
        using (var scope = factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var video = await files.CreateAsync(
                ownerId, null, "clip.mp4", "video/mp4",
                new MemoryStream(ImageFixtures.MinimalMp4("vv01")));
            videoId = video.Id;
        }
        using (var scope = factory.Services.CreateScope())
        {
            var thumbs = scope.ServiceProvider.GetRequiredService<IFileThumbnailService>();
            var outcome = await thumbs.EnsurePosterGeneratedAsync(videoId, ownerId);
            Assert.Equal(DerivativeOutcome.Generated, outcome);
        }
        return videoId;
    }

    private static async Task<int> ThumbnailRowCountAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileThumbnails.AsNoTracking().CountAsync();
    }

    // ── byte serving ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Thumbnail_And_Preview_Serve_Existing_Photo_Derivatives()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var thumb = await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token);
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType!.MediaType);
        Assert.True(thumb.Headers.CacheControl!.NoStore);
        Assert.True(thumb.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
        Assert.Contains("nosniff", nosniff);

        var preview = await VaultGetAsync(client, $"/api/private-vault/media/{photo}/preview", token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType!.MediaType);

        // The default thumbnail size (no query) is the small grid thumbnail.
        var defaultThumb = await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail", token);
        Assert.Equal(HttpStatusCode.OK, defaultThumb.StatusCode);
    }

    [Fact]
    public async Task Poster_Serves_Existing_Video_Poster()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var video = await CreateVideoWithPosterAsync(factory, ownerId);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { video })).EnsureSuccessStatusCode();

        var poster = await VaultGetAsync(client, $"/api/private-vault/media/{video}/poster", token);
        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);
        Assert.Equal("image/jpeg", poster.Content.Headers.ContentType!.MediaType);
        Assert.True(poster.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Missing_Derivative_Is_NotFound_And_Creates_Nothing()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        // A photo with ONLY the small thumbnail (upload) — no medium generated.
        var photo = await UploadPngAsync(client, "np.png", 300);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var before = await ThumbnailRowCountAsync(factory);
        var preview = await VaultGetAsync(client, $"/api/private-vault/media/{photo}/preview", token);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        // No lazy generation / enqueue may happen behind a vault view.
        Assert.Equal(before, await ThumbnailRowCountAsync(factory));
    }

    [Fact]
    public async Task Image_As_Poster_And_Video_As_Preview_Are_NotFound()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var video = await CreateVideoWithPosterAsync(factory, ownerId);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo, video })).EnsureSuccessStatusCode();

        // A photo has no poster derivative; a video has no medium preview.
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/poster", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{video}/preview", token)).StatusCode);
    }

    [Fact]
    public async Task Unknown_Thumbnail_Size_Is_NotFound()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=poster", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=huge", token)).StatusCode);
    }

    // ── authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Media_Requires_A_Valid_Token()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var url = $"/api/private-vault/media/{photo}/thumbnail?size=small";
        // No token header.
        Assert.Equal(HttpStatusCode.Unauthorized, (await VaultGetAsync(client, url, null)).StatusCode);
        // Garbage token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await VaultGetAsync(client, url, "garbage")).StatusCode);
        // Info endpoint too.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/info", null)).StatusCode);
    }

    [Fact]
    public async Task Media_Token_In_Query_String_Does_Not_Authorize()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var resp = await client.GetAsync(
            $"/api/private-vault/media/{photo}/thumbnail?size=small&X-Vault-Token={token}&token={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Expired_Token_Cannot_Fetch_Media()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PrivateVaultAccessTokens.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token)).StatusCode);
    }

    [Fact]
    public async Task Normal_Non_Vault_File_Is_NotFound_Through_Vault_Media()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        // Active normal file (never moved into the vault) — has a small thumbnail
        // but must be unreachable through the vault media endpoints.
        var active = await UploadPngAsync(client, "active.png", 300);
        await GenerateMediumAsync(factory, ownerId, active);

        // A second file marked Excluded but still NOT in the vault.
        var excluded = await UploadPngAsync(client, "excluded.png", 300);
        await GenerateMediumAsync(factory, ownerId, excluded);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.FileItems.SingleAsync(x => x.Id == excluded);
            f.MediaLibraryState = MediaLibraryState.Excluded;
            await db.SaveChangesAsync();
        }

        var token = await SetupAndUnlockAsync(client);

        foreach (var id in new[] { active, excluded })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await VaultGetAsync(client, $"/api/private-vault/media/{id}/thumbnail?size=small", token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await VaultGetAsync(client, $"/api/private-vault/media/{id}/info", token)).StatusCode);
        }
    }

    [Fact]
    public async Task Nonexistent_File_Is_Generic_NotFound()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var token = await SetupAndUnlockAsync(client);
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{missing}/thumbnail?size=small", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{missing}/info", token)).StatusCode);
    }

    [Fact]
    public async Task Foreign_Owner_Cannot_Fetch_Anothers_Vault_Media()
    {
        using var factory = NewFactory();
        var aliceId = await factory.SeedUserAsync("alice@example.com");
        var alice = await factory.LoginAsync("alice@example.com");
        await factory.SeedUserAsync("bob@example.com");
        var bob = await factory.LoginAsync("bob@example.com");

        var aliceFile = await UploadPngAsync(alice, "alice.png", 300);
        await GenerateMediumAsync(factory, aliceId, aliceFile);
        var aliceToken = await SetupAndUnlockAsync(alice);
        (await MoveInAsync(alice, aliceToken, new[] { aliceFile })).EnsureSuccessStatusCode();

        var url = $"/api/private-vault/media/{aliceFile}/thumbnail?size=small";
        // Bob presents Alice's token → owner-bound rejection (401).
        Assert.Equal(HttpStatusCode.Unauthorized, (await VaultGetAsync(bob, url, aliceToken)).StatusCode);

        // Bob unlocks his OWN vault, then asks for Alice's file id → 404 (not in
        // his vault), never a leak that the file exists elsewhere.
        var bobToken = await SetupAndUnlockAsync(bob);
        Assert.Equal(HttpStatusCode.NotFound, (await VaultGetAsync(bob, url, bobToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(bob, $"/api/private-vault/media/{aliceFile}/info", bobToken)).StatusCode);
    }

    // ── info DTO ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Info_Is_Sanitized_And_Uses_Title_As_Display_Name()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "original-name.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        (await client.PatchAsJsonAsync($"/api/files/{photo}/metadata", new
        {
            title = "Sunset",
            tags = new[] { "trip", "summer" },
            rating = 4,
            favorite = true,
        })).EnsureSuccessStatusCode();

        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var resp = await VaultGetAsync(client, $"/api/private-vault/media/{photo}/info", token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.CacheControl!.NoStore);
        var raw = await resp.Content.ReadAsStringAsync();
        var info = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("Sunset", info.GetProperty("title").GetString());
        Assert.Equal("Sunset", info.GetProperty("displayName").GetString());
        // Original filename is preserved for the details panel.
        Assert.Equal("original-name.png", info.GetProperty("name").GetString());
        Assert.Equal("image", info.GetProperty("mediaKind").GetString());
        Assert.Equal(4, info.GetProperty("rating").GetInt32());
        Assert.True(info.GetProperty("favorite").GetBoolean());
        Assert.True(info.GetProperty("previewAvailable").GetBoolean());
        Assert.Contains("trip", info.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Info_Falls_Back_To_Filename_When_No_Title()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "no-title.png", 300);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        var info = JsonDocument.Parse(await (await VaultGetAsync(
            client, $"/api/private-vault/media/{photo}/info", token)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("no-title.png", info.GetProperty("displayName").GetString());
        Assert.True(info.GetProperty("title").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Listing_Carries_Media_Kind_And_Display_Name()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "grid.png", 300);
        var video = await CreateVideoWithPosterAsync(factory, ownerId);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo, video })).EnsureSuccessStatusCode();

        var raw = await (await VaultGetAsync(client, "/api/private-vault/root", token)).Content.ReadAsStringAsync();
        var files = JsonDocument.Parse(raw).RootElement.GetProperty("files").EnumerateArray().ToList();

        var photoDto = files.Single(f => f.GetProperty("id").GetGuid() == photo);
        Assert.Equal("image", photoDto.GetProperty("mediaKind").GetString());
        Assert.Equal("grid.png", photoDto.GetProperty("displayName").GetString());
        Assert.True(photoDto.GetProperty("thumbnailAvailable").GetBoolean());

        var videoDto = files.Single(f => f.GetProperty("id").GetGuid() == video);
        Assert.Equal("video", videoDto.GetProperty("mediaKind").GetString());
        Assert.True(videoDto.GetProperty("posterAvailable").GetBoolean());

        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    // ── state transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Lock_Immediately_Blocks_Media()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token)).StatusCode);

        (await VaultSendAsync(client, HttpMethod.Post, "/api/private-vault/lock", token)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token)).StatusCode);
    }

    [Fact]
    public async Task Move_Out_Immediately_Blocks_Media()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "p.png", 300);
        await GenerateMediumAsync(factory, ownerId, photo);
        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token)).StatusCode);

        (await MoveOutAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        // Restored to the normal library → no longer reachable via vault media.
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/thumbnail?size=small", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await VaultGetAsync(client, $"/api/private-vault/media/{photo}/info", token)).StatusCode);
    }

    [Fact]
    public async Task Excluded_File_Round_Trips_Through_Vault_Preserving_Excluded()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var photo = await UploadPngAsync(client, "ex.png", 300);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.FileItems.SingleAsync(x => x.Id == photo);
            f.MediaLibraryState = MediaLibraryState.Excluded;
            await db.SaveChangesAsync();
        }

        var token = await SetupAndUnlockAsync(client);
        (await MoveInAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();
        (await MoveOutAsync(client, token, new[] { photo })).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.FileItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == photo);
            Assert.Null(f.PrivateVaultId);
            Assert.Equal(MediaLibraryState.Excluded, f.MediaLibraryState);
        }
    }
}

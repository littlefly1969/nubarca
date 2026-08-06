using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// Slice 53 — file & blob metadata model. Verifies the ownership boundaries:
// blob metadata is shared + immutable from user ops; user metadata is
// per-FileItem and never crosses users even when the blob is deduplicated.
public sealed class MetadataEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MetadataEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<FileItem> UploadAsync(
        Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    // ---- ownership / dedup -------------------------------------------------

    [Fact]
    public async Task Two_Users_Share_Dedup_Blob_With_Separate_User_Metadata()
    {
        var bytes = "shared-deduplicated-bytes"u8.ToArray();

        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, bytes, "alice.txt", "text/plain");

        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");
        var bobFile = await UploadAsync(bob, bytes, "bob.txt", "text/plain");

        // Same content => one physical blob, one shared blob-metadata row,
        // two distinct FileItems pointing at it.
        Assert.Equal(aliceFile.BlobObjectId, bobFile.BlobObjectId);
        Assert.Equal(1, await InDbAsync(db => db.BlobObjects.CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.BlobMetadata.CountAsync()));

        await PatchMetadataAsync(aliceClient, aliceFile.Id, new { title = "Alice's copy" });
        await PatchMetadataAsync(bobClient, bobFile.Id, new { title = "Bob's copy" });

        var aliceMeta = await GetMetadataAsync(aliceClient, aliceFile.Id);
        var bobMeta = await GetMetadataAsync(bobClient, bobFile.Id);

        Assert.Equal("Alice's copy", aliceMeta.User.Title);
        Assert.Equal("Bob's copy", bobMeta.User.Title);
    }

    [Fact]
    public async Task User_Cannot_See_Other_Users_File_Metadata()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, "a"u8.ToArray(), "secret.txt", "text/plain");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/metadata");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Updating_User_Metadata_Does_Not_Affect_Other_FileItem_On_Same_Blob()
    {
        var bytes = "dedup-isolation"u8.ToArray();

        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, bytes, "a.txt", "text/plain");

        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");
        var bobFile = await UploadAsync(bob, bytes, "b.txt", "text/plain");

        await PatchMetadataAsync(aliceClient, aliceFile.Id, new
        {
            title = "Alice",
            favorite = true,
            rating = 5,
            tags = new[] { "private", "alice-only" },
        });

        var bobMeta = await GetMetadataAsync(bobClient, bobFile.Id);
        Assert.Null(bobMeta.User.Title);
        Assert.False(bobMeta.User.Favorite);
        Assert.Null(bobMeta.User.Rating);
        Assert.Empty(bobMeta.User.Tags);

        // The shared blob-derived facts remain identical for both references.
        var aliceMeta = await GetMetadataAsync(aliceClient, aliceFile.Id);
        Assert.Equal(aliceMeta.Blob.MediaCategory, bobMeta.Blob.MediaCategory);
        Assert.Equal(aliceMeta.Blob.ExtractionStatus, bobMeta.Blob.ExtractionStatus);
        Assert.Equal(aliceMeta.Blob.ThumbnailStatus, bobMeta.Blob.ThumbnailStatus);
    }

    // ---- blob metadata immutability under logical ops ----------------------

    [Fact]
    public async Task Rename_And_Move_Do_Not_Change_Blob_Metadata_Or_Identity()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, CreatePngBytes(120, 80), "pic.png", "image/png");

        var before = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));

        // Rename (DB-only logical op).
        var rename = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/rename", new { name = "renamed.png" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        // Create a folder and move the file into it (DB-only logical op).
        var folderResp = await client.PostAsJsonAsync("/api/folders", new { name = "album" });
        var folderId = (await folderResp.Content.ReadFromJsonAsync<FolderIdProbe>())!.Id;
        var move = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/move", new { parentFolderId = folderId });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        // Blob identity + blob metadata are untouched by rename/move.
        var afterFile = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id));
        Assert.Equal(file.BlobObjectId, afterFile.BlobObjectId);

        var after = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.MediaCategory, after.MediaCategory);
        Assert.Equal(before.ExtractionStatus, after.ExtractionStatus);
        Assert.Equal(before.ThumbnailStatus, after.ThumbnailStatus);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(1, await InDbAsync(db => db.BlobMetadata.CountAsync()));
    }

    // ---- status fields + defaults ------------------------------------------

    [Fact]
    public async Task Image_Upload_Populates_Blob_Derived_Status_Fields_Safely()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, CreatePngBytes(64, 48), "img.png", "image/png");

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Equal("image", meta.Blob.MediaCategory);
        Assert.Equal(64, meta.Blob.Width);
        Assert.Equal(48, meta.Blob.Height);
        Assert.Equal(64L * 48, meta.Blob.PixelCount);
        // Slice 54: embedded extraction now runs on upload for images. A bare
        // PNG has no EXIF but the extractor still completes successfully.
        Assert.Equal("completed", meta.Blob.ExtractionStatus);
        // Thumbnail generated for a valid small PNG.
        Assert.Equal("generated", meta.Blob.ThumbnailStatus);
    }

    [Fact]
    public async Task NonImage_Upload_Skips_Thumbnail_And_Extraction()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "plain text body"u8.ToArray(), "notes.txt", "text/plain");

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Equal("document", meta.Blob.MediaCategory);
        Assert.Null(meta.Blob.Width);
        Assert.Null(meta.Blob.Height);
        Assert.Equal("skipped", meta.Blob.ThumbnailStatus);
        // Slice 54: a non-image has no embedded metadata to extract → skipped.
        Assert.Equal("skipped", meta.Blob.ExtractionStatus);
    }

    [Fact]
    public async Task Defaults_Apply_When_User_Metadata_Absent()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "x"u8.ToArray(), "doc.txt", "text/plain");

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Null(meta.User.Title);
        Assert.Null(meta.User.Description);
        Assert.Empty(meta.User.Tags);
        Assert.Null(meta.User.Rating);
        Assert.False(meta.User.Favorite);
        Assert.Null(meta.User.DateTakenOverride);
        Assert.Null(meta.User.LocationOverride);
    }

    [Fact]
    public async Task Defaults_Apply_For_File_Without_Blob_Metadata_Row()
    {
        // Simulate a file that predates the metadata model: drop its blob
        // metadata row, then read effective metadata — must still succeed with
        // safe fallbacks rather than 500.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "legacy"u8.ToArray(), "legacy.txt", "text/plain");

        await InDbAsync(async db =>
        {
            await db.BlobMetadata
                .Where(m => m.BlobObjectId == file.BlobObjectId)
                .ExecuteDeleteAsync();
            return 0;
        });

        var meta = await GetMetadataAsync(client, file.Id);
        Assert.Equal("document", meta.Blob.MediaCategory); // derived from MIME
        Assert.Equal("unknown", meta.Blob.ThumbnailStatus);
        Assert.Equal("pending", meta.Blob.ExtractionStatus);
        Assert.Empty(meta.User.Tags);
    }

    // ---- update validation + safety ----------------------------------------

    [Fact]
    public async Task Update_Replaces_User_Metadata_And_Roundtrips()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, CreatePngBytes(10, 10), "p.png", "image/png");

        await PatchMetadataAsync(client, file.Id, new
        {
            title = "  Trip 2026  ",
            description = "Beach photos",
            tags = new[] { "beach", "beach", " summer " },
            rating = 4,
            favorite = true,
            locationOverride = "Sardinia",
        });

        var meta = await GetMetadataAsync(client, file.Id);
        Assert.Equal("Trip 2026", meta.User.Title); // trimmed
        Assert.Equal("Beach photos", meta.User.Description);
        Assert.Equal(new[] { "beach", "summer" }, meta.User.Tags); // trimmed + deduped
        Assert.Equal(4, meta.User.Rating);
        Assert.True(meta.User.Favorite);
        Assert.Equal("Sardinia", meta.User.LocationOverride);

        // A second update with omitted fields clears them (full-replace).
        await PatchMetadataAsync(client, file.Id, new { title = "Only title" });
        var cleared = await GetMetadataAsync(client, file.Id);
        Assert.Equal("Only title", cleared.User.Title);
        Assert.Null(cleared.User.Description);
        Assert.Empty(cleared.User.Tags);
        Assert.Null(cleared.User.Rating);
        Assert.False(cleared.User.Favorite);
    }

    [Fact]
    public async Task Update_With_Invalid_Rating_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "r"u8.ToArray(), "r.txt", "text/plain");

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata", new { rating = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_For_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, "a"u8.ToArray(), "a.txt", "text/plain");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.PatchAsJsonAsync(
            $"/api/files/{aliceFile.Id}/metadata", new { title = "hijack" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/metadata");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_Response_Does_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, CreatePngBytes(20, 20), "leak.png", "image/png");
        await PatchMetadataAsync(client, file.Id, new { title = "t", tags = new[] { "x" } });

        var response = await client.GetAsync($"/api/files/{file.Id}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        var forbidden = new[]
        {
            "StorageKey", "storageKey", "storage_key",
            "Sha256", "sha256",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "RawMetadataJson", "rawMetadataJson", "raw_metadata_json",
            "TokenHash", "tokenHash", "token_hash",
            "objects/",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<FileMetadataResponse> GetMetadataAsync(HttpClient client, Guid fileId)
    {
        var response = await client.GetAsync($"/api/files/{fileId}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileMetadataResponse>())!;
    }

    private static async Task PatchMetadataAsync(HttpClient client, Guid fileId, object body)
    {
        var response = await client.PatchAsJsonAsync($"/api/files/{fileId}/metadata", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record FolderIdProbe(Guid Id);
}

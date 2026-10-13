using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Uploads;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Uploads;

// Uploading straight into an album: the file becomes the uploader's own in
// "Album/<album name>", then joins the album; bytes the uploader already owns
// are linked instead of stored again; a contributor uploads through the same
// route under the contribution rules; a viewer or a stranger cannot.
public sealed class AlbumUploadEndpointTests : IDisposable
{
    private const int Chunk = 1024 * 1024;

    private readonly SqliteWebApplicationFactory _factory;

    public AlbumUploadEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ResumableUploads:ChunkSizeBytes"] = Chunk.ToString(),
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string MemberEmail = "bob@example.com";
    private const string StrangerEmail = "carol@example.com";

    [Fact]
    public async Task Owner_Upload_Lands_In_The_Album_Folder_And_In_The_Album()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Estate 2026");

        var response = await UploadAsync(owner, albumId, Png("mare"), "mare.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fileId = body.GetProperty("id").GetGuid();
        Assert.Equal("mare.png", body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("linkedExisting").GetBoolean());
        Assert.False(body.GetProperty("alreadyInAlbum").GetBoolean());

        Assert.Equal(["Album", "Estate 2026"], await FolderPathOfAsync(fileId));
        var item = await AlbumItemAsync(albumId, fileId);
        Assert.NotNull(item);
        Assert.Equal(ownerId, item!.AddedByUserId);
    }

    [Fact]
    public async Task Bytes_The_Owner_Already_Has_Are_Linked_Not_Stored_Again()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mare");
        var bytes = Png("già mio");
        var existingId = await UploadToRootAsync(owner, bytes, "IMG_0001.png");
        var filesBefore = await CountFilesAsync(ownerId);

        var response = await UploadAsync(owner, albumId, bytes, "copia.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(existingId, body.GetProperty("id").GetGuid());
        Assert.True(body.GetProperty("linkedExisting").GetBoolean());
        Assert.Equal(filesBefore, await CountFilesAsync(ownerId));
        Assert.NotNull(await AlbumItemAsync(albumId, existingId));

        // Again: still one file, still one album item, and it says so.
        var again = await (await UploadAsync(owner, albumId, bytes, "copia.png")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(again.GetProperty("alreadyInAlbum").GetBoolean());
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
        Assert.Equal(filesBefore, await CountFilesAsync(ownerId));
    }

    [Fact]
    public async Task A_Name_Clash_In_The_Album_Folder_Is_Renamed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mare");

        await UploadAsync(owner, albumId, Png("uno"), "IMG.png");
        var second = await (await UploadAsync(owner, albumId, Png("due"), "IMG.png")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("IMG (2).png", second.GetProperty("name").GetString());
        Assert.Equal(2, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task A_Retry_With_The_Same_Key_Gives_The_Same_File_And_Still_Links_It()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mare");
        var bytes = Png("retry");

        var first = await (await UploadAsync(owner, albumId, bytes, "a.png", key: "web-album-1")).Content.ReadFromJsonAsync<JsonElement>();
        var fileId = first.GetProperty("id").GetGuid();

        // The first answer was lost after the file was stored but, say, the
        // album link was removed meanwhile: the retry replays and re-links.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AlbumItems.Where(ai => ai.AlbumId == albumId).ExecuteDeleteAsync();
        }
        var retry = await UploadAsync(owner, albumId, bytes, "a.png", key: "web-album-1");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(fileId, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.NotNull(await AlbumItemAsync(albumId, fileId));
        Assert.Equal(1, await CountFilesAsync(ownerId));
    }

    [Fact]
    public async Task A_Contributor_Uploads_Into_Their_Own_Space_And_The_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await InviteAcceptAsync(owner, bob, albumId, MemberEmail, "contributor");

        var response = await UploadAsync(bob, albumId, Png("bob"), "bob.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fileId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(bobId, (await db.FileItems.SingleAsync(f => f.Id == fileId)).OwnerUserId);
        Assert.Equal(bobId, (await db.AlbumItems.SingleAsync(ai => ai.AlbumId == albumId)).AddedByUserId);
        Assert.Equal(["Album", "Festa"], await FolderPathOfAsync(fileId));
    }

    [Fact]
    public async Task A_Viewer_Is_Refused_And_A_Stranger_Cannot_See_The_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var (carolId, carol) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await InviteAcceptAsync(owner, bob, albumId, MemberEmail, "viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(bob, albumId, Png("v"), "v.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(carol, albumId, Png("s"), "s.png")).StatusCode);
        Assert.Equal(0, await CountFilesAsync(bobId));
        Assert.Equal(0, await CountFilesAsync(carolId));
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task Something_That_Is_Not_A_Photo_Or_Video_Is_Refused_Before_Storing()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mare");

        var response = await UploadAsync(owner, albumId, "ciao"u8.ToArray(), "note.txt", "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("not_media", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await CountFilesAsync(ownerId));
    }

    [Fact]
    public async Task A_Photo_Sent_Without_A_Media_Type_Is_Recognised_By_Its_Extension()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Mare");

        var response = await UploadAsync(owner, albumId, Png("octet"), "IMG.png", "application/octet-stream");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("image/png", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task A_Large_File_Completes_Into_The_Album_Through_The_Resumable_Path()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Video");
        var payload = new byte[Chunk + 512];
        new Random(7).NextBytes(payload);

        var created = await owner.PostAsJsonAsync("/api/uploads/resumable",
            new { fileName = "clip.mp4", sizeBytes = payload.Length, contentType = "video/mp4" });
        var uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();
        for (var index = 0; index < 2; index++)
        {
            var start = index * Chunk;
            var part = new ByteArrayContent(payload, start, Math.Min(Chunk, payload.Length - start));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await owner.PutAsync($"/api/uploads/resumable/{uploadId}/chunks/{index}", part)).EnsureSuccessStatusCode();
        }

        var completed = await owner.PostAsJsonAsync($"/api/uploads/resumable/{uploadId}/complete", new { albumId });

        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        var fileId = (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotNull(await AlbumItemAsync(albumId, fileId));
        Assert.Equal(["Album", "Video"], await FolderPathOfAsync(fileId));
        Assert.Equal(1, await CountFilesAsync(ownerId));
    }

    [Fact]
    public async Task A_Resumable_Album_Completion_Refuses_A_Folder_Too_And_A_Viewer()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await InviteAcceptAsync(owner, bob, albumId, MemberEmail, "viewer");

        var created = await bob.PostAsJsonAsync("/api/uploads/resumable",
            new { fileName = "a.png", sizeBytes = 10, contentType = "image/png" });
        var uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync(
            $"/api/uploads/resumable/{uploadId}/complete", new { albumId, relativePath = "x/a.png" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync(
            $"/api/uploads/resumable/{uploadId}/complete", new { albumId })).StatusCode);
    }

    [Theory]
    [InlineData("Estate 2026", "Estate 2026")]
    [InlineData("Viaggi/Roma", "Viaggi Roma")]
    [InlineData("..", "Album")]
    [InlineData("  ", "Album")]
    [InlineData("Fine.", "Fine")]
    public void The_Album_Name_Becomes_One_Safe_Folder_Name(string albumName, string expected) =>
        Assert.Equal(expected, AlbumUploadIngestion.FolderNameFor(albumName));

    [Theory]
    [InlineData("a.jpg", "image/jpeg", "image/jpeg")]
    [InlineData("IMG_1.HEIC", "application/octet-stream", "image/heic")]
    [InlineData("clip.mov", "", "video/quicktime")]
    [InlineData("note.txt", "text/plain", null)]
    [InlineData("archive.zip", null, null)]
    public void Only_Photos_And_Videos_Have_A_Media_Type(string name, string? declared, string? expected) =>
        Assert.Equal(expected, AlbumUploadIngestion.MediaTypeOf(name, declared));

    // ── helpers ─────────────────────────────────────────────────────────────

    private static byte[] Png(string seed)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(seed.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, (byte)seed.Length, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid albumId, byte[] bytes, string name, string contentType = "image/png", string? key = null)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/albums/{albumId}/uploads")
        {
            Content = new MultipartFormDataContent { { part, "file", name } },
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<Guid> UploadToRootAsync(HttpClient client, byte[] bytes, string name)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var response = await client.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task InviteAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string role)
    {
        var invited = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members", new { email, role });
        invited.EnsureSuccessStatusCode();
        var membershipId = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("membershipId").GetGuid();
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null)).EnsureSuccessStatusCode();
    }

    private async Task<int> CountFilesAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.CountAsync(f => f.OwnerUserId == ownerId && f.DeletedAt == null);
    }

    private async Task<int> CountAlbumItemsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId);
    }

    private async Task<AlbumItem?> AlbumItemAsync(Guid albumId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.AsNoTracking().SingleOrDefaultAsync(ai => ai.AlbumId == albumId && ai.FileItemId == fileId);
    }

    // The file's folder chain from the root, by name.
    private async Task<string[]> FolderPathOfAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var folderId = (await db.FileItems.SingleAsync(f => f.Id == fileId)).ParentFolderId;
        var names = new List<string>();
        while (folderId is Guid id)
        {
            var folder = await db.Folders.SingleAsync(f => f.Id == id);
            names.Insert(0, folder.Name);
            folderId = folder.ParentFolderId;
        }
        return [.. names];
    }
}

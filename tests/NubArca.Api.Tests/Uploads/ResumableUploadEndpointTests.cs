using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Uploads;

namespace NubArca.Api.Tests.Uploads;

// The resumable owner upload: chunks in any order, a resend is a no-op, the
// server says what is missing, completion ingests exactly once into the folder
// the person chose, and nothing is visible before that.
public sealed class ResumableUploadEndpointTests : IDisposable
{
    // The smallest chunk the server hands out (the option is clamped to 1 MiB).
    private const int Chunk = 1024 * 1024;

    private readonly SqliteWebApplicationFactory _factory;

    public ResumableUploadEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ResumableUploads:ChunkSizeBytes"] = Chunk.ToString(),
            ["ResumableUploads:MaxActiveUploadsPerUser"] = "3",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static async Task<ResumableUploadResponse> CreateAsync(HttpClient client, string name, long size)
    {
        var response = await client.PostAsJsonAsync("/api/uploads/resumable", new { fileName = name, sizeBytes = size, contentType = "application/octet-stream" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResumableUploadResponse>())!;
    }

    private static async Task<HttpResponseMessage> PutChunkAsync(
        HttpClient client, Guid uploadId, int index, byte[] payload)
    {
        var start = index * Chunk;
        var length = Math.Min(Chunk, payload.Length - start);
        var content = new ByteArrayContent(payload, start, length);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PutAsync($"/api/uploads/resumable/{uploadId}/chunks/{index}", content);
    }

    private async Task<int> CountFilesAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.CountAsync(f => f.OwnerUserId == ownerId && f.DeletedAt == null);
    }

    [Fact]
    public async Task Chunks_In_Any_Order_Complete_Into_The_Chosen_Folder_Byte_For_Byte()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        Guid folderId;
        using (var scope = _factory.Services.CreateScope())
        {
            folderId = (await scope.ServiceProvider.GetRequiredService<IFolderService>()
                .CreateAsync(owner, null, "Trips")).Id;
        }
        var payload = Payload(Chunk * 2 + 1234);

        var upload = await CreateAsync(client, "clip.bin", payload.Length);
        Assert.Equal(3, upload.ExpectedChunkCount);
        Assert.Empty(upload.ReceivedChunks);

        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(client, upload.UploadId, 2, payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(client, upload.UploadId, 0, payload)).StatusCode);

        // Nothing is a file until completion, and an early completion is refused.
        Assert.Equal(0, await CountFilesAsync(owner));
        var early = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { parentFolderId = folderId });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("upload_incomplete", await early.Content.ReadAsStringAsync());

        // The server, not the client, knows what is missing.
        var state = await client.GetFromJsonAsync<ResumableUploadResponse>($"/api/uploads/resumable/{upload.UploadId}");
        Assert.Equal(new[] { 0, 2 }, state!.ReceivedChunks);

        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(client, upload.UploadId, 1, payload)).StatusCode);

        var complete = await client.PostAsJsonAsync(
            $"/api/uploads/resumable/{upload.UploadId}/complete", new { parentFolderId = folderId });
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
        var summary = await complete.Content.ReadFromJsonAsync<FileSummary>();
        Assert.Equal("clip.bin", summary!.Name);
        Assert.Equal(payload.LongLength, summary.SizeBytes);

        var downloaded = await client.GetByteArrayAsync($"/api/files/{summary.Id}/content");
        Assert.Equal(payload, downloaded);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.Id == summary.Id);
            Assert.Equal(folderId, file.ParentFolderId);
            // The temporary bytes are gone once the file is in.
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_factory.StorageRoot, "resumable"), "*.part"));
            Assert.Empty(await db.ResumableUploadChunks.ToListAsync());
        }
    }

    [Fact]
    public async Task A_Resent_Chunk_And_A_Repeated_Completion_Change_Nothing()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Payload(Chunk + 10);
        var upload = await CreateAsync(client, "twice.bin", payload.Length);

        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(client, upload.UploadId, 0, payload)).StatusCode);
        var resent = await PutChunkAsync(client, upload.UploadId, 0, payload);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        var resentBody = await resent.Content.ReadFromJsonAsync<ResumableChunkResponse>();
        Assert.True(resentBody!.AlreadyReceived);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(client, upload.UploadId, 1, payload)).StatusCode);

        var first = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstSummary = await first.Content.ReadFromJsonAsync<FileSummary>();

        // A lost response retried: the same file, not a second one.
        var again = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { });
        Assert.True(again.IsSuccessStatusCode);
        var againSummary = await again.Content.ReadFromJsonAsync<FileSummary>();
        Assert.Equal(firstSummary!.Id, againSummary!.Id);
        Assert.Equal(1, await CountFilesAsync(owner));
    }

    [Fact]
    public async Task A_Completion_Retried_After_Its_Replay_Record_Is_Gone_Keeps_Answering()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Payload(Chunk + 10);
        var upload = await CreateAsync(client, "late.bin", payload.Length);
        await PutChunkAsync(client, upload.UploadId, 0, payload);
        await PutChunkAsync(client, upload.UploadId, 1, payload);
        var first = await (await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { }))
            .Content.ReadFromJsonAsync<FileSummary>();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UploadOperations.ExecuteDeleteAsync();
        }

        // The upload row still names its file. Every retry answers with it,
        // rather than the first re-claim leaving the key stuck "in progress".
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var again = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { });
            Assert.True(again.IsSuccessStatusCode, $"attempt {attempt}: {again.StatusCode}");
            Assert.Equal(first!.Id, (await again.Content.ReadFromJsonAsync<FileSummary>())!.Id);
        }
    }

    [Fact]
    public async Task A_Chunk_Of_The_Wrong_Size_Is_Refused_And_Stays_Missing()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var upload = await CreateAsync(client, "short.bin", Chunk + 5);

        var shortChunk = new ByteArrayContent(new byte[100]);
        var response = await client.PutAsync($"/api/uploads/resumable/{upload.UploadId}/chunks/0", shortChunk);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var outOfRange = await client.PutAsync(
            $"/api/uploads/resumable/{upload.UploadId}/chunks/9", new ByteArrayContent(new byte[5]));
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        var state = await client.GetFromJsonAsync<ResumableUploadResponse>($"/api/uploads/resumable/{upload.UploadId}");
        Assert.Empty(state!.ReceivedChunks);
    }

    [Fact]
    public async Task Another_Owner_Cannot_See_Feed_Or_Complete_An_Upload()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var payload = Payload(10);
        var upload = await CreateAsync(alice, "mine.bin", payload.Length);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/uploads/resumable/{upload.UploadId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutChunkAsync(bob, upload.UploadId, 0, payload)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/uploads/resumable/{upload.UploadId}")).StatusCode);
    }

    [Fact]
    public async Task Completion_With_Rename_Gives_A_Taken_Name_A_Counter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var existing = new MultipartFormDataContent { { new ByteArrayContent("old"u8.ToArray()), "file", "video.mp4" } };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/files", existing)).StatusCode);

        var payload = Payload(20);
        var upload = await CreateAsync(client, "video.mp4", payload.Length);
        await PutChunkAsync(client, upload.UploadId, 0, payload);

        var failing = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { });
        Assert.Equal(HttpStatusCode.Conflict, failing.StatusCode);

        var renamed = await client.PostAsJsonAsync(
            $"/api/uploads/resumable/{upload.UploadId}/complete", new { conflict = "rename" });
        Assert.Equal(HttpStatusCode.Created, renamed.StatusCode);
        Assert.Equal("video (2).mp4", (await renamed.Content.ReadFromJsonAsync<FileSummary>())!.Name);
        Assert.Equal(2, await CountFilesAsync(owner));
    }

    [Fact]
    public async Task Relative_Path_Creates_Folders_At_Completion()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Payload(30);
        var upload = await CreateAsync(client, "a.bin", payload.Length);
        await PutChunkAsync(client, upload.UploadId, 0, payload);

        var complete = await client.PostAsJsonAsync(
            $"/api/uploads/resumable/{upload.UploadId}/complete", new { relativePath = "Holiday/2026/a.bin" });
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.Folders.Where(f => f.OwnerUserId == owner).Select(f => f.Name).ToListAsync();
        Assert.Contains("Holiday", names);
        Assert.Contains("2026", names);
    }

    [Fact]
    public async Task Create_Refuses_Bad_Input_And_Bounds_Unfinished_Uploads()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/uploads/resumable", new { fileName = "", sizeBytes = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/uploads/resumable", new { fileName = "x.bin", sizeBytes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/uploads/resumable", new { fileName = "..", sizeBytes = 5 })).StatusCode);

        for (var i = 0; i < 3; i++) await CreateAsync(client, $"f{i}.bin", 10);
        var fourth = await client.PostAsJsonAsync("/api/uploads/resumable", new { fileName = "f4.bin", sizeBytes = 10 });
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Contains("too_many_uploads", await fourth.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Directory_In_The_Client_Name_Never_Reaches_The_Disk_Or_The_File()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Payload(8);
        var upload = await CreateAsync(client, "../../etc/passwd", payload.Length);
        await PutChunkAsync(client, upload.UploadId, 0, payload);
        var complete = await client.PostAsJsonAsync($"/api/uploads/resumable/{upload.UploadId}/complete", new { });
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
        Assert.Equal("passwd", (await complete.Content.ReadFromJsonAsync<FileSummary>())!.Name);
    }

    [Fact]
    public async Task Delete_Discards_Bytes_And_Sweeper_Reclaims_Expired_Uploads()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var root = Path.Combine(_factory.StorageRoot, "resumable");

        var discarded = await CreateAsync(client, "gone.bin", 10);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/uploads/resumable/{discarded.UploadId}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(root, $"{discarded.UploadId:N}.part")));

        var abandoned = await CreateAsync(client, "old.bin", 10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ResumableUploads.Where(u => u.Id == abandoned.UploadId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        // Expired is gone for the client before the sweeper runs…
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/uploads/resumable/{abandoned.UploadId}")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var swept = await scope.ServiceProvider.GetRequiredService<IResumableUploadService>()
                .SweepExpiredAsync(CancellationToken.None);
            Assert.Equal(1, swept);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.ResumableUploads.AnyAsync(u => u.Id == abandoned.UploadId));
        }
        // …and its bytes are reclaimed by the sweep.
        Assert.False(File.Exists(Path.Combine(root, $"{abandoned.UploadId:N}.part")));
    }

    [Fact]
    public async Task Config_Reports_Enabled_With_The_Chunk_Size()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var config = await client.GetFromJsonAsync<JsonElement>("/api/uploads/resumable/config");
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.Equal(Chunk, config.GetProperty("chunkSizeBytes").GetInt32());
    }

    [Fact]
    public async Task Anonymous_Callers_Are_Refused()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/uploads/resumable", new { fileName = "x", sizeBytes = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class OwnerUploadNameTests
{
    [Theory]
    [InlineData("IMG_0001.jpg", new string[0], "IMG_0001.jpg")]
    [InlineData("IMG_0001.jpg", new[] { "IMG_0001.jpg" }, "IMG_0001 (2).jpg")]
    [InlineData("IMG_0001.jpg", new[] { "IMG_0001.jpg", "IMG_0001 (2).jpg" }, "IMG_0001 (3).jpg")]
    [InlineData("README", new[] { "README" }, "README (2)")]
    [InlineData(".bashrc", new[] { ".bashrc" }, ".bashrc (2)")]
    [InlineData("archive.tar.gz", new[] { "archive.tar.gz" }, "archive.tar (2).gz")]
    public void Next_Free_Name_Adds_The_First_Free_Counter(string requested, string[] taken, string expected)
    {
        Assert.Equal(expected, OwnerUploadIngestion.NextFreeName(requested, taken));
    }

    [Fact]
    public void Next_Free_Name_Stays_Within_The_Name_Limit()
    {
        var longName = new string('a', 251) + ".jpg";
        var result = OwnerUploadIngestion.NextFreeName(longName, [longName]);
        Assert.True(result.Length <= 255);
        Assert.EndsWith(" (2).jpg", result);
    }
}

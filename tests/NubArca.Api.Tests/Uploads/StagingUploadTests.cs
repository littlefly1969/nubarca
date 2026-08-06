using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Uploads;
using Xunit;

namespace NubArca.Api.Tests.Uploads;

// Slice 93 — web remote-staging upload: session lifecycle + authorization,
// manifest path/limit validation, resumable idempotent chunk uploads,
// verification, import handoff into the admin-import pipeline, cancellation,
// deletion/cleanup, and the no-leak posture of every new surface.
public sealed class StagingUploadTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-staging93-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    // Tiny 4-byte chunks so multi-chunk protocol tests stay readable.
    private static Dictionary<string, string?> Enabled(string root, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Staging:Enabled"] = "true",
            ["Staging:RootPath"] = root,
            ["Staging:MinChunkSizeBytes"] = "1",
            ["Staging:DefaultChunkSizeBytes"] = "4",
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) settings[k] = v;
        }
        return settings;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers -------------------------------------------------------------

    private static Task<(Guid UserId, HttpClient Client)> AuthAsync(
        SqliteWebApplicationFactory factory, string email = "owner@example.com")
    {
        factory.EnsureDatabaseCreated();
        return factory.CreateAuthenticatedClientAsync(email);
    }

    private static async Task<StagingSessionResponse> CreateSessionAsync(
        HttpClient client, object? body = null)
    {
        var resp = await client.PostAsJsonAsync("/api/uploads/staging/sessions", body ?? new { });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<StagingSessionResponse>())!;
    }

    private static async Task<StagingManifestResponse> SubmitManifestAsync(
        HttpClient client, Guid sessionId, params (string Path, long Size)[] files)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{sessionId}/manifest",
            new { files = files.Select(f => new { relativePath = f.Path, sizeBytes = f.Size, lastModifiedAt = (DateTime?)null }) });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<StagingManifestResponse>())!;
    }

    private static async Task<StagingMissingResponse> GetMissingAsync(HttpClient client, Guid sessionId)
        => (await client.GetFromJsonAsync<StagingMissingResponse>(
            $"/api/uploads/staging/sessions/{sessionId}/missing"))!;

    private static Task<HttpResponseMessage> PutChunkAsync(
        HttpClient client, Guid sessionId, Guid itemId, int chunkIndex, byte[] bytes)
        => client.PutAsync(
            $"/api/uploads/staging/sessions/{sessionId}/items/{itemId}/chunks/{chunkIndex}",
            new ByteArrayContent(bytes));

    // Uploads a whole file through the chunk protocol (4-byte chunks).
    private static async Task UploadFileAsync(
        HttpClient client, Guid sessionId, Guid itemId, byte[] content, int chunkSize = 4)
    {
        var chunkCount = content.Length == 0 ? 0 : (content.Length + chunkSize - 1) / chunkSize;
        for (var i = 0; i < chunkCount; i++)
        {
            var slice = content.Skip(i * chunkSize).Take(chunkSize).ToArray();
            var resp = await PutChunkAsync(client, sessionId, itemId, i, slice);
            resp.EnsureSuccessStatusCode();
        }
    }

    private static async Task<StagingVerifyResponse> VerifyAsync(HttpClient client, Guid sessionId)
    {
        var resp = await client.PostAsync($"/api/uploads/staging/sessions/{sessionId}/verify", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<StagingVerifyResponse>())!;
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory, int maxJobs = 10)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs);
    }

    private static async Task<StagingSessionResponse> GetSessionAsync(HttpClient client, Guid sessionId)
        => (await client.GetFromJsonAsync<StagingSessionResponse>(
            $"/api/uploads/staging/sessions/{sessionId}"))!;

    // ---- sessions + authorization ---------------------------------------------

    [Fact]
    public async Task Authenticated_User_Creates_Session_For_Self()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (userId, client) = await AuthAsync(factory);

        var session = await CreateSessionAsync(client, new { name = "Holiday photos" });
        Assert.Equal("draft", session.Status);
        Assert.Equal(userId, session.TargetUserId);
        Assert.Equal("Holiday photos", session.Name);
        Assert.True(session.ChunkSizeBytes > 0);
    }

    [Fact]
    public async Task Unauthenticated_Requests_Are_Rejected()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        factory.EnsureDatabaseCreated();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/uploads/staging/sessions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/uploads/staging/config")).StatusCode);
    }

    [Fact]
    public async Task Normal_User_Cannot_Target_Another_User_But_Admin_Can()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory, "plain@example.com");
        var otherId = await factory.SeedUserAsync("other@example.com");

        var forbidden = await client.PostAsJsonAsync(
            "/api/uploads/staging/sessions", new { targetUserId = otherId });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var adminId = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(adminId);
        var admin = await factory.LoginAsync("admin@example.com");
        var session = await CreateSessionAsync(admin, new { targetUserId = otherId });
        Assert.Equal(otherId, session.TargetUserId);
    }

    [Fact]
    public async Task Foreign_Session_Is_404_And_Disabled_Feature_Is_409()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, alice) = await AuthAsync(factory, "alice@example.com");
        var session = await CreateSessionAsync(alice);

        await factory.SeedUserAsync("bob@example.com");
        var bob = await factory.LoginAsync("bob@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/uploads/staging/sessions/{session.SessionId}")).StatusCode);

        using var disabled = new SqliteWebApplicationFactory();
        var (_, client) = await AuthAsync(disabled);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/uploads/staging/sessions", new { })).StatusCode);
    }

    // ---- manifest validation -----------------------------------------------------

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\windows\\evil.txt")]
    [InlineData("a//b.txt")]
    [InlineData("")]
    public async Task Manifest_Rejects_Unsafe_Paths(string path)
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);

        var resp = await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{session.SessionId}/manifest",
            new { files = new[] { new { relativePath = path, sizeBytes = 4L } } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Manifest_Enforces_File_Count_Session_And_File_Size_Limits()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?>
            {
                ["Staging:MaxFilesPerSession"] = "2",
                ["Staging:MaxSessionBytes"] = "100",
                ["Staging:MaxFileBytes"] = "50",
            }));
        var (_, client) = await AuthAsync(factory);

        // Too many files.
        var s1 = await CreateSessionAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{s1.SessionId}/manifest",
            new { files = new[] { Mf("a", 1), Mf("b", 1), Mf("c", 1) } })).StatusCode);

        // Per-file limit.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{s1.SessionId}/manifest",
            new { files = new[] { Mf("big.bin", 51) } })).StatusCode);

        // Per-session limit (each file is fine; the total is not).
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{s1.SessionId}/manifest",
            new { files = new[] { Mf("x.bin", 50), Mf("y.bin", 51 ) } })).StatusCode);

        static object Mf(string path, long size) => new { relativePath = path, sizeBytes = size };
    }

    [Fact]
    public async Task Manifest_Per_File_Limit_Aligns_With_Storage_MaxUploadBytes()
    {
        // Storage cap (8) below the staging cap (50): staging must refuse a
        // file the import would later reject.
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot(),
            new Dictionary<string, string?>
            {
                ["Staging:MaxFileBytes"] = "50",
                ["Storage:MaxUploadBytes"] = "8",
            }));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);

        var config = await client.GetFromJsonAsync<StagingConfigResponse>("/api/uploads/staging/config");
        Assert.Equal(8, config!.MaxFileBytes);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/uploads/staging/sessions/{session.SessionId}/manifest",
            new { files = new[] { new { relativePath = "video.mp4", sizeBytes = 9L } } })).StatusCode);
    }

    // ---- chunk protocol -----------------------------------------------------------

    [Fact]
    public async Task Chunks_Write_Expected_Bytes_And_Complete_The_Item()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("docs/hello.txt", 10));

        var missing = await GetMissingAsync(client, session.SessionId);
        var item = Assert.Single(missing.Items);
        Assert.Equal(new[] { 0, 1, 2 }, item.MissingChunks); // 10 bytes / 4 = 3 chunks

        var content = "0123456789"u8.ToArray();
        await UploadFileAsync(client, session.SessionId, item.ItemId, content);

        // The staged file holds exactly the uploaded bytes.
        var staged = Path.Combine(root, session.SessionId.ToString("N"), "files", "docs", "hello.txt");
        Assert.True(File.Exists(staged));
        Assert.Equal(content, await File.ReadAllBytesAsync(staged));

        var detail = await GetSessionAsync(client, session.SessionId);
        Assert.Equal("uploading", detail.Status);
        Assert.Equal(1, detail.ReceivedFiles);
        Assert.Equal(10, detail.ReceivedBytes);
    }

    [Fact]
    public async Task Chunk_Upload_Is_Idempotent_And_Resume_Skips_Received_Chunks()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 12));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];

        // Upload the middle chunk only.
        var ok = await PutChunkAsync(client, session.SessionId, item.ItemId, 1, "BBBB"u8.ToArray());
        ok.EnsureSuccessStatusCode();
        var first = await ok.Content.ReadFromJsonAsync<StagingChunkResponse>();
        Assert.False(first!.AlreadyReceived);
        Assert.Equal(1, first.ReceivedChunkCount);

        // Re-uploading it (e.g. the client missed the response) is a safe no-op.
        var again = await PutChunkAsync(client, session.SessionId, item.ItemId, 1, "BBBB"u8.ToArray());
        again.EnsureSuccessStatusCode();
        var second = await again.Content.ReadFromJsonAsync<StagingChunkResponse>();
        Assert.True(second!.AlreadyReceived);
        Assert.Equal(1, second.ReceivedChunkCount); // counters unchanged

        // Resume: the server reports exactly the still-missing chunks.
        var missing = (await GetMissingAsync(client, session.SessionId)).Items[0];
        Assert.Equal(new[] { 0, 2 }, missing.MissingChunks);
    }

    [Fact]
    public async Task Out_Of_Range_And_Wrong_Size_Chunks_Are_Rejected()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 10));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];

        // Index out of range (10 bytes / 4 = 3 chunks: 0..2).
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, 3, "AAAA"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, -1, "AAAA"u8.ToArray())).StatusCode);

        // Wrong size: middle chunks must be exactly chunk-sized, the final
        // chunk exactly the remainder (10 - 2*4 = 2).
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, 0, "AA"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, 2, "AAAA"u8.ToArray())).StatusCode);
    }

    // ---- verification --------------------------------------------------------------

    [Fact]
    public async Task Verify_Fails_While_Chunks_Are_Missing_And_Succeeds_When_Complete()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("a.txt", 4), ("sub/b.txt", 6));
        var items = (await GetMissingAsync(client, session.SessionId)).Items;

        await UploadFileAsync(client, session.SessionId, items[0].ItemId, "AAAA"u8.ToArray());
        var incomplete = await VerifyAsync(client, session.SessionId);
        Assert.False(incomplete.ReadyToImport);
        Assert.Equal(1, incomplete.VerifiedFiles);
        Assert.Equal(1, incomplete.IncompleteFiles);
        Assert.Equal("uploading", incomplete.Status);

        await UploadFileAsync(client, session.SessionId, items[1].ItemId, "BBBBBB"u8.ToArray());
        var complete = await VerifyAsync(client, session.SessionId);
        Assert.True(complete.ReadyToImport);
        Assert.Equal(2, complete.VerifiedFiles);
        Assert.Equal("ready_to_import", complete.Status);
    }

    [Fact]
    public async Task Verify_Detects_Corrupt_Staged_Bytes_And_Resets_The_Item_For_ReUpload()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("damaged.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "GOOD"u8.ToArray());

        // Tamper with the staged file behind the API's back (size drift).
        var staged = Path.Combine(root, session.SessionId.ToString("N"), "files", "damaged.bin");
        await File.WriteAllBytesAsync(staged, "TOO-LONG"u8.ToArray());

        var result = await VerifyAsync(client, session.SessionId);
        Assert.False(result.ReadyToImport);
        Assert.Equal(1, result.CorruptFiles);

        // The item went back to pending with all chunks missing — re-uploadable.
        var missing = (await GetMissingAsync(client, session.SessionId)).Items;
        Assert.Equal(item.ItemId, Assert.Single(missing).ItemId);
        Assert.Equal(new[] { 0 }, missing[0].MissingChunks);
    }

    [Fact]
    public async Task Zero_Byte_Files_Complete_Without_Chunks_And_Materialize_At_Verify()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        var manifest = await SubmitManifestAsync(client, session.SessionId, ("empty.txt", 0));
        Assert.Equal(1, manifest.AlreadyCompleteFiles);

        Assert.Empty((await GetMissingAsync(client, session.SessionId)).Items);
        var verify = await VerifyAsync(client, session.SessionId);
        Assert.True(verify.ReadyToImport);
        var staged = Path.Combine(root, session.SessionId.ToString("N"), "files", "empty.txt");
        Assert.True(File.Exists(staged));
        Assert.Equal(0, new FileInfo(staged).Length);
    }

    // ---- cancel / expiry / delete -----------------------------------------------------

    [Fact]
    public async Task Cancel_Blocks_Further_Uploads()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];

        var cancel = await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/cancel", null);
        cancel.EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await GetSessionAsync(client, session.SessionId)).Status);

        Assert.Equal(HttpStatusCode.Conflict,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, 0, "AAAA"u8.ToArray())).StatusCode);
    }

    [Fact]
    public async Task Expired_Session_Cannot_Be_Resumed()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemoteUploadSessions.Where(s => s.Id == session.SessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }

        Assert.Equal("expired", (await GetSessionAsync(client, session.SessionId)).Status);
        Assert.Equal(HttpStatusCode.Conflict,
            (await PutChunkAsync(client, session.SessionId, item.ItemId, 0, "AAAA"u8.ToArray())).StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Rows_And_Staging_Files()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "AAAA"u8.ToArray());

        var sessionDir = Path.Combine(root, session.SessionId.ToString("N"));
        Assert.True(Directory.Exists(sessionDir));

        var delete = await client.DeleteAsync($"/api/uploads/staging/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(Directory.Exists(sessionDir));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/uploads/staging/sessions/{session.SessionId}")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.RemoteUploadItems.CountAsync());
        Assert.Equal(0, await db.RemoteUploadChunks.CountAsync());
    }

    [Fact]
    public async Task Cleanup_Sweeper_Expires_And_Reclaims_Overdue_Sessions()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["Staging:CleanupEnabled"] = "true" }));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("file.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "AAAA"u8.ToArray());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemoteUploadSessions.Where(s => s.Id == session.SessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }

        var sweeper = factory.Services.GetRequiredService<StagingCleanupService>();
        var deleted = await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(root, session.SessionId.ToString("N"))));

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await vdb.RemoteUploadSessions.CountAsync());
    }

    // ---- import handoff ---------------------------------------------------------------

    [Fact]
    public async Task Import_Handoff_Creates_Run_And_Job_And_Preserves_Folder_Structure()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (userId, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId,
            ("photos/2024/trip/a.txt", 4), ("photos/2024/trip/b.txt", 4), ("root.txt", 4));
        foreach (var item in (await GetMissingAsync(client, session.SessionId)).Items)
        {
            await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        }
        Assert.True((await VerifyAsync(client, session.SessionId)).ReadyToImport);

        var import = await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null);
        import.EnsureSuccessStatusCode();
        var started = await import.Content.ReadFromJsonAsync<StagingImportStartResponse>();
        Assert.Equal("importing", started!.Status);
        Assert.NotEqual(Guid.Empty, started.AdminImportRunId);
        Assert.NotEqual(Guid.Empty, started.JobId);

        // The handoff pre-populated the import manifest — no second scan.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.AdminImportRuns.AsNoTracking().FirstAsync(r => r.Id == started.AdminImportRunId);
            Assert.Equal(session.SessionId, run.StagingSessionId);
            Assert.NotNull(run.ScanCompletedAt);
            Assert.Equal(3, run.ScannedFiles);
            Assert.Equal(3, await db.AdminImportItems.CountAsync(i => i.ImportRunId == run.Id));
        }

        await ProcessJobsAsync(factory);

        // Files landed with their folder structure in the target library.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var folders = await db.Folders
                .Where(f => f.OwnerUserId == userId && f.DeletedAt == null)
                .Select(f => f.Name).ToListAsync();
            Assert.Contains("photos", folders);
            Assert.Contains("2024", folders);
            Assert.Contains("trip", folders);
            Assert.Equal(3, await db.FileItems.CountAsync(f => f.OwnerUserId == userId && f.DeletedAt == null));
        }

        // The session reports the outcome (fully successful → imported).
        var detail = await GetSessionAsync(client, session.SessionId);
        Assert.Equal("imported", detail.Status);
        Assert.NotNull(detail.Import);
        Assert.Equal("succeeded", detail.Import!.Status);
        Assert.Equal(3, detail.Import.ImportedFiles);
    }

    [Fact]
    public async Task Import_Respects_Target_User_Quota()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot(),
            new Dictionary<string, string?> { ["Storage:DefaultUserQuotaBytes"] = "6" }));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("a.bin", 4), ("b.bin", 4));
        foreach (var item in (await GetMissingAsync(client, session.SessionId)).Items)
        {
            await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        }
        await VerifyAsync(client, session.SessionId);
        (await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null))
            .EnsureSuccessStatusCode();
        await ProcessJobsAsync(factory);

        // One file fits the 6-byte quota; the second fails with quota_exceeded
        // → run partial → session imported-with-warning, staging kept.
        var detail = await GetSessionAsync(client, session.SessionId);
        Assert.Equal("imported", detail.Status);
        Assert.Equal("partial_import", detail.LastErrorCode);
        Assert.Equal(1, detail.Import!.ImportedFiles);
        Assert.Equal(1, detail.Import.FailedFiles);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failed = await db.AdminImportItems.AsNoTracking()
            .FirstAsync(i => i.Status == "failed");
        Assert.Equal("quota_exceeded", failed.FailureCategory);
    }

    [Fact]
    public async Task Cancelling_An_Importing_Session_Cancels_The_Run_And_Syncs_Back()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("a.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        await VerifyAsync(client, session.SessionId);
        (await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null))
            .EnsureSuccessStatusCode();

        // Cancel BEFORE the job runs: the queued job is flagged and will never
        // execute; the freeze path syncs the session immediately.
        var cancel = await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/cancel", null);
        cancel.EnsureSuccessStatusCode();
        var body = await cancel.Content.ReadFromJsonAsync<StagingCancelResponse>();
        Assert.True(body!.CancellationRequested);
        Assert.Equal("cancelled", (await GetSessionAsync(client, session.SessionId)).Status);

        await ProcessJobsAsync(factory); // the flagged job finishes as cancelled, imports nothing
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync());
    }

    [Fact]
    public async Task Delete_Is_Blocked_While_Importing()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("a.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        await VerifyAsync(client, session.SessionId);
        (await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/uploads/staging/sessions/{session.SessionId}")).StatusCode);
    }

    // ---- failure reconciliation (slice 97, bug 2) ----------------------------------------

    // Drives a 1-file session to ready_to_import and starts the import.
    private static async Task<Guid> StartOneFileImportAsync(HttpClient client)
    {
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("a.bin", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        await VerifyAsync(client, session.SessionId);
        (await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null))
            .EnsureSuccessStatusCode();
        return session.SessionId;
    }

    [Fact]
    public async Task Failed_Import_Marks_Session_Failed_And_Allows_Discard()
    {
        var root = NewStagingRoot();
        // One attempt = the first failure is permanent (no retry waiting).
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["Jobs:DefaultMaxAttempts"] = "1" }));
        var (_, client) = await AuthAsync(factory);
        var sessionId = await StartOneFileImportAsync(client);

        // Sabotage the staged files so the run fails validation when executed.
        Directory.Delete(Path.Combine(root, sessionId.ToString("N"), "files"), recursive: true);
        await ProcessJobsAsync(factory);

        // The failure propagated run → session (the bug left it `importing`).
        var detail = await GetSessionAsync(client, sessionId);
        Assert.Equal("failed", detail.Status);
        Assert.Equal("import_failed", detail.LastErrorCode);
        Assert.NotNull(detail.Import);
        Assert.Equal("failed", detail.Import!.Status);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.RemoteUploadSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.NotNull(session.CompletedAt);
            var run = await db.AdminImportRuns.AsNoTracking().SingleAsync(r => r.StagingSessionId == sessionId);
            Assert.Equal("failed", run.Status);
            Assert.Equal("The staging session files are missing.", run.ErrorSummary);

            // No blob reference leaked along the failure path.
            var audit = await scope.ServiceProvider
                .GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(0, audit.DbRefcountTooHigh + audit.DbRefcountTooLow);
        }

        // A failed session is discardable (the bug blocked this forever).
        var delete = await client.DeleteAsync($"/api/uploads/staging/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.RemoteUploadSessions.AnyAsync(s => s.Id == sessionId));
        }
        Assert.False(Directory.Exists(Path.Combine(root, sessionId.ToString("N"))));
    }

    [Fact]
    public async Task Stale_Importing_Session_With_Terminal_Job_Is_Discardable()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["Jobs:DefaultMaxAttempts"] = "1" }));
        var (_, client) = await AuthAsync(factory);
        var sessionId = await StartOneFileImportAsync(client);
        Directory.Delete(Path.Combine(root, sessionId.ToString("N"), "files"), recursive: true);
        await ProcessJobsAsync(factory);

        // Recreate the PRE-FIX stuck state: run/job terminally failed but the
        // session row still says `importing` (legacy data from before the
        // failure-sync existed).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemoteUploadSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "importing")
                    .SetProperty(x => x.CompletedAt, (DateTime?)null));
        }

        // The resilient delete inspects the linked run/job, sees them terminal,
        // and allows the discard instead of returning 409 forever.
        var delete = await client.DeleteAsync($"/api/uploads/staging/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Stale_Importing_Session_With_Missing_Run_Is_Discardable()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);

        // Bookkeeping gone wrong: `importing` but the linked run row never
        // existed / was removed.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemoteUploadSessions.Where(s => s.Id == session.SessionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "importing")
                    .SetProperty(x => x.AdminImportRunId, Guid.NewGuid()));
        }

        var delete = await client.DeleteAsync($"/api/uploads/staging/sessions/{session.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Delete_Still_Refused_While_The_Linked_Job_Is_Alive()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewStagingRoot()));
        var (_, client) = await AuthAsync(factory);
        var sessionId = await StartOneFileImportAsync(client);

        // Import job is queued (not yet processed): the staged files may still
        // be read — delete must keep refusing exactly as before.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/uploads/staging/sessions/{sessionId}")).StatusCode);
    }

    [Fact]
    public async Task Cleanup_Sweeper_Reclaims_A_Failed_Session_After_Expiry()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?>
            {
                ["Jobs:DefaultMaxAttempts"] = "1",
                ["Staging:CleanupEnabled"] = "true",
            }));
        var (_, client) = await AuthAsync(factory);
        var sessionId = await StartOneFileImportAsync(client);
        Directory.Delete(Path.Combine(root, sessionId.ToString("N"), "files"), recursive: true);
        await ProcessJobsAsync(factory);
        Assert.Equal("failed", (await GetSessionAsync(client, sessionId)).Status);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RemoteUploadSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-5)));
        }

        var sweeper = factory.Services.GetRequiredService<StagingCleanupService>();
        var reclaimed = await sweeper.RunOnceAsync(CancellationToken.None);
        Assert.True(reclaimed >= 1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.RemoteUploadSessions.AnyAsync(s => s.Id == sessionId));
        }
        Assert.False(Directory.Exists(Path.Combine(root, sessionId.ToString("N"))));
    }

    // ---- no-leak ------------------------------------------------------------------------

    [Fact]
    public async Task Staging_Responses_Do_Not_Leak_Paths_Or_Internals()
    {
        var root = NewStagingRoot();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AuthAsync(factory);
        var session = await CreateSessionAsync(client);
        await SubmitManifestAsync(client, session.SessionId, ("sub/a.txt", 4));
        var item = (await GetMissingAsync(client, session.SessionId)).Items[0];
        await UploadFileAsync(client, session.SessionId, item.ItemId, "DATA"u8.ToArray());
        await VerifyAsync(client, session.SessionId);
        (await client.PostAsync($"/api/uploads/staging/sessions/{session.SessionId}/import", null))
            .EnsureSuccessStatusCode();
        await ProcessJobsAsync(factory);

        var bodies = new[]
        {
            await (await client.GetAsync("/api/uploads/staging/config")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/uploads/staging/sessions")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/uploads/staging/sessions/{session.SessionId}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/uploads/staging/sessions/{session.SessionId}/items")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/uploads/staging/sessions/{session.SessionId}/missing")).Content.ReadAsStringAsync(),
        };
        foreach (var body in bodies)
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(root, body, StringComparison.Ordinal);       // staging root
            Assert.DoesNotContain(factory.StorageRoot, body, StringComparison.Ordinal); // blob root
            Assert.DoesNotContain("payloadJson", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

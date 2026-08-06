using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.PhotoExport;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.PhotoExport;

// Owner-private photo-archive export: session creation, snapshot/manifest build,
// tree preservation, eligibility, token/owner access, revoke/expire, streaming,
// and no-leak. Exercises the real HTTP + upload + background-job stack.
public sealed class PhotoExportTests
{
    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "TokenHash", "tokenHash", "/storage/objects/", "FileItemId", "fileItemId",
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

    private static async Task<Guid> UploadTextAsync(HttpClient client, string name, Guid? folderId = null)
    {
        var part = new ByteArrayContent("hello world"u8.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
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

    private static async Task RunBuildAsync(SqliteWebApplicationFactory factory)
    {
        for (var i = 0; i < 200; i++)
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(10) == 0) break;
        }
    }

    private sealed record Created(Guid SessionId, string Token, string Status, DateTime ExpiresAt);
    private sealed record SessionStatus(
        Guid SessionId, string Status, int FileCount, long TotalBytes, string? ErrorSummary,
        DateTime CreatedAt, DateTime? CompletedAt, DateTime ExpiresAt, bool ManifestReady);

    private static async Task<Created> CreateSessionAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/photo-exports", null);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<Created>())!;
    }

    // Read the whole JSONL manifest using a token-only (unauthenticated) client.
    private static async Task<(HttpStatusCode Status, List<JsonElement> Lines, string Raw)> GetManifestAsync(
        SqliteWebApplicationFactory factory, Guid sessionId, string token)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync($"/api/photo-exports/{sessionId}/manifest");
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            return (resp.StatusCode, new(), string.Empty);
        }
        var raw = await resp.Content.ReadAsStringAsync();
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
        return (resp.StatusCode, lines, raw);
    }

    [Fact]
    public async Task Create_Requires_Authenticated_Owner()
    {
        using var factory = NewFactory();
        using var anon = factory.CreateClient();
        var resp = await anon.PostAsync("/api/photo-exports", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Manifest_Contains_Only_Owner_Visible_Photos_Preserving_Tree()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        // Tree: /root.png, /Trip/day.png, /Trip/Day1/deep.png, plus a non-photo
        // and a soft-deleted photo that must NOT appear.
        await UploadPngAsync(client, "root.png", 10);
        var trip = await CreateFolderAsync(client, "Trip");
        await UploadPngAsync(client, "day.png", 12, trip);
        var day1 = await CreateFolderAsync(client, "Day1", trip);
        await UploadPngAsync(client, "deep.png", 14, day1);
        await UploadTextAsync(client, "notes.txt"); // non-photo → excluded
        var deleted = await UploadPngAsync(client, "gone.png", 16);
        (await client.DeleteAsync($"/api/files/{deleted}")).EnsureSuccessStatusCode(); // soft-deleted → excluded

        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        var (status, lines, _) = await GetManifestAsync(factory, created.SessionId, created.Token);
        Assert.Equal(HttpStatusCode.OK, status);

        var paths = lines.Select(l => l.GetProperty("relativePath").GetString()!).ToList();
        Assert.Equal(
            new HashSet<string> { "Trip/Day1/deep.png", "Trip/day.png", "root.png" },
            paths.ToHashSet());
        // No date reorganization: paths are the logical tree, never yyyy/MM/dd.
        Assert.DoesNotContain(paths, p => System.Text.RegularExpressions.Regex.IsMatch(p, @"\d{4}/\d{2}"));
        // No non-photo, no soft-deleted.
        Assert.DoesNotContain(paths, p => p.EndsWith("notes.txt"));
        Assert.DoesNotContain(paths, p => p.EndsWith("gone.png"));
    }

    [Fact]
    public async Task Eligibility_Is_Centralized_And_Selects_Only_Photos()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadTextAsync(client, "c.txt");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var eligible = await PhotoExportEligibility.EligiblePhotos(db, ownerId).CountAsync();
        Assert.Equal(2, eligible); // the two images only
    }

    [Fact]
    public async Task Foreign_Owner_Cannot_Access_Session_Manifest_Or_File()
    {
        using var factory = NewFactory();
        await factory.SeedUserAsync("a@example.com");
        var clientA = await factory.LoginAsync("a@example.com");
        await factory.SeedUserAsync("b@example.com");
        var clientB = await factory.LoginAsync("b@example.com");

        await UploadPngAsync(clientA, "a.png", 10);
        var created = await CreateSessionAsync(clientA);
        await RunBuildAsync(factory);

        // B's cookie cannot read A's session status.
        var bStatus = await clientB.GetAsync($"/api/photo-exports/{created.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, bStatus.StatusCode);

        // Manifest with no token (B has no token) → 404.
        using var noToken = factory.CreateClient();
        var noTokenResp = await noToken.GetAsync($"/api/photo-exports/{created.SessionId}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, noTokenResp.StatusCode);

        // Wrong token → 404.
        var (wrong, _, _) = await GetManifestAsync(factory, created.SessionId, "not-the-token");
        Assert.Equal(HttpStatusCode.NotFound, wrong);
    }

    [Fact]
    public async Task Revoked_Session_Cannot_Be_Used()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        (await client.DeleteAsync($"/api/photo-exports/{created.SessionId}")).EnsureSuccessStatusCode();

        var (status, _, _) = await GetManifestAsync(factory, created.SessionId, created.Token);
        Assert.Equal(HttpStatusCode.NotFound, status);

        var owner = await client.GetFromJsonAsync<SessionStatus>($"/api/photo-exports/{created.SessionId}");
        Assert.Equal("revoked", owner!.Status);
    }

    [Fact]
    public async Task Expired_Session_Cannot_Be_Used()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        // Force expiry in the DB (no time-travel needed).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PhotoExportSessions
                .Where(s => s.Id == created.SessionId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
        }

        var (status, _, _) = await GetManifestAsync(factory, created.SessionId, created.Token);
        Assert.Equal(HttpStatusCode.NotFound, status);

        var owner = await client.GetFromJsonAsync<SessionStatus>($"/api/photo-exports/{created.SessionId}");
        Assert.Equal("expired", owner!.Status);
    }

    [Fact]
    public async Task File_Streaming_Returns_Original_Bytes()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var expected = Png(10);
        // Upload the exact bytes we will compare against.
        var part = new ByteArrayContent(expected);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var resp0 = await client.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", "exact.png" } });
        resp0.EnsureSuccessStatusCode();

        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        var (status, lines, _) = await GetManifestAsync(factory, created.SessionId, created.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        var entry = Assert.Single(lines);
        var downloadUrl = entry.GetProperty("downloadUrl").GetString()!;

        using var tokenClient = factory.CreateClient();
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        var fileResp = await tokenClient.GetAsync(downloadUrl);
        Assert.Equal(HttpStatusCode.OK, fileResp.StatusCode);
        var got = await fileResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(expected, got);
    }

    [Fact]
    public async Task Manifest_And_Status_Expose_No_Internals_And_EntryId_Is_Opaque()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "a.png", 10);
        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        var (_, lines, raw) = await GetManifestAsync(factory, created.SessionId, created.Token);
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
        // entryId is an opaque id, NOT the FileItemId.
        var entryId = Assert.Single(lines).GetProperty("entryId").GetString()!;
        Assert.NotEqual(fileId.ToString("N"), entryId);

        var statusResp = await client.GetAsync($"/api/photo-exports/{created.SessionId}");
        var statusRaw = await statusResp.Content.ReadAsStringAsync();
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, statusRaw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Snapshot_Builds_Across_Slices_For_Large_Library()
    {
        // Tiny slice budget forces the build to continue across many slices,
        // proving the snapshot is batched/bounded (never one giant in-memory pass).
        using var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Jobs:MaintenanceSliceItemBudget"] = "5" });
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        const int count = 23;
        for (var i = 0; i < count; i++)
        {
            await UploadPngAsync(client, $"p{i}.png", 10 + i);
        }

        var created = await CreateSessionAsync(client);
        await RunBuildAsync(factory);

        var status = await client.GetFromJsonAsync<SessionStatus>($"/api/photo-exports/{created.SessionId}");
        Assert.Equal("ready", status!.Status);
        Assert.Equal(count, status.FileCount);

        var (_, lines, _) = await GetManifestAsync(factory, created.SessionId, created.Token);
        Assert.Equal(count, lines.Count);
        // Manifest paths are unique (logical-tree invariant: no collisions).
        var paths = lines.Select(l => l.GetProperty("relativePath").GetString()!).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public async Task Token_Is_Stored_Hashed_Never_Raw()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        var created = await CreateSessionAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PhotoExportSessions.AsNoTracking().SingleAsync(s => s.Id == created.SessionId);
        Assert.NotEqual(created.Token, row.TokenHash);
        Assert.Equal(PhotoExportService.HashToken(created.Token), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
    }
}

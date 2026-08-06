using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 65: app-level upload size limit + per-user logical quota + the
// owner-scoped GET /api/storage/me accounting endpoint.
public sealed class StorageQuotaTests
{
    private static MultipartFormDataContent Multipart(byte[] bytes, string name)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", name } };
    }

    private static SqliteWebApplicationFactory Factory(
        long? maxUpload = null, long? quota = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxUpload is long m) settings["Storage:MaxUploadBytes"] = m.ToString();
        if (quota is long q) settings["Storage:DefaultUserQuotaBytes"] = q.ToString();
        var f = new SqliteWebApplicationFactory(settings, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    // ---- upload size limit -------------------------------------------------

    [Fact]
    public async Task Upload_Below_Max_Size_Succeeds()
    {
        using var factory = Factory(maxUpload: 1024);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.PostAsync("/api/files", Multipart(new byte[512], "small.bin"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_Above_Max_Size_Rejected_With_413()
    {
        using var factory = Factory(maxUpload: 1024);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.PostAsync("/api/files", Multipart(new byte[4096], "big.bin"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task Oversized_Upload_Does_Not_Persist_A_FileItem()
    {
        using var factory = Factory(maxUpload: 1024);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();

        await client.PostAsync("/api/files", Multipart(new byte[4096], "big.bin"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == owner));
    }

    // ---- per-user quota ----------------------------------------------------

    [Fact]
    public async Task Quota_Unlimited_Preserves_Existing_Upload_Behavior()
    {
        // No quota configured (default 0 = unlimited).
        using var factory = Factory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.PostAsync("/api/files", Multipart(new byte[2048], "a.bin"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Quota_Rejects_Upload_That_Would_Exceed_Logical_Bytes()
    {
        using var factory = Factory(quota: 1000);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        // First 600-byte file fits (600 <= 1000).
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/api/files", Multipart(new byte[600], "a.bin"))).StatusCode);

        // Second 600-byte file would total 1200 > 1000 → 413.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync("/api/files", Multipart(new byte[600], "b.bin"))).StatusCode);
    }

    [Fact]
    public async Task Duplicate_Blob_Still_Counts_Against_Uploader_Quota()
    {
        using var factory = Factory(quota: 1000);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var bytes = new byte[600];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);

        // Same bytes uploaded twice → deduped to one physical blob, but the
        // uploader owns two logical files. The second push reaches 1200 > 1000.
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/api/files", Multipart(bytes, "first.bin"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync("/api/files", Multipart(bytes, "second.bin"))).StatusCode);
    }

    [Fact]
    public async Task Trash_Files_Still_Count_Toward_Quota()
    {
        using var factory = Factory(quota: 1000);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var first = await client.PostAsync("/api/files", Multipart(new byte[600], "a.bin"));
        var firstId = (await first.Content.ReadFromJsonAsync<FileSummary>())!.Id;

        // Move it to trash (soft delete). The logical bytes still count.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/files/{firstId}")).StatusCode);

        // A second 600-byte upload would still total 1200 > 1000 → rejected.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync("/api/files", Multipart(new byte[600], "b.bin"))).StatusCode);
    }

    [Fact]
    public async Task Permanent_Delete_Frees_Quota()
    {
        using var factory = Factory(quota: 1000);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var first = await client.PostAsync("/api/files", Multipart(new byte[600], "a.bin"));
        var firstId = (await first.Content.ReadFromJsonAsync<FileSummary>())!.Id;

        await client.DeleteAsync($"/api/files/{firstId}");
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/trash/files/{firstId}")).StatusCode);

        // Now the row is gone; a second 600-byte upload fits again.
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/api/files", Multipart(new byte[600], "b.bin"))).StatusCode);
    }

    // ---- GET /api/storage/me ----------------------------------------------

    [Fact]
    public async Task StorageMe_Without_Auth_Returns_401()
    {
        using var factory = Factory();
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/storage/me")).StatusCode);
    }

    [Fact]
    public async Task StorageMe_Reports_Used_Quota_Remaining_FileCount()
    {
        using var factory = Factory(quota: 1000);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await client.PostAsync("/api/files", Multipart(new byte[300], "a.bin"));

        var usage = await client.GetFromJsonAsync<UserStorageUsage>("/api/storage/me");
        Assert.NotNull(usage);
        Assert.Equal(300, usage!.UsedBytes);
        Assert.Equal(1, usage.FileCount);
        Assert.Equal(1000, usage.QuotaBytes);
        Assert.Equal(700, usage.RemainingBytes);
    }

    [Fact]
    public async Task StorageMe_Unlimited_Returns_Null_Quota()
    {
        using var factory = Factory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await client.PostAsync("/api/files", Multipart(new byte[300], "a.bin"));

        var usage = await client.GetFromJsonAsync<UserStorageUsage>("/api/storage/me");
        Assert.Equal(300, usage!.UsedBytes);
        Assert.Null(usage.QuotaBytes);
        Assert.Null(usage.RemainingBytes);
    }

    [Fact]
    public async Task StorageMe_Is_Owner_Scoped()
    {
        using var factory = Factory(quota: 100_000);

        var alice = await factory.SeedUserAsync("alice@example.com");
        var aliceClient = await factory.LoginAsync("alice@example.com");
        await aliceClient.PostAsync("/api/files", Multipart(new byte[5000], "a.bin"));

        var bob = await factory.SeedUserAsync("bob@example.com");
        var bobClient = await factory.LoginAsync("bob@example.com");

        // Bob has uploaded nothing — his usage is his own, not Alice's.
        var bobUsage = await bobClient.GetFromJsonAsync<UserStorageUsage>("/api/storage/me");
        Assert.Equal(0, bobUsage!.UsedBytes);
        Assert.Equal(0, bobUsage.FileCount);
    }

    // ---- admin aggregate quota stats --------------------------------------

    [Fact]
    public async Task Admin_Stats_Expose_Aggregate_Quota_Only()
    {
        using var factory = Factory(quota: 1000);
        var owner = await factory.SeedUserAsync("owner@example.com");
        await factory.PromoteToAdminAsync(owner);
        var client = await factory.LoginAsync("owner@example.com");

        // Push owner over quota by writing rows directly (bypass enforcement)
        // so we can assert UsersOverQuota counts them.
        await client.PostAsync("/api/files", Multipart(new byte[800], "a.bin"));

        var stats = await client.GetFromJsonAsync<StorageStatsResponse>("/api/admin/storage-stats");
        Assert.NotNull(stats);
        Assert.Equal(1000, stats!.Quota.DefaultQuotaBytes);
        Assert.Equal(800, stats.Quota.TotalLogicalBytes);
        Assert.Equal(0, stats.Quota.UsersOverQuota); // 800 <= 1000

        // The response must remain aggregate-only — no owner id appears.
        var raw = await (await client.GetAsync("/api/admin/storage-stats")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(owner.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageKey", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("objects/", raw, StringComparison.Ordinal);
    }
}

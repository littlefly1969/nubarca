using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 84 — Storage Stats short-lived cache + safe phase-timing diagnostics.
public sealed class StorageStatsCacheTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public StorageStatsCacheTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminClientAsync()
    {
        var id = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(id);
        return await _factory.LoginAsync("admin@example.com");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task StorageStats_ExposesSafePhaseTimings()
    {
        var client = await AdminClientAsync();
        var root = await ReadAsync(await client.GetAsync("/api/admin/storage-stats"));

        var d = root.GetProperty("diagnostics");
        Assert.True(d.GetProperty("totalMillis").GetInt64() >= 0);
        Assert.True(d.GetProperty("coreMillis").GetInt64() >= 0);
        Assert.True(d.GetProperty("physicalScanMillis").GetInt64() >= 0);
        Assert.True(d.GetProperty("derivativeScanMillis").GetInt64() >= 0);
        Assert.True(d.GetProperty("metadataAggregateMillis").GetInt64() >= 0);
        // First compute is not from cache.
        Assert.False(d.GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task StorageStats_SecondCallServedFromCache_RefreshForcesRecompute()
    {
        var client = await AdminClientAsync();

        var first = await ReadAsync(await client.GetAsync("/api/admin/storage-stats"));
        Assert.False(first.GetProperty("diagnostics").GetProperty("cached").GetBoolean());
        var usersFirst = first.GetProperty("users").GetProperty("total").GetInt32();

        // Second call within the TTL is served from the cache — identical data.
        var second = await ReadAsync(await client.GetAsync("/api/admin/storage-stats"));
        Assert.True(second.GetProperty("diagnostics").GetProperty("cached").GetBoolean());
        Assert.Equal(usersFirst, second.GetProperty("users").GetProperty("total").GetInt32());

        // refresh=true bypasses the cache and recomputes.
        var refreshed = await ReadAsync(await client.GetAsync("/api/admin/storage-stats?refresh=true"));
        Assert.False(refreshed.GetProperty("diagnostics").GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task PhysicalScan_IsOptIn_SkippedByDefault_RunOnDemand()
    {
        var client = await AdminClientAsync();

        // Fast dashboard load (physical=false): scan skipped, counts not computed.
        var skipped = await ReadAsync(await client.GetAsync("/api/admin/storage-stats?physical=false"));
        Assert.False(skipped.GetProperty("diagnostics").GetProperty("physicalScanIncluded").GetBoolean());
        Assert.Equal(0, skipped.GetProperty("diagnostics").GetProperty("physicalScanMillis").GetInt64());
        Assert.Equal(-1, skipped.GetProperty("blobs").GetProperty("physicalBlobCount").GetInt32());

        // On-demand integrity check (physical=true): scan runs, counts computed.
        var ran = await ReadAsync(await client.GetAsync("/api/admin/storage-stats?physical=true"));
        Assert.True(ran.GetProperty("diagnostics").GetProperty("physicalScanIncluded").GetBoolean());
        Assert.True(ran.GetProperty("blobs").GetProperty("physicalBlobCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task Diagnostics_ExposeNoSensitiveValues()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/admin/storage-stats");
        var body = await resp.Content.ReadAsStringAsync();
        // Diagnostics are numbers + a timestamp only — no SQL text or internals.
        foreach (var needle in new[] { "SELECT", "StorageKey", "Sha256", "objects/", "sql" })
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

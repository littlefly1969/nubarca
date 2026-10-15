using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// A Print Box updates its agent only when someone asks, only to the bundle
/// this server publishes, and only if it said it can update itself. Its root
/// update service fetches exactly that bundle with the station's own
/// credential; a failure is reported for the request it belongs to and is not
/// retried on its own.
/// </summary>
public sealed class PrintAgentSelfUpdateTests : IDisposable
{
    private readonly string _bundles = Path.Combine(Path.GetTempPath(), $"nubarca-agent-self-update-{Guid.NewGuid():N}");
    private readonly SqliteWebApplicationFactory _factory;

    public PrintAgentSelfUpdateTests()
    {
        Directory.CreateDirectory(_bundles);
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["PrintAgentBundle:Directory"] = _bundles,
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_bundles)) Directory.Delete(_bundles, recursive: true);
    }

    private static string Assembly(int major, int minor, int build) => new Version(major, minor, build, 0).ToString();

    private byte[] Publish(string version)
    {
        var bytes = RandomNumberGenerator.GetBytes(2048);
        var name = $"nubarca-print-agent-{version}-linux-x64.zip";
        File.WriteAllBytes(Path.Combine(_bundles, name), bytes);
        File.WriteAllText(Path.Combine(_bundles, "current-linux-x64.json"), JsonSerializer.Serialize(new
        {
            artifact = "print-agent", version, runtime = "linux-x64", fileName = name,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), sizeBytes = bytes.Length,
            sourceCommit = new string('e', 40),
        }));
        return bytes;
    }

    private async Task<(HttpClient Owner, Guid StationId, string Credential)> BoxAsync(string version, bool selfUpdate,
        string email = "owner@example.com")
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(email);
        var created = await (await owner.PostAsJsonAsync("/api/print/stations", new { name = "Box" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var stationId = created.GetProperty("id").GetGuid();
        var enrolled = await (await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = created.GetProperty("enrollmentToken").GetString(), agentVersion = version,
        })).Content.ReadFromJsonAsync<JsonElement>();
        var credential = enrolled.GetProperty("stationCredential").GetString()!;
        await HeartbeatAsync(credential, version, selfUpdate);
        return (owner, stationId, credential);
    }

    private async Task<JsonElement> HeartbeatAsync(string credential, string version, bool selfUpdate,
        string? updateResult = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new
            {
                agentVersion = version, selfUpdate, updateResult,
                devices = Array.Empty<object>(),
            }),
        };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> AsStation(string credential, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    private static bool Offered(JsonElement heartbeat) =>
        heartbeat.TryGetProperty("agentUpdate", out var offer) && offer.ValueKind == JsonValueKind.Object;

    private static async Task<JsonElement> AgentOf(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))![0].GetProperty("agent");

    [Fact]
    public async Task Nothing_Is_Offered_Until_Someone_Asks_And_Then_Exactly_The_Published_Bundle()
    {
        var bytes = Publish("0.9.0");
        var (owner, stationId, credential) = await BoxAsync(Assembly(0, 8, 0), selfUpdate: true);

        Assert.False(Offered(await HeartbeatAsync(credential, Assembly(0, 8, 0), true)));
        Assert.Equal(HttpStatusCode.NoContent, (await AsStation(credential, "/api/print-agent/update")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsStation(credential, "/api/print-agent/update/bundle")).StatusCode);

        var asked = await owner.PostAsync($"/api/print/stations/{stationId}/agent-update", null);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal("0.9.0", (await AgentOf(owner)).GetProperty("updateTarget").GetString());

        var heartbeat = await HeartbeatAsync(credential, Assembly(0, 8, 0), true);
        Assert.True(Offered(heartbeat));
        var offer = heartbeat.GetProperty("agentUpdate");
        Assert.Equal("0.9.0", offer.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), offer.GetProperty("sha256").GetString());

        var described = await (await AsStation(credential, "/api/print-agent/update")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(offer.GetProperty("requestId").GetString(), described.GetProperty("requestId").GetString());
        var download = await AsStation(credential, "/api/print-agent/update/bundle");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        // The box comes back on the new version: done, and nothing more is offered.
        Assert.False(Offered(await HeartbeatAsync(credential, Assembly(0, 9, 0), true)));
        var agent = await AgentOf(owner);
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("updateTarget").ValueKind);
        Assert.Equal("ok", agent.GetProperty("updateResult").GetString());
        Assert.Equal("0.9.0", agent.GetProperty("updateResultVersion").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PrintAgentUpdateRequest));
    }

    [Fact]
    public async Task A_Failure_Ends_Only_Its_Own_Request_And_Is_Not_Retried()
    {
        Publish("0.9.0");
        var (owner, stationId, credential) = await BoxAsync(Assembly(0, 8, 0), selfUpdate: true);
        (await owner.PostAsync($"/api/print/stations/{stationId}/agent-update", null)).EnsureSuccessStatusCode();
        var requestId = (await HeartbeatAsync(credential, Assembly(0, 8, 0), true))
            .GetProperty("agentUpdate").GetProperty("requestId").GetString();

        // An outcome of some earlier attempt does not end this one.
        Assert.True(Offered(await HeartbeatAsync(credential, Assembly(0, 8, 0), true, "failed 0.9.0 12345")));
        // This request's failure does, and nothing is offered again.
        Assert.False(Offered(await HeartbeatAsync(credential, Assembly(0, 8, 0), true, $"failed 0.9.0 {requestId}")));
        Assert.False(Offered(await HeartbeatAsync(credential, Assembly(0, 8, 0), true, $"failed 0.9.0 {requestId}")));
        var agent = await AgentOf(owner);
        Assert.Equal("failed", agent.GetProperty("updateResult").GetString());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("updateTarget").ValueKind);

        // Asked again: offered again, with the same stale failure line ignored.
        await Task.Delay(5);
        (await owner.PostAsync($"/api/print/stations/{stationId}/agent-update", null)).EnsureSuccessStatusCode();
        Assert.True(Offered(await HeartbeatAsync(credential, Assembly(0, 8, 0), true, $"failed 0.9.0 {requestId}")));
    }

    [Fact]
    public async Task A_Box_That_Cannot_Update_Itself_Or_Is_Current_Is_Not_Asked()
    {
        Publish("0.9.0");
        var (owner, stationId, _) = await BoxAsync(Assembly(0, 8, 0), selfUpdate: false);
        var refused = await owner.PostAsync($"/api/print/stations/{stationId}/agent-update", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("manual_update", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var (owner2, current, _) = await BoxAsync(Assembly(0, 9, 0), selfUpdate: true, "second@example.com");
        var upToDate = await owner2.PostAsync($"/api/print/stations/{current}/agent-update", null);
        Assert.Equal("up_to_date", (await upToDate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_The_Owner_Or_An_Administrator_Asks()
    {
        Publish("0.9.0");
        var (_, stationId, _) = await BoxAsync(Assembly(0, 8, 0), selfUpdate: true);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsync($"/api/print/stations/{stationId}/agent-update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.PostAsync("/api/admin/system-update/print-boxes", null)).StatusCode);

        var (adminId, admin) = await _factory.CreateAuthenticatedClientAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(adminId);
        var fleet = await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/system-update/print-boxes");
        var box = Assert.Single(fleet!);
        Assert.True(box.GetProperty("canUpdate").GetBoolean());
        var all = await (await admin.PostAsync("/api/admin/system-update/print-boxes", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, all.GetProperty("requested").GetInt32());
        Assert.False((await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/system-update/print-boxes"))![0]
            .GetProperty("canUpdate").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/admin/system-update/print-boxes/{stationId}")).StatusCode);
    }
}

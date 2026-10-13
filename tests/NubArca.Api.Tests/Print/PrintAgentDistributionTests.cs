using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The Print Agent bundle the printers page hands out, and how a station's
/// agent stands against this server: a bundle is offered only once its bytes
/// hash to its pointer, downloaded without an account only while an
/// administrator has it switched on, and an agent too old is refused —
/// never one merely older than the latest.
/// </summary>
public sealed class PrintAgentDistributionTests : IDisposable
{
    private const string Commit = "ed46ff75108e1698ca009c40b331b241ada47871";

    /// <summary>
    /// The four-part version an agent's assembly reports. Built, not written:
    /// four dotted numbers in source read as an address to the identity contract.
    /// </summary>
    private static string Assembly(int major, int minor, int build) => new Version(major, minor, build, 0).ToString();
    private readonly string _bundles = Path.Combine(Path.GetTempPath(), $"nubarca-agent-bundles-{Guid.NewGuid():N}");
    private readonly SqliteWebApplicationFactory _factory;

    public PrintAgentDistributionTests()
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

    /// <summary>Publishes a bundle as the deploy script does: the zip, then its pointer.</summary>
    private byte[] Publish(string version = "0.7.0", string? claimedSha = null, string commit = Commit)
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var name = $"nubarca-print-agent-{version}-linux-x64.zip";
        File.WriteAllBytes(Path.Combine(_bundles, name), bytes);
        File.WriteAllText(Path.Combine(_bundles, "current-linux-x64.json"), JsonSerializer.Serialize(new
        {
            artifact = "print-agent", version, runtime = "linux-x64", fileName = name,
            sha256 = claimedSha ?? Convert.ToHexStringLower(SHA256.HashData(bytes)),
            sizeBytes = bytes.Length, sourceCommit = commit,
        }));
        return bytes;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var (id, client) = await _factory.CreateAuthenticatedClientAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(id);
        return client;
    }

    [Fact]
    public async Task Nothing_Published_Is_Said_And_Nothing_Is_Downloadable()
    {
        var (_, member) = await _factory.CreateAuthenticatedClientAsync("member@example.com");
        var described = await member.GetFromJsonAsync<JsonElement>("/api/print/agent");
        Assert.False(described.GetProperty("available").GetBoolean());
        Assert.False(described.GetProperty("sharing").GetBoolean());
        Assert.False(described.GetProperty("canManage").GetBoolean());
        Assert.Equal("0.2.2", described.GetProperty("minimumVersion").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync("/api/print-agent/download/nubarca-print-agent-0.7.0-linux-x64.zip")).StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Switches_The_Download_On_And_The_Box_Gets_Exactly_The_Published_Bytes()
    {
        var bytes = Publish();
        var admin = await AdminAsync();
        var anonymous = _factory.CreateClient();
        const string url = "/api/print-agent/download/nubarca-print-agent-0.7.0-linux-x64.zip";

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/print/agent");
        Assert.True(before.GetProperty("available").GetBoolean());
        Assert.True(before.GetProperty("canManage").GetBoolean());
        Assert.Equal("0.7.0", before.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("downloadPath").ValueKind);
        // Published but not switched on: nothing to download.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);

        var on = await admin.PutAsJsonAsync("/api/print/agent/sharing", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(url, (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("downloadPath").GetString());

        var download = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        // Only the current bundle's own name.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync("/api/print-agent/download/nubarca-print-agent-0.6.0-linux-x64.zip")).StatusCode);

        (await admin.PutAsJsonAsync("/api/print/agent/sharing", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PrintAgentSharingSet));
    }

    [Fact]
    public async Task Only_An_Administrator_Switches_It()
    {
        Publish();
        var (_, member) = await _factory.CreateAuthenticatedClientAsync("member@example.com");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync("/api/print/agent/sharing", new { enabled = true })).StatusCode);
        Assert.False((await member.GetFromJsonAsync<JsonElement>("/api/print/agent")).GetProperty("sharing").GetBoolean());
    }

    [Fact]
    public async Task A_Bundle_Whose_Bytes_Do_Not_Hash_To_Its_Pointer_Is_No_Bundle()
    {
        Publish(claimedSha: new string('0', 64));
        var admin = await AdminAsync();
        (await admin.PutAsJsonAsync("/api/print/agent/sharing", new { enabled = true })).EnsureSuccessStatusCode();
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/print/agent")).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient()
            .GetAsync("/api/print-agent/download/nubarca-print-agent-0.7.0-linux-x64.zip")).StatusCode);
    }

    private async Task<(Guid Id, string Token)> StationAsync(HttpClient owner)
    {
        var created = await (await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return (created.GetProperty("id").GetGuid(), created.GetProperty("enrollmentToken").GetString()!);
    }

    private Task<HttpResponseMessage> EnrollAsync(Guid stationId, string token, string version, string? build = null) =>
        _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll",
            new { stationId, enrollmentToken = token, agentVersion = version, agentBuild = build });

    private Task<HttpResponseMessage> HeartbeatAsync(string credential, string version, string? build = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new
            {
                agentVersion = version, agentBuild = build,
                devices = new[]
                {
                    new
                    {
                        deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                        adapterKind = "cups", capabilities = new { formats = new[] { "10x15" }, color = true },
                        observedState = "ready",
                    },
                },
            }),
        };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> AgentOf(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))![0].GetProperty("agent");

    [Fact]
    public async Task An_Agent_Too_Old_Is_Refused_And_Its_Token_Still_Enrolls_The_Updated_One()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (stationId, token) = await StationAsync(owner);

        var old = await EnrollAsync(stationId, token, Assembly(0, 2, 1));
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        var refusal = await old.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_incompatible", refusal.GetProperty("error").GetString());
        Assert.Equal("0.2.2", refusal.GetProperty("minimumVersion").GetString());

        Assert.Equal(HttpStatusCode.OK, (await EnrollAsync(stationId, token, Assembly(0, 2, 2))).StatusCode);
    }

    [Fact]
    public async Task An_Older_Compatible_Agent_Works_And_Is_Only_Told_An_Update_Exists()
    {
        Publish("0.7.0");
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (stationId, token) = await StationAsync(owner);
        var credential = (await (await EnrollAsync(stationId, token, Assembly(0, 6, 0))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stationCredential").GetString()!;
        (await HeartbeatAsync(credential, Assembly(0, 6, 0))).EnsureSuccessStatusCode();

        var agent = await AgentOf(owner);
        Assert.Equal("0.6.0", agent.GetProperty("version").GetString());
        Assert.Equal("update-available", agent.GetProperty("compatibility").GetString());
        Assert.Equal("0.7.0", agent.GetProperty("latestVersion").GetString());
        Assert.False(agent.GetProperty("unofficial").GetBoolean());

        // The published build, as CI made it: current and official.
        (await HeartbeatAsync(credential, Assembly(0, 7, 0), Commit)).EnsureSuccessStatusCode();
        agent = await AgentOf(owner);
        Assert.Equal("current", agent.GetProperty("compatibility").GetString());
        Assert.False(agent.GetProperty("unofficial").GetBoolean());
        Assert.Equal(Commit[..12], agent.GetProperty("build").GetString());

        // The same version from another build, or none: not the published one.
        (await HeartbeatAsync(credential, Assembly(0, 7, 0), new string('a', 40))).EnsureSuccessStatusCode();
        Assert.True((await AgentOf(owner)).GetProperty("unofficial").GetBoolean());
        (await HeartbeatAsync(credential, Assembly(0, 7, 0))).EnsureSuccessStatusCode();
        Assert.True((await AgentOf(owner)).GetProperty("unofficial").GetBoolean());
    }

    [Fact]
    public async Task An_Agent_That_Became_Incompatible_Is_Given_No_Work()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (stationId, token) = await StationAsync(owner);
        var credential = (await (await EnrollAsync(stationId, token, Assembly(0, 7, 0))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stationCredential").GetString()!;
        (await HeartbeatAsync(credential, Assembly(0, 7, 0))).EnsureSuccessStatusCode();
        var printerId = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))![0]
            .GetProperty("devices")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(
            $"/api/print/stations/{stationId}/test-jobs", new { printerDeviceId = printerId })).StatusCode);

        // A box downgraded below what the server serves: its heartbeat still
        // tells the owner so, but no job goes to it.
        (await HeartbeatAsync(credential, Assembly(0, 2, 1))).EnsureSuccessStatusCode();
        Assert.Equal("incompatible", (await AgentOf(owner)).GetProperty("compatibility").GetString());
        var claim = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/jobs/claim")
        {
            Content = JsonContent.Create(new { adapterKind = "cups" }),
        };
        claim.Headers.Add("X-NubArca-Print-Credential", credential);
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.CreateClient().SendAsync(claim)).StatusCode);
    }

    [Fact]
    public void An_Assembly_Version_Reads_As_Its_Three_Parts() =>
        Assert.Equal("0.6.0", PrintAgentCompatibility.Display(PrintAgentCompatibility.Parse(Assembly(0, 6, 0))!));

    [Theory]
    [InlineData("0.6.0", "0.6.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData("v1.0", null)]
    public void Versions_Are_Read_As_The_Agent_Reports_Them(string reported, string? expected) =>
        Assert.Equal(expected, PrintAgentCompatibility.Parse(reported) is { } v ? PrintAgentCompatibility.Display(v) : null);
}

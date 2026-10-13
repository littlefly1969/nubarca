using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// A printer switched to its simulated twin: only by its owner and only while
/// the agent offers a twin; every job remembers whether it was simulated when
/// it was made; a simulated job never reaches an agent that cannot simulate;
/// and the owner sees, downloads and clears the simulated sheets.
/// </summary>
public sealed class PrinterSimulationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrinterSimulationTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private async Task<Printer> PrinterAsync(HttpClient owner, object? simulation)
    {
        var created = await (await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId = id, enrollmentToken = created.GetProperty("enrollmentToken").GetString(), agentVersion = "0.8.0",
        });
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        (await HeartbeatAsync(credential, simulation)).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new Printer(id, await db.PrinterDevices.Where(d => d.PrintStationId == id).Select(d => d.Id).SingleAsync(),
            credential);
    }

    private async Task<HttpResponseMessage> HeartbeatAsync(string credential, object? simulation)
    {
        var device = new Dictionary<string, object?>
        {
            ["deviceKey"] = "NubArca-RX1HS", ["displayName"] = "DNP DS-RX1HS", ["manufacturer"] = "DNP",
            ["model"] = "DS-RX1HS", ["adapterKind"] = "cups",
            ["capabilities"] = new { formats = new[] { "10x15", "2x6x2" }, color = true }, ["observedState"] = "ready",
        };
        if (simulation is not null) device["simulation"] = simulation;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new { agentVersion = "0.8.0", devices = new[] { device } }),
        };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return await _factory.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> ClaimAsync(string credential, bool supportsSimulation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/jobs/claim")
        {
            Content = JsonContent.Create(new { adapterKind = "cups", supportsSimulation }),
        };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<Guid> TestJobAsync(HttpClient owner, Printer printer)
    {
        var job = await owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/test-jobs",
            new { printerDeviceId = printer.DeviceId });
        Assert.Equal(HttpStatusCode.Accepted, job.StatusCode);
        return (await job.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SwitchAsync(HttpClient owner, Printer printer, bool enabled) =>
        owner.PutAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/simulation",
            new { enabled });

    private async Task CompleteAsync(string credential)
    {
        var claim = await (await ClaimAsync(credential, supportsSimulation: true)).Content.ReadFromJsonAsync<JsonElement>();
        var jobId = claim.GetProperty("jobId").GetGuid();
        var token = claim.GetProperty("claimToken").GetString();
        foreach (var (path, body) in new (string, object)[]
                 {
                     ($"/api/print-agent/jobs/{jobId}/submitting", new { claimToken = token }),
                     ($"/api/print-agent/jobs/{jobId}/result", new { claimToken = token, outcome = "completed", spoolReference = "sim" }),
                 })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-NubArca-Print-Credential", credential);
            (await _factory.CreateClient().SendAsync(request)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Only_The_Owner_Switches_And_Only_While_The_Agent_Offers_A_Twin()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var noTwin = await PrinterAsync(owner, simulation: null);
        Assert.Equal(HttpStatusCode.Conflict, (await SwitchAsync(owner, noTwin, true)).StatusCode);

        var withTwin = await PrinterAsync(owner, new { available = true, only = false });
        var on = await SwitchAsync(owner, withTwin, true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("simulated").GetBoolean());

        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await SwitchAsync(stranger, withTwin, false)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PrinterSimulationSet));
    }

    [Fact]
    public async Task A_Job_Keeps_Whether_It_Was_Simulated_And_Never_Reaches_An_Agent_That_Cannot_Simulate()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var printer = await PrinterAsync(owner, new { available = true, only = false });
        (await SwitchAsync(owner, printer, true)).EnsureSuccessStatusCode();
        var heartbeat = await (await HeartbeatAsync(printer.Credential, new { available = true, only = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["NubArca-RX1HS"], heartbeat.GetProperty("simulatedDeviceKeys").EnumerateArray().Select(k => k.GetString()));

        var simulated = await TestJobAsync(owner, printer);
        // The owner switches back: the queued job stays what it was made as.
        (await SwitchAsync(owner, printer, false)).EnsureSuccessStatusCode();
        var real = await TestJobAsync(owner, printer);

        // An agent that does not say it can simulate is given only the real job.
        var claim = await (await ClaimAsync(printer.Credential, supportsSimulation: false)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(real, claim.GetProperty("jobId").GetGuid());
        Assert.False(claim.GetProperty("simulated").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimAsync(printer.Credential, supportsSimulation: false)).StatusCode);

        var twinClaim = await (await ClaimAsync(printer.Credential, supportsSimulation: true)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(simulated, twinClaim.GetProperty("jobId").GetGuid());
        Assert.True(twinClaim.GetProperty("simulated").GetBoolean());
    }

    [Fact]
    public async Task A_Box_With_Only_A_Twin_Is_Always_Simulated()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var printer = await PrinterAsync(owner, new { available = true, only = true });
        var device = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))![0].GetProperty("devices")[0];
        Assert.True(device.GetProperty("simulated").GetBoolean());
        Assert.True(device.GetProperty("simulationOnly").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await SwitchAsync(owner, printer, false)).StatusCode);
        await TestJobAsync(owner, printer);
        using var scope = _factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrintJobs.SingleAsync()).Simulated);
    }

    [Fact]
    public async Task The_Owner_Sees_Downloads_And_Clears_The_Simulated_Sheets_And_The_Box_Is_Told()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var printer = await PrinterAsync(owner, new { available = true, only = false });
        var real = await TestJobAsync(owner, printer);
        (await SwitchAsync(owner, printer, true)).EnsureSuccessStatusCode();
        var simulated = await TestJobAsync(owner, printer);
        await CompleteAsync(printer.Credential);
        await CompleteAsync(printer.Credential);
        var list = $"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/simulated-jobs";

        var listed = (await owner.GetFromJsonAsync<JsonElement[]>(list))!;
        Assert.Equal(simulated, Assert.Single(listed).GetProperty("id").GetGuid());
        var sheet = await owner.GetAsync($"/api/print/jobs/{simulated}/simulated-sheet");
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        Assert.Equal("image/png", sheet.Content.Headers.ContentType?.MediaType);
        // A real print's sheet is never served here, and nobody else's.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/print/jobs/{real}/simulated-sheet")).StatusCode);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/print/jobs/{simulated}/simulated-sheet")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(list)).StatusCode);

        string key;
        using (var scope = _factory.Services.CreateScope())
            key = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrintJobs.SingleAsync(j => j.Id == simulated))
                .ArtifactStorageKey!;
        var cleared = await (await owner.DeleteAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, cleared.GetProperty("cleared").GetInt32());
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement[]>(list))!);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.PrintJobs.AnyAsync(j => j.Id == real), "a real print is never cleared");
            Assert.False(await scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>().ExistsAsync(key));
        }
        // The box hears it at its next heartbeat.
        var heartbeat = await (await HeartbeatAsync(printer.Credential, new { available = true, only = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, heartbeat.GetProperty("simulatedOutputPurgeBefore").ValueKind);
    }
}

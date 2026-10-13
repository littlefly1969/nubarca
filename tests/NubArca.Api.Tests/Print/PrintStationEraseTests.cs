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
/// "Erase everything" for a revoked print station: only after revocation, only
/// by its owner, and then nothing of it is left — jobs, rendered sheets, loans,
/// printers, enrollment codes, the station — but one audit line saying who
/// erased which station id.
/// </summary>
public sealed class PrintStationEraseTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrintStationEraseTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private sealed record Station(Guid Id, Guid DeviceId, string Credential);

    private async Task<Station> StationWithAJobAndALoanAsync(HttpClient owner)
    {
        var created = await (await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId = id, enrollmentToken = created.GetProperty("enrollmentToken").GetString(), agentVersion = "0.7.0",
        });
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new
            {
                agentVersion = "0.7.0",
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
        heartbeat.Headers.Add("X-NubArca-Print-Credential", credential);
        (await _factory.CreateClient().SendAsync(heartbeat)).EnsureSuccessStatusCode();
        var deviceId = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!
            .Single(s => s.GetProperty("id").GetGuid() == id).GetProperty("devices")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(
            $"/api/print/stations/{id}/test-jobs", new { printerDeviceId = deviceId })).StatusCode);
        var grantee = $"mario-{Guid.NewGuid():N}@example.com";
        await _factory.CreateAuthenticatedClientAsync(grantee);
        (await owner.PostAsJsonAsync($"/api/print/stations/{id}/devices/{deviceId}/shares",
            new { email = grantee, maxSheets = 10 })).EnsureSuccessStatusCode();
        return new Station(id, deviceId, credential);
    }

    [Fact]
    public async Task A_Station_Not_Revoked_Is_Not_Erased_And_Another_Owners_Is_Not_Found()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await StationWithAJobAndALoanAsync(owner);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);

        (await owner.DeleteAsync($"/api/print/stations/{station.Id}")).EnsureSuccessStatusCode();
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
    }

    [Fact]
    public async Task A_Revoked_Station_Is_Erased_With_Everything_It_Left_But_One_Audit_Line()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await StationWithAJobAndALoanAsync(owner);
        string artifactKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            artifactKey = (await db.PrintJobs.SingleAsync(j => j.PrintStationId == station.Id)).ArtifactStorageKey!;
            Assert.True(await scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>().ExistsAsync(artifactKey));
        }
        (await owner.DeleteAsync($"/api/print/stations/{station.Id}")).EnsureSuccessStatusCode();

        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/print/stations/{station.Id}/erase");
        Assert.Equal(1, preview.GetProperty("jobs").GetInt32());
        Assert.Equal(0, preview.GetProperty("parties").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.PrintStations.AnyAsync(s => s.Id == station.Id));
            Assert.False(await db.PrinterDevices.AnyAsync(d => d.PrintStationId == station.Id));
            Assert.False(await db.PrinterShares.AnyAsync(s => s.PrinterDeviceId == station.DeviceId));
            Assert.False(await db.PrintStationEnrollments.AnyAsync(e => e.PrintStationId == station.Id));
            Assert.False(await db.PrintJobs.AnyAsync(j => j.PrintStationId == station.Id));
            Assert.False(await scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>().ExistsAsync(artifactKey));

            var erased = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PrintStationErase);
            Assert.Equal(station.Id, erased.EntityId);
            Assert.Equal(AuditEntityTypes.PrintStation, erased.EntityType);
            Assert.True(string.IsNullOrEmpty(erased.MetadataJson) || erased.MetadataJson == "null");
        }
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
    }

    [Fact]
    public async Task Another_Station_Of_The_Same_Owner_Is_Untouched()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var erased = await StationWithAJobAndALoanAsync(owner);
        var kept = await StationWithAJobAndALoanAsync(owner);
        (await owner.DeleteAsync($"/api/print/stations/{erased.Id}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/print/stations/{erased.Id}/erase")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.PrintStations.AnyAsync(s => s.Id == kept.Id));
        Assert.Equal(1, await db.PrintJobs.CountAsync(j => j.PrintStationId == kept.Id));
        Assert.True(await db.PrinterShares.AnyAsync(s => s.PrinterDeviceId == kept.DeviceId));
        var key = (await db.PrintJobs.SingleAsync(j => j.PrintStationId == kept.Id)).ArtifactStorageKey!;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>().ExistsAsync(key));
    }
}

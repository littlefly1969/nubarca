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
/// printers, enrollment codes, the station. The erase and its
/// <c>print.station.erase</c> audit record are one transaction: if the record
/// cannot be written, nothing is erased. Earlier audit records are kept
/// (append-only); the erase adds exactly one.
/// </summary>
public sealed class PrintStationEraseTests : IDisposable
{
    private readonly AuditSwitch _audit = new();
    private readonly SqliteWebApplicationFactory _factory;

    public PrintStationEraseTests()
    {
        // Its own host, never a pooled one: the audit logger below is this test's.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>());
        _factory.ConfigureExtraServices = services => services.AddScoped<IAuditLogger>(provider =>
            new SwitchableAudit(ActivatorUtilities.CreateInstance<AuditLogger>(provider), _audit));
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>The audit store as a test decides: working, or failing the erase record.</summary>
    private sealed class AuditSwitch
    {
        public bool FailEraseRecord { get; set; }
    }

    private sealed class SwitchableAudit(IAuditLogger inner, AuditSwitch state) : IAuditLogger
    {
        public Task LogAsync(AuditActor actor, string action, string entityType, Guid? entityId, string? ipAddress,
            object? metadata, CancellationToken cancellationToken = default) =>
            inner.LogAsync(actor, action, entityType, entityId, ipAddress, metadata, cancellationToken);

        public Task WriteAsync(AuditActor actor, string action, string entityType, Guid? entityId, string? ipAddress,
            object? metadata, CancellationToken cancellationToken = default) =>
            state.FailEraseRecord && action == AuditActions.PrintStationErase
                ? throw new InvalidOperationException("the audit store is unavailable")
                : inner.WriteAsync(actor, action, entityType, entityId, ipAddress, metadata, cancellationToken);
    }

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

    /// <summary>Everything the station left, as rows: what an erase must take, or a rollback keep.</summary>
    private async Task<(bool Station, int Devices, int Shares, int Enrollments, int Jobs, int EraseRecords)> LeftAsync(
        Station station)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PrintStations.AnyAsync(s => s.Id == station.Id),
            await db.PrinterDevices.CountAsync(d => d.PrintStationId == station.Id),
            await db.PrinterShares.CountAsync(s => s.PrinterDeviceId == station.DeviceId),
            await db.PrintStationEnrollments.CountAsync(e => e.PrintStationId == station.Id),
            await db.PrintJobs.CountAsync(j => j.PrintStationId == station.Id),
            await db.AuditLogs.CountAsync(a => a.Action == AuditActions.PrintStationErase && a.EntityId == station.Id));
    }

    private async Task<string> ArtifactKeyAsync(Station station)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrintJobs
            .SingleAsync(j => j.PrintStationId == station.Id)).ArtifactStorageKey!;
    }

    private async Task<bool> ArtifactExistsAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>().ExistsAsync(key);
    }

    [Fact]
    public async Task A_Station_Not_Revoked_Or_Not_Ones_Own_Is_Neither_Erased_Nor_Touched()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await StationWithAJobAndALoanAsync(owner);
        var before = await LeftAsync(station);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(before, await LeftAsync(station));

        (await owner.DeleteAsync($"/api/print/stations/{station.Id}")).EnsureSuccessStatusCode();
        var revoked = await LeftAsync(station);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(revoked, await LeftAsync(station));
        Assert.Equal(0, revoked.EraseRecords);
    }

    [Fact]
    public async Task A_Revoked_Station_Is_Erased_With_Everything_It_Left_And_One_Erase_Record_Added()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await StationWithAJobAndALoanAsync(owner);
        var artifactKey = await ArtifactKeyAsync(station);
        Assert.True(await ArtifactExistsAsync(artifactKey));
        (await owner.DeleteAsync($"/api/print/stations/{station.Id}")).EnsureSuccessStatusCode();
        int earlierRecords;
        using (var scope = _factory.Services.CreateScope())
        {
            // The loan's creation and its end at revocation: records about this printer, written earlier.
            earlierRecords = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
                .CountAsync(a => a.EntityId == station.DeviceId);
            Assert.True(earlierRecords >= 2);
        }

        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/print/stations/{station.Id}/erase");
        Assert.Equal(1, preview.GetProperty("jobs").GetInt32());
        Assert.Equal(0, preview.GetProperty("parties").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);

        Assert.Equal((false, 0, 0, 0, 0, 1), await LeftAsync(station));
        Assert.False(await ArtifactExistsAsync(artifactKey));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var erased = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PrintStationErase);
            Assert.Equal(station.Id, erased.EntityId);
            Assert.Equal(AuditEntityTypes.PrintStation, erased.EntityType);
            Assert.Null(erased.MetadataJson);
            // Append-only: what the trail said before the erase, it still says.
            Assert.Equal(earlierRecords, await db.AuditLogs.CountAsync(a => a.EntityId == station.DeviceId));
        }
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!);
    }

    [Fact]
    public async Task When_The_Erase_Record_Cannot_Be_Written_Nothing_Is_Erased_And_A_Retry_Erases_Once()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var station = await StationWithAJobAndALoanAsync(owner);
        var artifactKey = await ArtifactKeyAsync(station);
        (await owner.DeleteAsync($"/api/print/stations/{station.Id}")).EnsureSuccessStatusCode();
        var revoked = await LeftAsync(station);

        _audit.FailEraseRecord = true;
        var failed = await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        // Rolled back whole: the station and everything it left are still there, with no record.
        Assert.Equal(revoked, await LeftAsync(station));
        Assert.Equal(0, revoked.EraseRecords);
        Assert.True(await ArtifactExistsAsync(artifactKey));

        // The retry finds the station as it was, and erases it — once.
        _audit.FailEraseRecord = false;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal((false, 0, 0, 0, 0, 1), await LeftAsync(station));
        Assert.False(await ArtifactExistsAsync(artifactKey));
        // And once more: nothing left to erase, nothing recorded twice.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/print/stations/{station.Id}/erase")).StatusCode);
        Assert.Equal(1, (await LeftAsync(station)).EraseRecords);
    }

    [Fact]
    public async Task Another_Station_Of_The_Same_Owner_Is_Untouched()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var erased = await StationWithAJobAndALoanAsync(owner);
        var kept = await StationWithAJobAndALoanAsync(owner);
        var keptBefore = await LeftAsync(kept);
        (await owner.DeleteAsync($"/api/print/stations/{erased.Id}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/print/stations/{erased.Id}/erase")).EnsureSuccessStatusCode();

        Assert.Equal(keptBefore, await LeftAsync(kept));
        Assert.True(await ArtifactExistsAsync(await ArtifactKeyAsync(kept)));
    }
}

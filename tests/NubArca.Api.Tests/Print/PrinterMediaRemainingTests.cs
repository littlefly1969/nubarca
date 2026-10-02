using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The prints physically left on a printer's media, as its agent reports them:
/// stored as the printer said, dated on the server's clock, shown to the owner
/// and to whoever it is lent to — and never a budget, a ceiling or a lock.
/// </summary>
public sealed class PrinterMediaRemainingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrinterMediaRemainingTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private async Task<Printer> PrinterAsync(HttpClient owner)
    {
        var created = await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" });
        var station = await created.Content.ReadFromJsonAsync<JsonElement>();
        var stationId = station.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = station.GetProperty("enrollmentToken").GetString(), agentVersion = "0.5.0",
        });
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        (await Heartbeat(credential, new { available = true, remainingPrints = 187, ageSeconds = 3 })).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new Printer(stationId,
            await db.PrinterDevices.Where(d => d.PrintStationId == stationId).Select(d => d.Id).SingleAsync(), credential);
    }

    /// <summary>A heartbeat; <paramref name="media"/> null is an agent that predates the count.</summary>
    private Task<HttpResponseMessage> Heartbeat(string credential, object? media)
    {
        var device = new Dictionary<string, object?>
        {
            ["deviceKey"] = "dnp", ["displayName"] = "DNP DS-RX1HS", ["manufacturer"] = "DNP", ["model"] = "DS-RX1HS",
            ["adapterKind"] = "cups", ["capabilities"] = new { formats = new[] { "10x15" }, color = true },
            ["observedState"] = "ready",
        };
        if (media is not null) device["mediaRemaining"] = media;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new { agentVersion = "0.5.0", devices = new[] { device } }),
        };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<(int? Count, DateTime? At)> StoredAsync(Printer printer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.PrinterDevices.AsNoTracking().SingleAsync(d => d.Id == printer.DeviceId);
        return (device.MediaRemainingPrints, device.MediaRemainingObservedAt);
    }

    private static async Task<JsonElement> OwnerDevice(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/print/stations"))[0].GetProperty("devices")[0];

    [Fact]
    public async Task The_Printers_Count_Is_Stored_And_Dated_On_The_Servers_Clock()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var before = DateTime.UtcNow;
        var printer = await PrinterAsync(owner);
        var (count, at) = await StoredAsync(printer);
        Assert.Equal(187, count);
        // Three seconds old when the agent read it: dated three seconds back.
        Assert.InRange(at!.Value, before.AddSeconds(-4), DateTime.UtcNow.AddSeconds(-2));

        var device = await OwnerDevice(owner);
        Assert.Equal(187, device.GetProperty("mediaRemainingPrints").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, device.GetProperty("mediaRemainingObservedAt").ValueKind);
    }

    [Fact]
    public async Task Zero_Is_Spent_Media_And_Unavailable_Is_No_Count()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);

        (await Heartbeat(printer.Credential, new { available = true, remainingPrints = 0, ageSeconds = 0 })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await StoredAsync(printer)).Count);

        (await Heartbeat(printer.Credential, new { available = false, remainingPrints = (int?)null })).EnsureSuccessStatusCode();
        Assert.Equal((null, null), await StoredAsync(printer));
        Assert.Equal(JsonValueKind.Null, (await OwnerDevice(owner)).GetProperty("mediaRemainingPrints").ValueKind);
    }

    [Fact]
    public async Task An_Older_Agent_Neither_Clears_Nor_Freshens_The_Last_Reading()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var reading = await StoredAsync(printer);

        (await Heartbeat(printer.Credential, media: null)).EnsureSuccessStatusCode();
        Assert.Equal(reading, await StoredAsync(printer));
    }

    [Fact]
    public async Task An_Age_Or_A_Count_Out_Of_Bounds_Never_Fails_The_Heartbeat()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);

        var ancient = await Heartbeat(printer.Credential, new { available = true, remainingPrints = 50, ageSeconds = int.MaxValue });
        Assert.Equal(HttpStatusCode.OK, ancient.StatusCode);
        var (_, at) = await StoredAsync(printer);
        Assert.True(at > DateTime.UtcNow.AddDays(-31), "a reading is dated no earlier than the bound");

        var future = await Heartbeat(printer.Credential, new { available = true, remainingPrints = 50, ageSeconds = -500 });
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        Assert.True((await StoredAsync(printer)).At <= DateTime.UtcNow, "never dated in the future");

        foreach (var absurd in new[] { -1, 100_001 })
        {
            var response = await Heartbeat(printer.Credential, new { available = true, remainingPrints = absurd, ageSeconds = 0 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null((await StoredAsync(printer)).Count);
        }
    }

    [Fact]
    public async Task The_Borrower_Sees_The_Media_And_The_Loan_Separately_And_A_Stranger_Sees_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var (_, mario) = await _factory.CreateAuthenticatedClientAsync("mario@example.com");
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("anna@example.com");
        var printer = await PrinterAsync(owner);
        (await owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 12 })).EnsureSuccessStatusCode();

        var lent = (await mario.GetFromJsonAsync<JsonElement>("/api/print/shared-printers"))[0];
        Assert.Equal(187, lent.GetProperty("mediaRemainingPrints").GetInt32());
        Assert.Equal(12, lent.GetProperty("maxSheets").GetInt32());
        Assert.Equal(0, lent.GetProperty("usedSheets").GetInt32());

        Assert.Equal(0, (await stranger.GetFromJsonAsync<JsonElement>("/api/print/stations")).GetArrayLength());
        Assert.Equal(0, (await stranger.GetFromJsonAsync<JsonElement>("/api/print/shared-printers")).GetArrayLength());

        // A new count changes neither the loan nor anything else.
        (await Heartbeat(printer.Credential, new { available = true, remainingPrints = 3, ageSeconds = 0 })).EnsureSuccessStatusCode();
        lent = (await mario.GetFromJsonAsync<JsonElement>("/api/print/shared-printers"))[0];
        Assert.Equal(3, lent.GetProperty("mediaRemainingPrints").GetInt32());
        Assert.Equal(12, lent.GetProperty("maxSheets").GetInt32());
        Assert.Equal(0, lent.GetProperty("usedSheets").GetInt32());
    }

    [Fact]
    public async Task A_Spent_Count_Is_Not_A_Lock_The_Printer_Still_Takes_Its_Job()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        (await Heartbeat(printer.Credential, new { available = true, remainingPrints = 0, ageSeconds = 0 })).EnsureSuccessStatusCode();

        (await owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/test-jobs",
            new { printerDeviceId = printer.DeviceId })).EnsureSuccessStatusCode();
        var claim = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/jobs/claim")
        {
            Content = JsonContent.Create(new { adapterKind = "cups" }),
        };
        claim.Headers.Add("X-NubArca-Print-Credential", printer.Credential);
        // The printer, CUPS and the driver decide what happens to the paper.
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().SendAsync(claim)).StatusCode);
    }
}

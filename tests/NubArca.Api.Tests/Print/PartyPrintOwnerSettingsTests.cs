using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The HOST's print settings, frozen against the real endpoints.
///
/// Two things are load-bearing here and both are easy to get quietly wrong.
/// The two budgets are INDEPENDENT and their counters are HISTORY, so nothing
/// may sum them, reset them, or let a budget fall under what has already been
/// printed. And enabling printing is a promise the guest hub then makes on the
/// host's behalf, so the printer is validated when it is chosen rather than
/// discovered to be wrong by the first guest who tries.
/// </summary>
public sealed class PartyPrintOwnerSettingsTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyPrintOwnerSettingsTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private int _hosts;

    private sealed record Host(HttpClient Client, Guid OwnerId, Guid AlbumId);

    private async Task<Host> SeedHostAsync()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync(
            $"host{Interlocked.Increment(ref _hosts)}@example.com");
        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Festa" });
        created.EnsureSuccessStatusCode();
        var albumId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = await db.Albums.Where(a => a.Id == albumId)
            .Select(a => a.OwnerUserId).SingleAsync();
        return new Host(client, ownerId, albumId);
    }

    /// <summary>A station with one printer, capable of 10x15 unless told otherwise.</summary>
    private async Task<(Guid StationId, Guid DeviceId)> SeedPrinterAsync(
        Guid ownerUserId, string formats = "{\"formats\":[\"10x15\"]}",
        bool revoked = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId, OwnerUserId = ownerUserId, Name = "Postazione",
            Enabled = true, CreatedAt = DateTime.UtcNow,
            RevokedAt = revoked ? DateTime.UtcNow : null,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId, PrintStationId = stationId, DeviceKey = "d1",
            DisplayName = "DS620", AdapterKind = "fake", CapabilitiesJson = formats,
            LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (stationId, deviceId);
    }

    private static Task<HttpResponseMessage> SaveAsync(Host host, object body) =>
        host.Client.PatchAsJsonAsync($"/api/albums/{host.AlbumId}/party-print-settings", body);

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    [Fact]
    public async Task An_Unconfigured_Party_Reads_As_Printing_Off_Rather_Than_Missing()
    {
        var host = await SeedHostAsync();
        var response = await host.Client.GetAsync(
            $"/api/albums/{host.AlbumId}/party-print-settings");
        response.EnsureSuccessStatusCode();

        // No profile row yet is a party that has never been set up for printing,
        // which is a perfectly good answer with a shape the panel can render.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("photo").GetProperty("maxPrints").GetInt32());
        Assert.Equal(
            PartyPrintLimits.FooterMaxLength, body.GetProperty("footerMaxLength").GetInt32());
    }

    [Fact]
    public async Task Turning_Printing_On_Stores_Each_Product_Budget_Separately()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);

        var response = await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 40,
            stripEnabled = true, stripMaxPrints = 10,
            footerText = "Grazie di essere qui",
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 40 photos and 10 strips are two budgets, never one pool of 50.
        Assert.Equal(40, body.GetProperty("photo").GetProperty("maxPrints").GetInt32());
        Assert.Equal(40, body.GetProperty("photo").GetProperty("remaining").GetInt32());
        Assert.Equal(10, body.GetProperty("strip").GetProperty("maxPrints").GetInt32());
        Assert.Equal(10, body.GetProperty("strip").GetProperty("remaining").GetInt32());
        Assert.Equal("Grazie di essere qui", body.GetProperty("footerText").GetString());
    }

    [Fact]
    public async Task Saving_One_Setting_Leaves_The_Rest_Alone()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 40,
            stripEnabled = true, stripMaxPrints = 10, footerText = "Ciao",
        })).EnsureSuccessStatusCode();

        // A panel that saves one switch must not silently reset the others.
        var response = await SaveAsync(host, new { stripEnabled = false });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("strip").GetProperty("enabled").GetBoolean());
        Assert.Equal(10, body.GetProperty("strip").GetProperty("maxPrints").GetInt32());
        Assert.True(body.GetProperty("photo").GetProperty("enabled").GetBoolean());
        Assert.Equal(40, body.GetProperty("photo").GetProperty("maxPrints").GetInt32());
        Assert.Equal("Ciao", body.GetProperty("footerText").GetString());
    }

    [Fact]
    public async Task A_Budget_Cannot_Be_Lowered_Under_What_Has_Already_Been_Printed()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 40,
            stripEnabled = true, stripMaxPrints = 10,
        })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PartyPrintProfiles
                .SingleAsync(p => p.PartyAlbumId == host.AlbumId);
            profile.PhotoAcceptedCount = 12;
            await db.SaveChangesAsync();
        }

        // Twelve sheets have physically come out. A budget of 5 would mean a
        // remainder of minus seven, which is not a number to show anyone.
        var refused = await SaveAsync(host, new { photoMaxPrints = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("photo_budget_below_used", await ErrorOf(refused));

        // Raising it is fine, and the spent count is untouched by the change.
        var raised = await SaveAsync(host, new { photoMaxPrints = 60 });
        raised.EnsureSuccessStatusCode();
        var body = await raised.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, body.GetProperty("photo").GetProperty("used").GetInt32());
        Assert.Equal(48, body.GetProperty("photo").GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Turning_A_Product_Off_And_On_Resumes_From_The_Same_History()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 20,
        })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PartyPrintProfiles
                .SingleAsync(p => p.PartyAlbumId == host.AlbumId);
            profile.PhotoAcceptedCount = 8;
            await db.SaveChangesAsync();
        }

        // Turning the last product off leaves printing switched on with nothing
        // to offer, which the hub already renders as no card at all. Refusing
        // the edit would only strand the host.
        (await SaveAsync(host, new { photoEnabled = false })).EnsureSuccessStatusCode();
        var back = await SaveAsync(host, new { photoEnabled = true });
        back.EnsureSuccessStatusCode();

        // The paper already spent did not come back, so neither does the budget.
        var body = await back.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8, body.GetProperty("photo").GetProperty("used").GetInt32());
        Assert.Equal(12, body.GetProperty("photo").GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Turning_Off_Every_Product_Withdraws_The_Guest_Card()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        var party = await host.Client.PatchAsJsonAsync(
            $"/api/albums/{host.AlbumId}/party-settings", new { enabled = true });
        party.EnsureSuccessStatusCode();
        var viewToken = (await party.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10,
        })).EnsureSuccessStatusCode();

        // The master switch is still on, but there is nothing to print, so the
        // capability is not published. No card, rather than a card that leads
        // to an empty studio.
        (await SaveAsync(host, new { photoEnabled = false })).EnsureSuccessStatusCode();
        var anon = _factory.CreateClient();
        var album = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        Assert.Equal(JsonValueKind.Null, album.GetProperty("printUrl").ValueKind);
    }

    [Fact]
    public async Task A_Budget_Outside_The_Stated_Range_Is_Refused_Not_Clamped()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);

        foreach (var value in new[] { 0, PartyPrintLimits.MaxBudget + 1 })
        {
            var refused = await SaveAsync(host, new
            {
                enabled = true, printStationId = stationId, printerDeviceId = deviceId,
                photoEnabled = true, photoMaxPrints = value,
            });
            // Silently clamping would leave a host believing they set something
            // they did not.
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("photo_budget_range", await ErrorOf(refused));
        }
    }

    [Fact]
    public async Task Printing_Cannot_Be_Enabled_Without_A_Printer_Or_A_Product()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);

        var noPrinter = await SaveAsync(host, new
        {
            enabled = true, photoEnabled = true, photoMaxPrints = 10,
        });
        Assert.Equal("printer_required", await ErrorOf(noPrinter));

        var noProduct = await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = false, stripEnabled = false,
        });
        // Printing on with nothing to print is a card that leads nowhere.
        Assert.Equal("product_required", await ErrorOf(noProduct));
    }

    [Fact]
    public async Task A_Printer_That_Cannot_Do_10x15_Is_Refused_At_Configuration_Time()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(
            host.OwnerId, formats: "{\"formats\":[\"a4\"]}");

        var refused = await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10,
        });
        // Both products compose a 10x15 sheet: a printer that cannot do that
        // size cannot print either, and the host learns it now.
        Assert.Equal("format_unsupported", await ErrorOf(refused));
    }

    [Fact]
    public async Task A_Revoked_Station_Cannot_Be_Chosen()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId, revoked: true);

        var refused = await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10,
        });
        Assert.Equal("station_unavailable", await ErrorOf(refused));
    }

    [Fact]
    public async Task A_Host_Cannot_Aim_Their_Party_At_Someone_Else_Is_Printer()
    {
        var host = await SeedHostAsync();
        var stranger = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(stranger.OwnerId);

        var refused = await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10,
        });
        // The device exists and does 10x15 — but not for this host.
        Assert.Equal("station_unavailable", await ErrorOf(refused));
    }

    [Fact]
    public async Task Another_Host_Is_Party_Is_Not_Found_Rather_Than_Forbidden()
    {
        var host = await SeedHostAsync();
        var stranger = await SeedHostAsync();

        var read = await stranger.Client.GetAsync(
            $"/api/albums/{host.AlbumId}/party-print-settings");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        var write = await stranger.Client.PatchAsJsonAsync(
            $"/api/albums/{host.AlbumId}/party-print-settings", new { photoMaxPrints = 5 });
        // Foreign and missing are indistinguishable, so a probe learns nothing.
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task The_Settings_Are_Owner_Only()
    {
        var host = await SeedHostAsync();
        var anon = _factory.CreateClient();
        var response = await anon.GetAsync($"/api/albums/{host.AlbumId}/party-print-settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_Host_Is_Line_Is_Cleaned_Up_But_Never_Silently_Cut()
    {
        var host = await SeedHostAsync();

        var messy = await SaveAsync(host, new { footerText = "  Buon\tcompleanno\n  Anna  " });
        messy.EnsureSuccessStatusCode();
        var body = await messy.Content.ReadFromJsonAsync<JsonElement>();
        // One line, because the renderer's footer band is one line.
        Assert.Equal("Buon compleanno Anna", body.GetProperty("footerText").GetString());

        var tooLong = await SaveAsync(host, new
        {
            footerText = new string('x', PartyPrintLimits.FooterMaxLength + 1),
        });
        // Refused rather than truncated: a host must not discover the cut on paper.
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("footer_too_long", await ErrorOf(tooLong));
    }

    [Fact]
    public async Task An_Empty_Line_Removes_It_While_An_Absent_Field_Keeps_It()
    {
        var host = await SeedHostAsync();
        (await SaveAsync(host, new { footerText = "Auguri Anna" })).EnsureSuccessStatusCode();

        // An unrelated save must not wipe the line the host wrote.
        var untouched = await SaveAsync(host, new { photoEnabled = false });
        untouched.EnsureSuccessStatusCode();
        Assert.Equal("Auguri Anna",
            (await untouched.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("footerText").GetString());

        // Clearing the box is how a host removes it, and it stores absence
        // rather than a blank line the renderer would reserve a band for.
        var cleared = await SaveAsync(host, new { footerText = "" });
        cleared.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("footerText").ValueKind);
    }

    [Fact]
    public async Task Configuring_Printing_Is_Audited_Without_The_Host_Is_Words()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 25, footerText = "Auguri Anna",
        })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.AuditLogs
            .Where(a => a.Action == NubArca.Api.Audit.AuditActions.PartyPrintConfigure)
            .OrderByDescending(a => a.CreatedAt)
            .FirstAsync();

        // What was configured is a security question; what it SAID is not.
        Assert.Contains("photoMaxPrints", entry.MetadataJson!);
        Assert.Contains("\"hasFooterText\":true", entry.MetadataJson!);
        Assert.DoesNotContain("Auguri Anna", entry.MetadataJson!);
    }

    [Fact]
    public async Task The_Guest_Hub_Follows_The_Host_Is_Switch()
    {
        var host = await SeedHostAsync();
        var (stationId, deviceId) = await SeedPrinterAsync(host.OwnerId);
        var party = await host.Client.PatchAsJsonAsync(
            $"/api/albums/{host.AlbumId}/party-settings", new { enabled = true });
        party.EnsureSuccessStatusCode();
        var viewToken = (await party.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

        var anon = _factory.CreateClient();
        var before = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("printUrl").ValueKind);

        (await SaveAsync(host, new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10,
        })).EnsureSuccessStatusCode();

        // The panel is the only thing that decides; the hub just reports it.
        var after = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        Assert.Equal(JsonValueKind.String, after.GetProperty("printUrl").ValueKind);

        (await SaveAsync(host, new { enabled = false })).EnsureSuccessStatusCode();
        var off = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        Assert.Equal(JsonValueKind.Null, off.GetProperty("printUrl").ValueKind);
    }
}

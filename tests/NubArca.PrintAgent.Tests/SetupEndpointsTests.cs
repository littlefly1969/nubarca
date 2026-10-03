using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Networking;
using NubArca.PrintAgent.Setup;

namespace NubArca.PrintAgent.Tests;

public sealed class SetupEndpointsTests : IAsyncLifetime
{
    private const string Secret = "S3cret-Password!";
    private readonly FakeNetworkManager _network = new();
    private readonly (ILoggerFactory Factory, CapturingLoggerProvider Provider) _logs = CapturingLoggerProvider.Create();
    private readonly string _fakeOutput = Path.Combine(Path.GetTempPath(), $"nubarca-setup-{Guid.NewGuid():N}");
    private NetworkProvisioningService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _service = new NetworkProvisioningService(_network,
            new NetworkProvisioningOptions { Enabled = true, AccessPointPassword = "setup-pass" },
            _logs.Factory.CreateLogger<NetworkProvisioningService>(),
            new ProvisioningTimings(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10),
                TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.Zero),
            boxSuffix: "A7F3");
        await _service.BootAsync(default); // no network: setup mode
        var status = new PrintBoxStatusService(_service,
            new FakePrinterAdapter(_fakeOutput, TimeSpan.Zero, remainingPrints: 187),
            new AgentConnectionState(), new PrintAgentOptions());
        _app = SetupEndpoints.Build(_service, status, _logs.Factory, IPAddress.Loopback, 0);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_fakeOutput)) Directory.Delete(_fakeOutput, recursive: true);
    }

    private Task<HttpResponseMessage> Connect(string json) =>
        _http.PostAsync("/setup/wifi/connect", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task An_Offline_Printers_Last_Count_Is_Not_Shown_As_Current()
    {
        var status = new PrintBoxStatusService(_service, new OfflineWithCount(),
            new AgentConnectionState(), new PrintAgentOptions());
        var printer = (await status.GetAsync(default)).Printer!;
        Assert.Equal("offline", printer.State);
        Assert.Null(printer.RemainingPrints);
    }

    /// <summary>A printer that still has a cached count but is no longer there.</summary>
    private sealed class OfflineWithCount : IPrinterAdapter
    {
        public string Kind => "fake";
        public Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredPrinter>>([new("p", "DNP DS-RX1HS", "DNP", "RX1HS", "fake")]);
        public Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter p, CancellationToken ct) =>
            Task.FromResult(new PrinterCapabilities(["10x15"], true));
        public Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter p, CancellationToken ct) =>
            Task.FromResult(new PrinterObservedStatus("offline"));
        public Task<PrintSubmissionResult> SubmitAsync(PrintSubmission s, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<PrinterMediaStatus> GetMediaStatusAsync(DiscoveredPrinter p, CancellationToken ct) =>
            Task.FromResult(new PrinterMediaStatus(187));
    }

    [Fact]
    public async Task The_Page_Is_Served_Locked_Down()
    {
        var response = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NubArca Print Box", await response.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/setup.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/setup.css")).StatusCode);
    }

    /// <summary>A client that reports a redirect instead of following it, as the phone's probe sees it.</summary>
    private HttpClient NotFollowing() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = _http.BaseAddress };

    [Theory]
    [InlineData("/generate_204")]              // Android
    [InlineData("/gen_204")]
    [InlineData("/hotspot-detect.html")]       // Apple
    [InlineData("/library/test/success.html")]
    [InlineData("/connecttest.txt")]           // Windows
    [InlineData("/ncsi.txt")]
    [InlineData("/redirect")]
    public async Task A_Connectivity_Probe_Is_Sent_To_The_Page(string probe)
    {
        using var client = NotFollowing();
        var response = await client.GetAsync(probe);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_Stray_Address_Lands_On_The_Page_And_The_Real_Ones_Still_Answer()
    {
        using var client = NotFollowing();
        var stray = await client.GetAsync("/some/where/else");
        Assert.Equal(HttpStatusCode.Redirect, stray.StatusCode);
        Assert.Equal("/", stray.Headers.Location?.OriginalString);

        // The fallback never shadows the page, its assets or the API.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/setup.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/setup/status")).StatusCode);
    }

    [Fact]
    public async Task Over_Ethernet_The_Page_Configures_The_Wifi_Without_Leaving_The_Cable()
    {
        var ethernet = new NetworkUplink("eth0", "ethernet", "Wired connection 1", "192.0.2.9");
        var network = new FakeNetworkManager { Uplink = ethernet };
        var service = new NetworkProvisioningService(network,
            new NetworkProvisioningOptions { Enabled = true, AccessPointPassword = "setup-pass" },
            _logs.Factory.CreateLogger<NetworkProvisioningService>(),
            new ProvisioningTimings(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10),
                TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.Zero),
            boxSuffix: "A7F3");
        await service.BootAsync(default);
        var status = new PrintBoxStatusService(service,
            new FakePrinterAdapter(_fakeOutput, TimeSpan.Zero, remainingPrints: 187),
            new AgentConnectionState(), new PrintAgentOptions());
        await using var app = SetupEndpoints.Build(service, status, _logs.Factory, IPAddress.Loopback, 0);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var before = JsonDocument.Parse(await http.GetStringAsync("/setup/status")).RootElement;
        Assert.Equal("connected", before.GetProperty("network").GetString());
        Assert.Equal("ethernet", before.GetProperty("connectionType").GetString());
        Assert.True(before.GetProperty("wifiConfigurationAvailable").GetBoolean());

        var response = await http.PostAsync("/setup/wifi/connect", new StringContent(
            $$"""{"ssid":"Studio","password":"{{Secret}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await service.PendingConnect;

        var after = JsonDocument.Parse(await http.GetStringAsync("/setup/status")).RootElement;
        Assert.Equal("connected", after.GetProperty("lastAttempt").GetProperty("outcome").GetString());
        Assert.Equal("ethernet", after.GetProperty("connectionType").GetString());
        Assert.Equal(0, network.AccessPointStarts);
        Assert.Equal(0, network.AccessPointStops);
        Assert.DoesNotContain(Secret, _logs.Provider.All);
        await app.StopAsync();
    }

    [Fact]
    public async Task Status_Says_How_The_Box_Is_Doing_And_Nothing_Secret()
    {
        var body = await _http.GetStringAsync("/setup/status");
        var status = JsonDocument.Parse(body).RootElement;
        Assert.Equal("access-point", status.GetProperty("network").GetString());
        Assert.Equal("NubArca-Print-A7F3", status.GetProperty("setupNetwork").GetString());
        Assert.Equal("10.42.0.1", status.GetProperty("address").GetString());
        Assert.Equal("ready", status.GetProperty("networkManager").GetString());
        Assert.Equal("not-used", status.GetProperty("cups").GetString());
        Assert.Equal("ready", status.GetProperty("printer").GetProperty("state").GetString());
        // The printer's own count of the prints left on its media.
        Assert.Equal(187, status.GetProperty("printer").GetProperty("remainingPrints").GetInt32());
        Assert.Equal("disconnected", status.GetProperty("nubarca").GetString());
        // In setup mode the page can configure the Wi-Fi.
        Assert.True(status.GetProperty("wifiConfigurationAvailable").GetBoolean());
        Assert.DoesNotContain("setup-pass", body);
    }

    [Fact]
    public async Task The_Networks_Seen_Before_Setup_Mode_Are_Listed()
    {
        var networks = await _http.GetFromJsonAsync<List<WifiNetwork>>("/setup/wifi",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(["Location-WiFi", "Studio", "Phone Hotspot"], networks!.Select(n => n.Ssid));
        Assert.False(networks![2].Secure);

        // A page reloaded in a loop does not make the radio scan in a loop.
        var scans = _network.Scans;
        for (var i = 0; i < 5; i++) await _http.GetStringAsync("/setup/wifi");
        Assert.True(_network.Scans - scans <= 1, $"{_network.Scans - scans} scans for 5 requests");
    }

    [Theory]
    [InlineData("""{"ssid":"","password":"correct-horse"}""", "invalid_ssid")]
    [InlineData("""{"ssid":"--help","password":"correct-horse"}""", "invalid_ssid")]
    [InlineData("""{"ssid":"a\u0007b","password":"correct-horse"}""", "invalid_ssid")]
    [InlineData("""{"ssid":"0123456789012345678901234567890123","password":"correct-horse"}""", "invalid_ssid")]
    [InlineData("""{"ssid":"Location-WiFi","password":"short"}""", "invalid_password")]
    [InlineData("""{"ssid":"Location-WiFi","password":"pässwörd-ünicode"}""", "invalid_password")]
    [InlineData("{\"ssid\":\"Location-WiFi\",\"password\":", "invalid_request")]
    [InlineData("""not json""", "invalid_request")]
    public async Task Invalid_Requests_Are_Refused_Before_NetworkManager(string json, string error)
    {
        var response = await Connect(json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetString());
        Assert.Equal(0, _network.Connects);
    }

    [Fact]
    public async Task Only_Json_Is_Accepted()
    {
        var response = await _http.PostAsync("/setup/wifi/connect",
            new FormUrlEncodedContent([new("ssid", "Location-WiFi"), new("password", Secret)]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _network.Connects);
    }

    [Fact]
    public async Task A_Failed_Attempt_Restores_Setup_Mode_And_Never_Echoes_The_Password()
    {
        _network.Joins = (_, _) => false;
        var response = await Connect($$"""{"ssid":"Location-WiFi","password":"{{Secret}}"}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadAsStringAsync();
        Assert.Contains("connecting", accepted);
        Assert.DoesNotContain(Secret, accepted);

        await _service.PendingConnect;
        var status = await _http.GetStringAsync("/setup/status");
        var root = JsonDocument.Parse(status).RootElement;
        Assert.Equal("access-point", root.GetProperty("network").GetString());
        Assert.Equal("failed", root.GetProperty("lastAttempt").GetProperty("outcome").GetString());
        Assert.Equal("Location-WiFi", root.GetProperty("lastAttempt").GetProperty("ssid").GetString());
        Assert.True(_network.AccessPointActive);

        Assert.DoesNotContain(Secret, status);
        Assert.DoesNotContain(Secret, _logs.Provider.All);
    }

    [Fact]
    public async Task A_Successful_Attempt_Leaves_Setup_Mode_And_Refuses_Another()
    {
        var response = await Connect($$"""{"ssid":"Location-WiFi","password":"{{Secret}}"}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _service.PendingConnect;
        Assert.Equal(ProvisioningMode.Connected, _service.State.Mode);
        Assert.False(_network.AccessPointActive);

        // Now on Wi-Fi: changing network would cut the box off, so it is refused.
        var again = await Connect("""{"ssid":"Studio","password":"another-pass"}""");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("unavailable", JsonDocument.Parse(await again.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetString());
        var status = JsonDocument.Parse(await _http.GetStringAsync("/setup/status")).RootElement;
        Assert.False(status.GetProperty("wifiConfigurationAvailable").GetBoolean());
        Assert.DoesNotContain(Secret, _logs.Provider.All);
    }
}

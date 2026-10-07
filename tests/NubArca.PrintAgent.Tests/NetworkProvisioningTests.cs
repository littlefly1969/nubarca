using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Tests;

public sealed class NetworkProvisioningTests
{
    private static readonly NetworkUplink Ethernet = new("eth0", "ethernet", "Wired connection 1", "192.0.2.9");
    private static readonly NetworkUplink HomeWifi = new("wlan0", "wifi", "Home", "192.0.2.30");

    private static ProvisioningTimings Fast(TimeSpan? retry = null) => new(
        Grace: TimeSpan.FromMilliseconds(300),
        Poll: TimeSpan.FromMilliseconds(20),
        Verify: TimeSpan.FromMilliseconds(200),
        HandOff: TimeSpan.FromMilliseconds(10),
        FirstRetry: retry ?? TimeSpan.FromHours(1),
        MaxRetry: TimeSpan.FromHours(1),
        UserQuiet: TimeSpan.Zero);

    private static NetworkProvisioningService Service(FakeNetworkManager network, ProvisioningTimings? timings = null,
        ILogger<NetworkProvisioningService>? logger = null, string password = "") =>
        new(network, new NetworkProvisioningOptions { Enabled = true, AccessPointPassword = password },
            logger ?? NullLogger<NetworkProvisioningService>.Instance, timings ?? Fast(), boxSuffix: "A7F3");

    [Fact]
    public async Task Already_Connected_At_Boot_Opens_No_Setup_Network()
    {
        var network = new FakeNetworkManager { Uplink = HomeWifi };
        var service = Service(network);
        await service.BootAsync(default);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.Equal(0, network.AccessPointStarts);
    }

    [Fact]
    public async Task A_Network_That_Comes_Back_Within_The_Grace_Period_Opens_No_Setup_Network()
    {
        var network = new FakeNetworkManager();
        var service = Service(network);
        var boot = service.BootAsync(default);
        await Task.Delay(100);
        network.Uplink = HomeWifi; // NetworkManager auto-connected
        await boot;
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.Equal(0, network.AccessPointStarts);
    }

    [Fact]
    public async Task No_Network_After_The_Grace_Period_Opens_The_Setup_Network()
    {
        var network = new FakeNetworkManager();
        var service = Service(network, password: "setup-pass");
        await service.BootAsync(default);

        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.Equal(1, network.AccessPointStarts);
        Assert.Equal("NubArca-Print-A7F3", network.LastAccessPointSsid);
        Assert.Equal("setup-pass", network.LastAccessPointPassword);
        Assert.Equal("10.42.0.1", service.State.AccessPointAddress);
        // Scanned BEFORE the radio became an access point.
        Assert.Equal(3, service.Networks.Count);
    }

    [Fact]
    public async Task Ethernet_Is_Enough_And_Closes_A_Setup_Network_That_Was_Open()
    {
        var wired = new FakeNetworkManager { Uplink = Ethernet };
        var service = Service(wired);
        await service.BootAsync(default);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.Equal(0, wired.AccessPointStarts);

        var network = new FakeNetworkManager();
        var open = Service(network);
        await open.BootAsync(default);
        network.Uplink = Ethernet; // a cable plugged in while in setup mode
        await open.TickAsync(default);
        Assert.Equal(ProvisioningMode.Connected, open.State.Mode);
        Assert.False(network.AccessPointActive);
    }

    [Fact]
    public async Task A_Phone_Joins_A_Network_And_The_Setup_Network_Stays_Closed()
    {
        var network = new FakeNetworkManager();
        var service = Service(network);
        await service.BootAsync(default);

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Location-WiFi", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.False(network.AccessPointActive);
        Assert.Equal(1, network.AccessPointStarts);
        Assert.Equal(ConnectOutcome.Connected, service.State.LastAttempt?.Outcome);
        Assert.Equal("Location-WiFi", service.State.Uplink?.Name);
    }

    [Fact]
    public async Task A_Wrong_Password_Brings_The_Setup_Network_Back()
    {
        var (loggers, logs) = CapturingLoggerProvider.Create();
        var network = new FakeNetworkManager { Joins = (_, _) => false };
        var service = Service(network, logger: loggers.CreateLogger<NetworkProvisioningService>());
        await service.BootAsync(default);

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Location-WiFi", "wrong-password-123"));
        await service.PendingConnect;

        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.True(network.AccessPointActive);
        Assert.Equal(2, network.AccessPointStarts);
        Assert.Equal(1, network.Forgets); // not retried with the wrong password at every boot
        Assert.Equal("Location-WiFi", network.LastForgotten);
        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
        Assert.Contains("Setup network restored", logs.All);
        Assert.DoesNotContain("wrong-password-123", logs.All);
    }

    [Fact]
    public async Task A_Connection_Without_An_Address_Counts_As_Failed()
    {
        // nmcli says the connection is up, but no address and route ever follow:
        // a box that cannot reach anything is not connected.
        var network = new FakeNetworkManager { AddressAfterJoin = false };
        var service = Service(network);
        await service.BootAsync(default);

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Location-WiFi", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.True(network.AccessPointActive);
        Assert.Equal(1, network.Forgets);
        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
    }

    [Fact]
    public async Task From_Setup_Mode_Success_Means_The_Requested_Wifi_Profile_Itself()
    {
        // T8: nmcli reports the join, but the radio ends up on ANOTHER of the
        // box's profiles — and meanwhile a cable is plugged in. Neither is the
        // network the person chose.
        var network = new FakeNetworkManager
        {
            WifiAfterJoin = _ => new WifiClientState(true, "nubarca-wifi-Home", "192.0.2.30", true),
        };
        var service = Service(network);
        await service.BootAsync(default);
        network.Uplink = Ethernet;

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
        Assert.Equal("Studio", network.LastForgotten);
    }

    [Fact]
    public async Task From_Setup_Mode_A_Wifi_Without_A_Gateway_Is_Not_Joined()
    {
        var network = new FakeNetworkManager
        {
            WifiAfterJoin = ssid => new WifiClientState(true, NetworkManagerCli.ClientConnectionPrefix + ssid, "192.0.2.20", false),
        };
        var service = Service(network);
        await service.BootAsync(default);

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.True(network.AccessPointActive);
    }

    // --- Over Ethernet ---------------------------------------------------------

    private static async Task<(FakeNetworkManager Network, NetworkProvisioningService Service)> OnEthernetAsync(
        Action<FakeNetworkManager>? configure = null)
    {
        var network = new FakeNetworkManager { Uplink = Ethernet };
        configure?.Invoke(network);
        var service = Service(network);
        await service.BootAsync(default);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        return (network, service);
    }

    [Fact]
    public async Task Over_Ethernet_The_Wifi_Is_Scanned()
    {
        // T1
        var (network, service) = await OnEthernetAsync();
        Assert.True(service.CanConfigureWifi);
        var scans = network.Scans;
        var found = await service.RefreshNetworksAsync(default);
        Assert.Equal(scans + 1, network.Scans);
        Assert.Equal(["Location-WiFi", "Studio", "Phone Hotspot"], found.Select(n => n.Ssid));
    }

    [Fact]
    public async Task Over_Ethernet_A_Connection_Request_Is_Accepted()
    {
        // T2
        var (_, service) = await OnEthernetAsync();
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;
    }

    [Fact]
    public async Task On_Wifi_The_Wifi_Cannot_Be_Reconfigured()
    {
        // T3: one radio — changing network would cut the uplink the page is on.
        var network = new FakeNetworkManager { Uplink = HomeWifi };
        var service = Service(network);
        await service.BootAsync(default);
        Assert.False(service.CanConfigureWifi);
        Assert.Equal(ConnectRequest.Unavailable, service.RequestConnect("Studio", "correct-horse"));
        var scans = network.Scans;
        await service.RefreshNetworksAsync(default);
        Assert.Equal(scans, network.Scans);
        Assert.Equal(0, network.Connects);
    }

    [Fact]
    public async Task Over_Ethernet_The_Cable_Never_Makes_A_Failed_Wifi_Look_Joined()
    {
        // T4, the regression: nmcli calls the join done, the Wi-Fi never gets
        // there — and Ethernet is up the whole time.
        var (network, service) = await OnEthernetAsync(n => n.AddressAfterJoin = false);
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
        Assert.Equal("Studio", network.LastForgotten);
        Assert.Equal(Ethernet, network.Uplink);
    }

    [Fact]
    public async Task Over_Ethernet_A_Joined_Wifi_Leaves_The_Cable_Alone()
    {
        // T5
        var (network, service) = await OnEthernetAsync();
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ConnectOutcome.Connected, service.State.LastAttempt?.Outcome);
        Assert.Equal("nubarca-wifi-Studio", network.WifiClient.ConnectionName);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.Equal(Ethernet, service.State.Uplink);
        Assert.Equal(Ethernet, network.Uplink);
        Assert.Equal(0, network.AccessPointStops);
        Assert.Equal(0, network.AccessPointStarts);
    }

    [Fact]
    public async Task Over_Ethernet_A_Failed_Wifi_Opens_No_Setup_Network()
    {
        // T6
        var (network, service) = await OnEthernetAsync(n => n.Joins = (_, _) => false);
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "wrong-password-1"));
        await service.PendingConnect;

        Assert.Equal(ConnectOutcome.Failed, service.State.LastAttempt?.Outcome);
        Assert.Equal("Studio", network.LastForgotten);
        Assert.Equal(0, network.AccessPointStarts);
        Assert.False(network.AccessPointActive);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.True(service.CanConfigureWifi, "the page can try again at once");
    }

    [Fact]
    public async Task Only_One_Attempt_At_A_Time_And_Only_In_Setup_Mode()
    {
        var connected = Service(new FakeNetworkManager { Uplink = HomeWifi });
        await connected.BootAsync(default);
        Assert.Equal(ConnectRequest.Unavailable, connected.RequestConnect("Location-WiFi", "correct-horse"));

        var service = Service(new FakeNetworkManager(), Fast() with { HandOff = TimeSpan.FromMilliseconds(200) });
        await service.BootAsync(default);
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Location-WiFi", "correct-horse"));
        Assert.Equal(ConnectRequest.Busy, service.RequestConnect("Studio", "another-pass"));
        await service.PendingConnect;
    }

    [Fact]
    public async Task A_Lost_Network_Gets_The_Grace_Period_Before_The_Setup_Network_Opens()
    {
        var network = new FakeNetworkManager { Uplink = HomeWifi };
        var service = Service(network);
        await service.BootAsync(default);

        network.Uplink = null; // router rebooting, roaming, DHCP renewal…
        await service.TickAsync(default);
        await Task.Delay(100);
        await service.TickAsync(default);
        Assert.Equal(0, network.AccessPointStarts);

        await Task.Delay(300);
        await service.TickAsync(default);
        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.Equal(1, network.AccessPointStarts);
    }

    [Fact]
    public async Task A_Router_That_Was_Only_Late_Is_Found_Again_Without_A_Phone()
    {
        var network = new FakeNetworkManager();
        var service = Service(network, Fast(retry: TimeSpan.FromMilliseconds(50)));
        await service.BootAsync(default);
        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);

        network.KnownNetwork = HomeWifi; // it answers once the radio is free
        await Task.Delay(80);
        await service.TickAsync(default);
        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.Equal("Home", service.State.Uplink?.Name);
    }

    [Fact]
    public async Task Retrying_Known_Networks_Backs_Off_And_Pauses_While_Someone_Is_On_The_Page()
    {
        var network = new FakeNetworkManager();
        var service = Service(network, Fast(retry: TimeSpan.FromMilliseconds(50)) with
        {
            UserQuiet = TimeSpan.FromMinutes(5),
        });
        await service.BootAsync(default);

        service.NoteUserActivity();
        await Task.Delay(80);
        await service.TickAsync(default);
        // Someone is choosing a network: the setup network does not blink away.
        Assert.Equal(0, network.AccessPointStops);
        Assert.Equal(1, network.AccessPointStarts);
    }

    [Fact]
    public async Task Without_NetworkManager_The_Service_Reports_It_And_Keeps_Running()
    {
        var (loggers, logs) = CapturingLoggerProvider.Create();
        var network = new FakeNetworkManager { Available = false };
        var service = Service(network, logger: loggers.CreateLogger<NetworkProvisioningService>());
        await service.StartAsync(default);
        await Eventually.TrueAsync(() => service.State.Mode == ProvisioningMode.Unavailable);
        Assert.False(service.State.ManagerAvailable);
        Assert.Contains("NetworkManager is not running", logs.All);

        network.Available = true;
        network.Uplink = Ethernet;
        await Eventually.TrueAsync(() => service.State.Mode == ProvisioningMode.Connected);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task An_Open_Setup_Network_Is_Warned_About()
    {
        var (loggers, logs) = CapturingLoggerProvider.Create();
        var service = Service(new FakeNetworkManager { Uplink = Ethernet },
            logger: loggers.CreateLogger<NetworkProvisioningService>());
        await service.StartAsync(default);
        await Eventually.TrueAsync(() => logs.All.Contains("is OPEN"));
        await service.StopAsync(default);
    }
}

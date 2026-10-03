using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Tests;

/// <summary>
/// Plain http://&lt;gateway&gt;/ on the setup network: nft sends port 80 to the
/// setup page while — and only while — the setup network is up.
/// </summary>
public sealed class CaptivePortalTests
{
    private static readonly NetworkUplink Ethernet = new("eth0", "ethernet", "Wired connection 1", "192.0.2.9");

    private static ProvisioningTimings Fast() => new(
        Grace: TimeSpan.FromMilliseconds(100),
        Poll: TimeSpan.FromMilliseconds(20),
        Verify: TimeSpan.FromMilliseconds(200),
        HandOff: TimeSpan.FromMilliseconds(10),
        FirstRetry: TimeSpan.FromHours(1),
        MaxRetry: TimeSpan.FromHours(1),
        UserQuiet: TimeSpan.Zero);

    private static (FakeNetworkManager Network, FakeCaptivePortalRedirect Redirect, NetworkProvisioningService Service)
        Box(Action<FakeNetworkManager>? configure = null, ILogger<NetworkProvisioningService>? logger = null)
    {
        var network = new FakeNetworkManager();
        configure?.Invoke(network);
        var redirect = new FakeCaptivePortalRedirect(network.Events);
        var service = new NetworkProvisioningService(network,
            new NetworkProvisioningOptions { Enabled = true, AccessPointPassword = "setup-pass" },
            logger ?? NullLogger<NetworkProvisioningService>.Instance, Fast(), boxSuffix: "A7F3", captive: redirect);
        return (network, redirect, service);
    }

    // --- nft -----------------------------------------------------------------

    [Fact]
    public void The_Redirect_Is_One_Table_Of_Its_Own_Replaced_In_One_Transaction()
    {
        Assert.Equal(
            "add table ip nubarca_setup; delete table ip nubarca_setup; add table ip nubarca_setup; "
            + "add chain ip nubarca_setup prerouting { type nat hook prerouting priority -100 ; policy accept ; }; "
            + "add rule ip nubarca_setup prerouting iifname \"wlp1s0\" tcp dport 80 redirect to :8080",
            NftCaptivePortalRedirect.EnableCommand("wlp1s0", 8080));
        // Removing it cannot fail because it is already gone.
        Assert.Equal("add table ip nubarca_setup; delete table ip nubarca_setup", NftCaptivePortalRedirect.DisableCommand());
    }

    [Fact]
    public async Task Nft_Is_Run_Directly_With_The_Ruleset_As_One_Argument()
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok());
        var redirect = new NftCaptivePortalRedirect(runner, NullLogger<NftCaptivePortalRedirect>.Instance);
        Assert.True(await redirect.EnableAsync("wlan0", 8080, default));
        await redirect.DisableAsync(default);

        var calls = runner.Calls.ToArray();
        Assert.All(calls, c => Assert.Equal("nft", c.File));
        Assert.Equal([NftCaptivePortalRedirect.EnableCommand("wlan0", 8080)], calls[0].Args);
        Assert.Equal([NftCaptivePortalRedirect.DisableCommand()], calls[1].Args);
    }

    [Theory]
    [InlineData("wlan0\"; flush ruleset; \"", 8080)]
    [InlineData("wlan 0", 8080)]
    [InlineData("", 8080)]
    [InlineData("a-very-long-interface-name", 8080)]
    [InlineData("wlan0", 0)]
    [InlineData("wlan0", 70000)]
    public async Task Nothing_That_Is_Not_An_Interface_And_A_Port_Reaches_Nft(string wifiInterface, int port)
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok());
        var redirect = new NftCaptivePortalRedirect(runner, NullLogger<NftCaptivePortalRedirect>.Instance);
        Assert.False(await redirect.EnableAsync(wifiInterface, port, default));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Without_The_Capability_It_Fails_And_Says_So()
    {
        var (loggers, logs) = CapturingLoggerProvider.Create();
        var runner = new FakeProcessRunner((_, _) =>
            FakeProcessRunner.Fail("Error: Could not process rule: Operation not permitted\nadd table ip nubarca_setup"));
        var redirect = new NftCaptivePortalRedirect(runner, loggers.CreateLogger<NftCaptivePortalRedirect>());
        Assert.False(await redirect.EnableAsync("wlan0", 8080, default));
        Assert.Contains("Operation not permitted", logs.All);
        Assert.Contains("stays on port 8080", logs.All);

        var missing = new NftCaptivePortalRedirect(new FakeProcessRunner((file, _) => ProcessResult.Missing(file)),
            NullLogger<NftCaptivePortalRedirect>.Instance);
        Assert.False(await missing.EnableAsync("wlan0", 8080, default));
        await missing.DisableAsync(default); // never throws
    }

    // --- Lifecycle -------------------------------------------------------------

    [Fact]
    public async Task The_Redirect_Comes_On_Only_Once_The_Setup_Network_Is_Up()
    {
        var (network, redirect, service) = Box();
        await service.BootAsync(default);

        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.True(redirect.Active);
        // Cleared at boot (a crash may have left one), then on after the AP is up, on the page's own port.
        Assert.Equal(["redirect-off", "ap-start", "redirect-on wlan0:8080"], network.Events);
    }

    [Fact]
    public async Task A_Cable_Plugged_In_Removes_The_Redirect_Before_The_Setup_Network_Goes()
    {
        var (network, redirect, service) = Box();
        await service.BootAsync(default);
        network.Events.Clear();
        network.Uplink = Ethernet;
        await service.TickAsync(default);

        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.False(redirect.Active);
        Assert.Equal(["redirect-off", "ap-stop"], network.Events);
    }

    [Fact]
    public async Task A_Phones_Attempt_Removes_It_And_A_Failure_Puts_It_Back_With_The_Setup_Network()
    {
        var (network, redirect, service) = Box(n => n.Joins = (_, _) => false);
        await service.BootAsync(default);
        network.Events.Clear();

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "wrong-password-1"));
        await service.PendingConnect;

        Assert.Equal(["redirect-off", "ap-stop", "ap-start", "redirect-on wlan0:8080"], network.Events);
        Assert.True(redirect.Active);
        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
    }

    [Fact]
    public async Task A_Joined_Network_Leaves_No_Redirect_Behind()
    {
        var (network, redirect, service) = Box();
        await service.BootAsync(default);

        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "correct-horse"));
        await service.PendingConnect;

        Assert.Equal(ProvisioningMode.Connected, service.State.Mode);
        Assert.False(redirect.Active);
        Assert.False(network.AccessPointActive);
    }

    [Fact]
    public async Task Over_Ethernet_No_Redirect_Is_Ever_Installed()
    {
        var (network, redirect, service) = Box(n => n.Uplink = Ethernet);
        await service.BootAsync(default);
        Assert.Equal(ConnectRequest.Accepted, service.RequestConnect("Studio", "wrong-password-1"));
        await service.PendingConnect;

        Assert.DoesNotContain(network.Events, e => e.StartsWith("redirect-on", StringComparison.Ordinal));
        Assert.False(redirect.Active);
    }

    [Fact]
    public async Task Without_The_Redirect_The_Setup_Network_Still_Opens_On_The_Pages_Own_Port()
    {
        var (loggers, logs) = CapturingLoggerProvider.Create();
        var (network, redirect, service) = Box(logger: loggers.CreateLogger<NetworkProvisioningService>());
        redirect.Works = false;
        await service.BootAsync(default);

        Assert.Equal(ProvisioningMode.AccessPoint, service.State.Mode);
        Assert.True(network.AccessPointActive);
        Assert.Contains("http://10.42.0.1:8080/", logs.All);
        Assert.DoesNotContain("setup-pass", logs.All);
    }

    [Fact]
    public async Task Stopping_The_Agent_Removes_The_Redirect()
    {
        var (network, redirect, service) = Box();
        await service.StartAsync(default);
        await Eventually.TrueAsync(() => redirect.Active);
        await service.StopAsync(default);
        Assert.False(redirect.Active);
        Assert.Equal("redirect-off", network.Events.Last());
    }
}

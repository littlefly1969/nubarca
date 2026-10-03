using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Tests;

public sealed class NetworkManagerCliTests
{
    [Fact]
    public void Terse_Fields_Unescape_Colons_And_Backslashes()
    {
        Assert.Equal(["My:Net", "82", "WPA2"], NmcliTerse.Split(@"My\:Net:82:WPA2"));
        Assert.Equal([@"a\b", "", "x"], NmcliTerse.Split(@"a\\b::x"));
    }

    [Fact]
    public void Networks_Are_Deduplicated_Hidden_Ones_Dropped_Strongest_First()
    {
        var networks = NmcliTerse.Networks("""
            Location-WiFi:40:WPA2
            Location-WiFi:82:WPA2
            :90:WPA2
            Cafe:55:
            Old:60:--
            Studio\:5G:70:WPA3
            """);
        Assert.Equal(
            [new WifiNetwork("Location-WiFi", 82, true), new WifiNetwork("Studio:5G", 70, true),
             new WifiNetwork("Old", 60, false), new WifiNetwork("Cafe", 55, false)],
            networks);
    }

    [Fact]
    public void An_Address_Counts_Only_With_A_Default_Route()
    {
        Assert.Equal(("192.0.2.5", true), NmcliTerse.Addressing("IP4.ADDRESS[1]:192.0.2.5/24\nIP4.GATEWAY:192.0.2.1\nIP6.GATEWAY:\n"));
        Assert.Equal(("10.42.0.1", false), NmcliTerse.Addressing("IP4.ADDRESS[1]:10.42.0.1/24\nIP4.GATEWAY:--\nIP6.GATEWAY:\n"));
        Assert.Equal((null, false), NmcliTerse.Addressing(""));
    }

    private static FakeProcessRunner Nmcli(string devices, Dictionary<string, string>? show = null, bool running = true) =>
        new((file, args) =>
        {
            if (file != "nmcli") return ProcessResult.Missing(file);
            if (args.Contains("general")) return FakeProcessRunner.Ok(running ? "running\n" : "asleep\n");
            if (args.Contains("status")) return FakeProcessRunner.Ok(devices);
            if (args.Contains("show")) return FakeProcessRunner.Ok(show?.GetValueOrDefault(args[^1]) ?? "");
            return FakeProcessRunner.Ok();
        });

    [Fact]
    public async Task Ethernet_With_A_Gateway_Is_The_Uplink()
    {
        var cli = new NetworkManagerCli(Nmcli(
            "eth0:ethernet:connected:Wired connection 1\nwlan0:wifi:disconnected:\nlo:loopback:unmanaged:\n",
            new() { ["eth0"] = "IP4.ADDRESS[1]:192.0.2.9/24\nIP4.GATEWAY:192.0.2.1\n" }));
        var snapshot = await cli.GetSnapshotAsync(default);
        Assert.True(snapshot.ManagerAvailable);
        Assert.Equal("wlan0", snapshot.WifiInterface);
        Assert.Equal(new NetworkUplink("eth0", "ethernet", "Wired connection 1", "192.0.2.9"), snapshot.Uplink);
        Assert.False(snapshot.AccessPointActive);
    }

    [Fact]
    public async Task With_A_Cable_And_A_Wifi_Both_Up_The_Cable_Is_The_Uplink()
    {
        var cli = new NetworkManagerCli(Nmcli(
            "wlan0:wifi:connected:nubarca-wifi-Studio\neth0:ethernet:connected:Wired connection 1\n",
            new()
            {
                ["wlan0"] = "IP4.ADDRESS[1]:192.0.2.20/24\nIP4.GATEWAY:192.0.2.1\n",
                ["eth0"] = "IP4.ADDRESS[1]:192.0.2.9/24\nIP4.GATEWAY:192.0.2.1\n",
            }));
        Assert.Equal("ethernet", (await cli.GetSnapshotAsync(default)).Uplink?.Type);
    }

    [Fact]
    public async Task The_Setup_Network_Is_Recognised_And_Is_Not_An_Uplink()
    {
        var cli = new NetworkManagerCli(Nmcli(
            "wlan0:wifi:connected:nubarca-setup\n",
            new() { ["wlan0"] = "IP4.ADDRESS[1]:10.42.0.1/24\nIP4.GATEWAY:--\n" }));
        var snapshot = await cli.GetSnapshotAsync(default);
        Assert.Null(snapshot.Uplink);
        Assert.True(snapshot.AccessPointActive);
        Assert.Equal("10.42.0.1", snapshot.AccessPointAddress);
    }

    [Fact]
    public async Task A_Box_Profile_Shows_Its_Network_Name()
    {
        var cli = new NetworkManagerCli(Nmcli(
            @"wlan0:wifi:connected:nubarca-wifi-Location\:WiFi" + "\n",
            new() { ["wlan0"] = "IP4.ADDRESS[1]:192.0.2.20/24\nIP4.GATEWAY:192.0.2.1\n" }));
        Assert.Equal("Location:WiFi", (await cli.GetSnapshotAsync(default)).Uplink?.Name);
    }

    [Fact]
    public async Task No_Running_NetworkManager_Is_Unavailable_Not_An_Error()
    {
        Assert.False((await new NetworkManagerCli(Nmcli("", running: false)).GetSnapshotAsync(default)).ManagerAvailable);
        var absent = new NetworkManagerCli(new FakeProcessRunner((file, _) => ProcessResult.Missing(file)));
        Assert.Equal(NetworkSnapshot.Unavailable, await absent.GetSnapshotAsync(default));
    }

    [Fact]
    public void The_Wifi_Client_Is_Read_From_Its_Own_Device()
    {
        var joined = NmcliTerse.WifiClient(
            "GENERAL.STATE:100 (connected)\n" + @"GENERAL.CONNECTION:nubarca-wifi-Location\:WiFi" + "\n"
            + "IP4.ADDRESS[1]:192.0.2.20/24\nIP4.GATEWAY:192.0.2.1\n");
        Assert.Equal(new WifiClientState(true, "nubarca-wifi-Location:WiFi", "192.0.2.20", true), joined);

        // Still activating: not connected, whatever else is set.
        Assert.False(NmcliTerse.WifiClient("GENERAL.STATE:50 (connecting (configuring))\nGENERAL.CONNECTION:nubarca-wifi-Studio\n").Connected);
        // Connected, but no gateway came with the address.
        var noGateway = NmcliTerse.WifiClient(
            "GENERAL.STATE:100 (connected)\nGENERAL.CONNECTION:nubarca-wifi-Studio\nIP4.ADDRESS[1]:192.0.2.20/24\nIP4.GATEWAY:--\n");
        Assert.True(noGateway.Connected);
        Assert.False(noGateway.HasGateway);
        // Nothing at all.
        Assert.Equal(WifiClientState.Disconnected,
            NmcliTerse.WifiClient("GENERAL.STATE:30 (disconnected)\nGENERAL.CONNECTION:--\n"));
    }

    [Fact]
    public async Task The_Wifi_Client_Is_Asked_Of_The_Wifi_Interface_Only()
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok(
            "GENERAL.STATE:100 (connected)\nGENERAL.CONNECTION:nubarca-wifi-Studio\nIP4.ADDRESS[1]:192.0.2.20/24\nIP4.GATEWAY:192.0.2.1\n"));
        var state = await new NetworkManagerCli(runner).GetWifiClientStateAsync("wlan0", default);
        Assert.Equal("nubarca-wifi-Studio", state.ConnectionName);
        Assert.Equal(["-t", "-f", "GENERAL.STATE,GENERAL.CONNECTION,IP4.ADDRESS,IP4.GATEWAY", "device", "show", "wlan0"],
            runner.Calls.Single().Args);

        var failing = new NetworkManagerCli(new FakeProcessRunner((_, _) => FakeProcessRunner.Fail("Error: Device 'wlan0' not found.")));
        Assert.Equal(WifiClientState.Disconnected, await failing.GetWifiClientStateAsync("wlan0", default));
    }

    [Fact]
    public async Task Joining_Passes_Ssid_And_Password_As_Their_Own_Arguments()
    {
        var runner = Nmcli("");
        var cli = new NetworkManagerCli(runner);
        const string ssid = "Location WiFi; rm -rf / $(reboot)";
        const string password = "p'a\"ss w$(x)rd";
        Assert.True(await cli.ConnectAsync("wlan0", ssid, password, default));

        var calls = runner.Calls.ToArray();
        // The box's own profile for this SSID is replaced, never another tool's.
        Assert.Equal(["connection", "delete", "id", "nubarca-wifi-" + ssid], calls[0].Args);
        Assert.Equal(
            ["--wait", "45", "device", "wifi", "connect", ssid, "password", password,
             "ifname", "wlan0", "name", "nubarca-wifi-" + ssid],
            calls[1].Args);
        Assert.All(calls, c => Assert.Equal("nmcli", c.File));
    }

    [Fact]
    public async Task An_Open_Network_Is_Joined_Without_A_Password_Argument()
    {
        var runner = Nmcli("");
        await new NetworkManagerCli(runner).ConnectAsync("wlan0", "Cafe", null, default);
        Assert.DoesNotContain("password", runner.Calls.Last().Args);
    }

    [Fact]
    public async Task The_Setup_Network_Is_A_Shared_Access_Point_Without_Autoconnect()
    {
        var runner = Nmcli("");
        Assert.True(await new NetworkManagerCli(runner).StartAccessPointAsync("wlan0", "NubArca-Print-A7F3", "setup-pass", default));
        var calls = runner.Calls.Select(c => c.Args).ToArray();
        Assert.Equal(["connection", "delete", "id", "nubarca-setup"], calls[0]);
        var add = calls[1];
        Assert.Equal(["connection", "add", "type", "wifi", "ifname", "wlan0", "con-name", "nubarca-setup",
            "autoconnect", "no", "ssid", "NubArca-Print-A7F3"], add.Take(12));
        Assert.Contains("ap", add);
        Assert.Contains("shared", add);
        Assert.Equal("setup-pass", add[add.ToList().IndexOf("wifi-sec.psk") + 1]);
        Assert.Equal(["--wait", "30", "connection", "up", "id", "nubarca-setup"], calls[2]);

        var open = Nmcli("");
        await new NetworkManagerCli(open).StartAccessPointAsync("wlan0", "NubArca-Print-A7F3", null, default);
        Assert.DoesNotContain(open.Calls.ToArray()[1].Args, a => a.StartsWith("wifi-sec", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Box_Suffix_Is_Stable_And_Reveals_Nothing()
    {
        const string machineId = "5f1c0e2d9a8b4c7d8e9f0a1b2c3d4e5f";
        var suffix = BoxIdentity.Suffix(machineId);
        Assert.Matches("^[0-9A-F]{4}$", suffix);
        Assert.Equal(suffix, BoxIdentity.Suffix(machineId + "\n"));
        Assert.NotEqual(suffix, BoxIdentity.Suffix("0" + machineId[1..]));
        Assert.DoesNotContain(suffix.ToLowerInvariant(), machineId[^4..]);
    }
}

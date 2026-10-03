using System.Text;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Networking;

/// <summary>
/// NetworkManager through <c>nmcli</c>, in terse field mode (<c>-t -f</c>), never
/// its human-readable tables.
///
/// Every value — an SSID, a password — is its own argv entry through
/// <see cref="IProcessRunner"/>: nothing a phone sends is ever part of a command
/// line a shell could read. One honest limit: while nmcli runs, its arguments
/// are visible in the process table to other local accounts. On a single-purpose
/// box that has none, that is the price of using the standard tool; nothing here
/// ever writes a password to a log or a file.
/// </summary>
public sealed class NetworkManagerCli : INetworkManager
{
    public const string SetupConnectionName = "nubarca-setup";
    public const string ClientConnectionPrefix = "nubarca-wifi-";

    private const string Nmcli = "nmcli";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private const int ActivationWaitSeconds = 45;
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(ActivationWaitSeconds + 15);

    private readonly IProcessRunner _runner;

    public NetworkManagerCli(IProcessRunner runner) => _runner = runner;

    public async Task<NetworkSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var general = await RunAsync(["-t", "-f", "RUNNING", "general"], QueryTimeout, cancellationToken);
        if (!general.Succeeded || !general.StdOut.Trim().Equals("running", StringComparison.Ordinal))
            return NetworkSnapshot.Unavailable;

        var devices = await RunAsync(["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
            QueryTimeout, cancellationToken);
        if (!devices.Succeeded) return NetworkSnapshot.Unavailable;

        string? wifi = null;
        NetworkUplink? uplink = null;
        var apActive = false;
        string? apAddress = null;
        foreach (var fields in NmcliTerse.Lines(devices.StdOut))
        {
            if (fields.Count < 4) continue;
            var (device, type, state, connection) = (fields[0], fields[1], fields[2], fields[3]);
            if (type == "wifi") wifi ??= device;
            if (!state.StartsWith("connected", StringComparison.Ordinal)) continue;

            if (connection == SetupConnectionName)
            {
                apActive = true;
                apAddress = (await AddressingAsync(device, cancellationToken)).Address;
                continue;
            }
            if (uplink is not null || type is not ("ethernet" or "wifi")) continue;
            var addressing = await AddressingAsync(device, cancellationToken);
            if (addressing.Address is not null && addressing.HasDefaultRoute)
            {
                var name = connection.StartsWith(ClientConnectionPrefix, StringComparison.Ordinal)
                    ? connection[ClientConnectionPrefix.Length..]
                    : connection;
                uplink = new NetworkUplink(device, type, string.IsNullOrEmpty(name) ? null : name,
                    addressing.Address);
            }
        }
        return new NetworkSnapshot(true, wifi, uplink, apActive, apAddress);
    }

    public async Task<WifiClientState> GetWifiClientStateAsync(string wifiInterface,
        CancellationToken cancellationToken)
    {
        var show = await RunAsync(
            ["-t", "-f", "GENERAL.STATE,GENERAL.CONNECTION,IP4.ADDRESS,IP4.GATEWAY", "device", "show", wifiInterface],
            QueryTimeout, cancellationToken);
        return show.Succeeded ? NmcliTerse.WifiClient(show.StdOut) : WifiClientState.Disconnected;
    }

    public async Task<IReadOnlyList<WifiNetwork>> ScanAsync(string wifiInterface, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["-t", "-f", "SSID,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", wifiInterface, "--rescan", "yes"],
            QueryTimeout, cancellationToken);
        return result.Succeeded ? NmcliTerse.Networks(result.StdOut) : [];
    }

    public async Task<bool> StartAccessPointAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken)
    {
        // Re-created every time, so a profile edited by hand or left half-made by
        // a power cut cannot change what the setup network is. autoconnect is off:
        // this service, not NetworkManager, decides when the box is in setup mode.
        await RunAsync(["connection", "delete", "id", SetupConnectionName], QueryTimeout, cancellationToken);
        var add = new List<string>
        {
            "connection", "add", "type", "wifi", "ifname", wifiInterface,
            "con-name", SetupConnectionName, "autoconnect", "no", "ssid", ssid,
            "802-11-wireless.mode", "ap", "802-11-wireless.band", "bg",
            "ipv4.method", "shared", "ipv6.method", "ignore",
        };
        if (!string.IsNullOrEmpty(password))
        {
            add.AddRange([
                "wifi-sec.key-mgmt", "wpa-psk", "wifi-sec.psk", password,
                "wifi-sec.proto", "rsn", "wifi-sec.pairwise", "ccmp", "wifi-sec.group", "ccmp",
            ]);
        }
        if (!(await RunAsync(add, QueryTimeout, cancellationToken)).Succeeded) return false;
        var up = await RunAsync(["--wait", "30", "connection", "up", "id", SetupConnectionName],
            ActivationTimeout, cancellationToken);
        return up.Succeeded;
    }

    public async Task StopAccessPointAsync(CancellationToken cancellationToken) =>
        await RunAsync(["connection", "down", "id", SetupConnectionName], QueryTimeout, cancellationToken);

    public async Task<bool> ConnectAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken)
    {
        // One profile per SSID, owned by this box: replacing it IS the update,
        // and nothing another tool created is ever touched.
        await ForgetAsync(ssid, cancellationToken);
        var arguments = new List<string>
        {
            "--wait", ActivationWaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "device", "wifi", "connect", ssid,
        };
        if (!string.IsNullOrEmpty(password)) arguments.AddRange(["password", password]);
        arguments.AddRange(["ifname", wifiInterface, "name", ClientConnectionPrefix + ssid]);
        return (await RunAsync(arguments, ActivationTimeout, cancellationToken)).Succeeded;
    }

    public async Task ForgetAsync(string ssid, CancellationToken cancellationToken) =>
        await RunAsync(["connection", "delete", "id", ClientConnectionPrefix + ssid], QueryTimeout, cancellationToken);

    private async Task<(string? Address, bool HasDefaultRoute)> AddressingAsync(string device,
        CancellationToken cancellationToken)
    {
        var show = await RunAsync(["-t", "-f", "IP4.ADDRESS,IP4.GATEWAY,IP6.GATEWAY", "device", "show", device],
            QueryTimeout, cancellationToken);
        return show.Succeeded ? NmcliTerse.Addressing(show.StdOut) : (null, false);
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _runner.RunAsync(Nmcli, arguments, timeout, cancellationToken);
}

/// <summary>Reads nmcli's terse output: ':' separates fields, '\' escapes ':' and '\' inside one.</summary>
public static class NmcliTerse
{
    public static IEnumerable<IReadOnlyList<string>> Lines(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0) yield return Split(line);
        }
    }

    public static IReadOnlyList<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length) { current.Append(line[++i]); continue; }
            if (c == ':') { fields.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Visible networks, hidden ones dropped, one entry per SSID at its best signal.</summary>
    public static IReadOnlyList<WifiNetwork> Networks(string output) =>
        Lines(output)
            .Where(f => f.Count >= 3 && f[0].Length > 0)
            .Select(f => new WifiNetwork(
                f[0],
                int.TryParse(f[1], out var signal) ? Math.Clamp(signal, 0, 100) : 0,
                f[2].Length > 0 && f[2] != "--"))
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(n => n.Signal).First())
            .OrderByDescending(n => n.Signal)
            .ThenBy(n => n.Ssid, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One device as a Wi-Fi client, from <c>device show</c>: connected only in
    /// NetworkManager's state 100, under the profile it names, with the first
    /// IPv4 address and whether an IPv4 gateway came with it.
    /// </summary>
    public static WifiClientState WifiClient(string output)
    {
        var connected = false;
        string? connection = null;
        string? address = null;
        var gateway = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var value = Unescape(line[(colon + 1)..].Trim());
            if (value is "" or "--") continue;
            if (key == "GENERAL.STATE")
                connected = value.StartsWith("100", StringComparison.Ordinal);
            else if (key == "GENERAL.CONNECTION")
                connection = value;
            else if (key.StartsWith("IP4.ADDRESS", StringComparison.Ordinal))
                address ??= value.Split('/')[0];
            else if (key == "IP4.GATEWAY")
                gateway = true;
        }
        return new WifiClientState(connected, connection, address, gateway);
    }

    /// <summary>A terse value with its backslash escapes undone: <c>\:</c> is ':' and <c>\\</c> is one backslash.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var plain = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) { plain.Append(value[++i]); continue; }
            plain.Append(value[i]);
        }
        return plain.ToString();
    }

    /// <summary>The first IPv4 address (without prefix length), and whether any default gateway exists.</summary>
    public static (string? Address, bool HasDefaultRoute) Addressing(string output)
    {
        string? address = null;
        var route = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (value is "" or "--") continue;
            if (key.StartsWith("IP4.ADDRESS", StringComparison.Ordinal))
                address ??= value.Split('/')[0];
            else if (key is "IP4.GATEWAY" or "IP6.GATEWAY")
                route = true;
        }
        return (address, route);
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Tests;

/// <summary>Answers commands from a script and remembers exactly which argv it was handed.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _respond;
    public ConcurrentQueue<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

    public FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) => _respond = respond;

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Enqueue((fileName, arguments.ToArray()));
        return Task.FromResult(_respond(fileName, arguments));
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty);
    public static ProcessResult Fail(string stderr, int exitCode = 1) => new(exitCode, string.Empty, stderr);
}

/// <summary>Every formatted log line, so a test can prove what was never written.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }
    public string All => string.Join('\n', Lines);

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    public static (ILoggerFactory Factory, CapturingLoggerProvider Provider) Create()
    {
        var provider = new CapturingLoggerProvider();
        return (LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace)), provider);
    }
}

/// <summary>A NetworkManager whose world the test decides.</summary>
public sealed class FakeNetworkManager : INetworkManager
{
    private readonly object _lock = new();
    public bool Available { get; set; } = true;
    public string? WifiInterface { get; set; } = "wlan0";
    public NetworkUplink? Uplink { get; set; }
    public bool AccessPointActive { get; private set; }
    public IReadOnlyList<WifiNetwork> Visible { get; set; } =
        [new("Location-WiFi", 82, true), new("Studio", 67, true), new("Phone Hotspot", 55, false)];

    /// <summary>Decides whether joining (ssid, password) works.</summary>
    public Func<string, string?, bool> Joins { get; set; } = (_, _) => true;

    /// <summary>False: nmcli reports the activation, but no address and route ever follow.</summary>
    public bool AddressAfterJoin { get; set; } = true;

    /// <summary>A known network NetworkManager reconnects to by itself once the radio is free.</summary>
    public NetworkUplink? KnownNetwork { get; set; }

    /// <summary>The Wi-Fi interface as a client, on its own — what an attempt is verified against.</summary>
    public WifiClientState WifiClient { get; set; } = WifiClientState.Disconnected;

    /// <summary>
    /// Overrides what the Wi-Fi interface reports after a join nmcli called
    /// successful: another profile, no gateway, nothing at all.
    /// </summary>
    public Func<string, WifiClientState>? WifiAfterJoin { get; set; }

    public int AccessPointStarts;
    public int AccessPointStops;
    public int Scans;
    public int Connects;
    public int Forgets;
    public string? LastAccessPointSsid;
    public string? LastAccessPointPassword;

    public Task<NetworkSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Available
                ? new NetworkSnapshot(true, WifiInterface, Uplink, AccessPointActive,
                    AccessPointActive ? "10.42.0.1" : null)
                : NetworkSnapshot.Unavailable);
        }
    }

    public Task<WifiClientState> GetWifiClientStateAsync(string wifiInterface, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(wifiInterface == WifiInterface ? WifiClient : WifiClientState.Disconnected);
    }

    public Task<IReadOnlyList<WifiNetwork>> ScanAsync(string wifiInterface, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Scans);
        lock (_lock) return Task.FromResult(AccessPointActive ? (IReadOnlyList<WifiNetwork>)[] : Visible);
    }

    public Task<bool> StartAccessPointAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            AccessPointStarts++;
            AccessPointActive = true;
            LastAccessPointSsid = ssid;
            LastAccessPointPassword = password;
            if (Uplink?.Type == "wifi") Uplink = null; // one radio
            WifiClient = WifiClientState.Disconnected;
        }
        return Task.FromResult(true);
    }

    public Task StopAccessPointAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            AccessPointStops++;
            AccessPointActive = false;
            if (KnownNetwork is not null) Uplink = KnownNetwork;
        }
        return Task.CompletedTask;
    }

    public Task<bool> ConnectAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Connects);
        var ok = Joins(ssid, password);
        lock (_lock)
        {
            if (ok && WifiAfterJoin is not null)
            {
                WifiClient = WifiAfterJoin(ssid);
            }
            else if (ok && AddressAfterJoin)
            {
                WifiClient = new WifiClientState(true, NetworkManagerCli.ClientConnectionPrefix + ssid, "192.0.2.20", true);
                // Ethernet stays the uplink it was; Wi-Fi becomes it only when nothing else is.
                if (Uplink is null || Uplink.Type == "wifi")
                    Uplink = new NetworkUplink(wifiInterface, "wifi", ssid, "192.0.2.20");
            }
        }
        return Task.FromResult(ok);
    }

    public Task ForgetAsync(string ssid, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Forgets);
        lock (_lock) LastForgotten = ssid;
        return Task.CompletedTask;
    }

    public string? LastForgotten;
}

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }
}

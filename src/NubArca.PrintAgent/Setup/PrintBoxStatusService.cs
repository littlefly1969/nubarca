using System.Text.Json.Serialization;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent.Setup;

/// <summary>The Print Box, summarised for a person holding a phone. No secret is ever part of it.</summary>
public sealed record PrintBoxStatus(
    string NetworkManager,
    string Network,
    string? ConnectionType,
    string? Connection,
    string? Address,
    string SetupNetwork,
    string Cups,
    PrintBoxPrinter? Printer,
    [property: JsonPropertyName("nubarca")] string NubArca,
    DateTimeOffset? LastServerContact,
    PrintBoxAttempt? LastAttempt);

/// <summary>
/// The box's printer. <see cref="RemainingPrints"/> is the physical media count
/// the printer reports, or null when it reports none — never a guess.
/// </summary>
public sealed record PrintBoxPrinter(string Name, string State, string? Detail, int? RemainingPrints = null);

public sealed record PrintBoxAttempt(string Ssid, string Outcome, DateTimeOffset At);

/// <summary>
/// One place that knows how the box is doing. The endpoints ask this; this asks
/// the provisioning service, the printer adapter and the worker's view of the
/// server — it runs no command of its own.
/// </summary>
public sealed class PrintBoxStatusService
{
    /// <summary>A few idle heartbeats; older than this, the server counts as unreachable.</summary>
    private static readonly TimeSpan ServerWindow = TimeSpan.FromSeconds(60);

    private readonly NetworkProvisioningService _network;
    private readonly IPrinterAdapter _adapter;
    private readonly AgentConnectionState _connection;
    private readonly PrintAgentOptions _options;

    public PrintBoxStatusService(NetworkProvisioningService network, IPrinterAdapter adapter,
        AgentConnectionState connection, PrintAgentOptions options)
    {
        _network = network; _adapter = adapter; _connection = connection; _options = options;
    }

    public async Task<PrintBoxStatus> GetAsync(CancellationToken cancellationToken)
    {
        var state = _network.State;
        var cups = _adapter is IPrintSystemHealth health
            ? await health.IsAvailableAsync(cancellationToken) ? "ready" : "unavailable"
            : "not-used";

        PrintBoxPrinter? printer = null;
        var printers = await _adapter.DiscoverAsync(cancellationToken);
        var chosen = _options.PrinterName is { Length: > 0 } name
            ? printers.FirstOrDefault(p => string.Equals(p.DeviceKey, name, StringComparison.OrdinalIgnoreCase))
            : printers.FirstOrDefault();
        if (chosen is not null)
        {
            var observed = await _adapter.GetStatusAsync(chosen, cancellationToken);
            // The same cached reading the heartbeat sends, so the page and the
            // server agree; a failed query is no count, never a failed page.
            PrinterMediaStatus media;
            try { media = await _adapter.GetMediaStatusAsync(chosen, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { media = PrinterMediaStatus.Unavailable; }
            // An offline printer's last count is not a current one.
            var remaining = observed.State == "offline" ? null : media.RemainingPrints;
            printer = new PrintBoxPrinter(chosen.DisplayName, DisplayState(observed.State), observed.Detail, remaining);
        }
        else if (_options.PrinterName is { Length: > 0 } missing)
        {
            printer = new PrintBoxPrinter(missing, "offline", "Queue missing in CUPS");
        }

        var now = DateTimeOffset.UtcNow;
        return new PrintBoxStatus(
            NetworkManager: state.ManagerAvailable ? "ready" : "unavailable",
            Network: state.Mode switch
            {
                ProvisioningMode.Connected => "connected",
                ProvisioningMode.AccessPoint => "access-point",
                ProvisioningMode.Starting => "starting",
                _ => "disconnected",
            },
            ConnectionType: state.Uplink?.Type,
            Connection: state.Uplink?.Name,
            Address: state.Mode == ProvisioningMode.AccessPoint ? state.AccessPointAddress : state.Uplink?.Address,
            SetupNetwork: state.AccessPointSsid,
            Cups: cups,
            Printer: printer,
            NubArca: _connection.IsConnected(now, ServerWindow) ? "connected" : "disconnected",
            LastServerContact: _connection.LastContact,
            LastAttempt: state.LastAttempt is { } attempt
                ? new PrintBoxAttempt(attempt.Ssid, attempt.Outcome.ToString().ToLowerInvariant(), attempt.At)
                : null);
    }

    /// <summary>The server's word for a printing queue is "busy"; a person reads "printing".</summary>
    private static string DisplayState(string state) => state == "busy" ? "printing" : state;
}

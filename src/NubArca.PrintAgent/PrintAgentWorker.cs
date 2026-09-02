using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Journal;
using NubArca.PrintAgent.Security;

namespace NubArca.PrintAgent;

public sealed class PrintAgentWorker : BackgroundService
{
    private readonly PrintAgentApiClient _api;
    private readonly ICredentialStore _credentials;
    private readonly IPrinterAdapter _adapter;
    private readonly ExecutionJournal _journal;
    private readonly AgentExecutionCoordinator _coordinator;
    private readonly PrintAgentOptions _options;
    private readonly ILogger<PrintAgentWorker> _logger;
    private static readonly string Version = typeof(PrintAgentWorker).Assembly.GetName().Version?.ToString() ?? "unknown";

    public PrintAgentWorker(PrintAgentApiClient api, ICredentialStore credentials,
        IPrinterAdapter adapter, ExecutionJournal journal, AgentExecutionCoordinator coordinator,
        PrintAgentOptions options, ILogger<PrintAgentWorker> logger)
    {
        _api = api; _credentials = credentials; _adapter = adapter; _journal = journal;
        _coordinator = coordinator; _options = options; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _journal.InitializeAsync(stoppingToken);
        var credential = await _credentials.LoadAsync(stoppingToken);
        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException("Print Agent is not enrolled. Run the enroll command first.");
        _api.SetCredential(credential);
        await _coordinator.RecoverAsync(stoppingToken);

        var backoff = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reports = await ObservePrintersAsync(stoppingToken);
                var heartbeat = await _api.HeartbeatAsync(Version, reports, stoppingToken);
                if (heartbeat.DesiredState == "running")
                {
                    var claim = await _api.ClaimAsync(_adapter.Kind, stoppingToken);
                    if (claim is not null)
                    {
                        await _coordinator.ExecuteAsync(claim, stoppingToken);
                        backoff = 1;
                        continue;
                    }
                }
                backoff = 1;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IdlePollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Print Agent cycle failed ({ExceptionType}); reconnecting.", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(backoff), stoppingToken);
                backoff = Math.Min(Math.Max(2, backoff * 2), Math.Max(2, _options.MaxBackoffSeconds));
            }
        }
    }

    private async Task<IReadOnlyList<AgentDeviceReport>> ObservePrintersAsync(CancellationToken ct)
    {
        var result = new List<AgentDeviceReport>();
        foreach (var printer in await _adapter.DiscoverAsync(ct))
        {
            var capabilities = await _adapter.GetCapabilitiesAsync(printer, ct);
            var status = await _adapter.GetStatusAsync(printer, ct);
            result.Add(new(printer.DeviceKey, printer.DisplayName, printer.Manufacturer,
                printer.Model, printer.AdapterKind, capabilities, status.State));
        }
        return result;
    }
}

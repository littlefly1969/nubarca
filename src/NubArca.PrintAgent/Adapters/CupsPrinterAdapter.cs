using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Adapters;

/// <summary>Whether the local print system itself is up, separately from any printer.</summary>
public interface IPrintSystemHealth
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Prints through the local CUPS server with its own command-line tools.
///
/// NubArca never speaks USB or a vendor protocol: CUPS and its driver
/// (Gutenprint for the DNP DS-RX1/RX1HS) own the printer. This adapter only
/// chooses the queue — the photo queue for <c>10x15</c>, the strip queue, set
/// up in CUPS with the driver's 2-inch cut, for <c>2x6x2</c> — exactly the model
/// the Windows spooler adapter uses, through the same
/// <see cref="SpoolerQueueRouting"/>.
///
/// Capabilities are deliberately conservative: a configured queue that CUPS
/// lists is a 10x15 printer, and 2x6x2 is advertised only while the strip
/// queue is listed too. No IPP capability parsing, no option names of any one
/// driver: the queues carry their settings.
/// </summary>
public sealed class CupsPrinterAdapter : IPrinterAdapter, IPrintSystemHealth
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>One heartbeat asks three questions; they share one lpstat.</summary>
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(2);

    private readonly string? _configuredPrinter;
    private readonly string? _stripPrinter;
    private readonly IProcessRunner _runner;
    private readonly ILogger<CupsPrinterAdapter> _logger;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private IReadOnlyDictionary<string, CupsQueue>? _snapshot;
    private long _snapshotAt;
    private bool? _cupsAvailable;
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private const string MissingMarker = "missing:";

    public CupsPrinterAdapter(string? configuredPrinter, string? stripPrinter,
        IProcessRunner runner, ILogger<CupsPrinterAdapter> logger)
    {
        _configuredPrinter = string.IsNullOrWhiteSpace(configuredPrinter) ? null : configuredPrinter;
        _stripPrinter = string.IsNullOrWhiteSpace(stripPrinter) ? null : stripPrinter;
        _runner = runner;
        _logger = logger;
    }

    public string Kind => PrintAdapterKinds.Cups;

    public async Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var queues = await QueuesAsync(cancellationToken);
        if (queues is null) return [];

        IEnumerable<string> names = queues.Keys;
        if (_configuredPrinter is not null)
        {
            if (!queues.ContainsKey(_configuredPrinter))
            {
                // Logged when it goes missing, not on every heartbeat it stays so.
                if (_announced.Add(MissingMarker + _configuredPrinter))
                    _logger.LogWarning("Printer queue missing: {Queue} is not configured in CUPS.", _configuredPrinter);
                _announced.Remove(_configuredPrinter);
                return [];
            }
            _announced.Remove(MissingMarker + _configuredPrinter);
            names = [queues[_configuredPrinter].Name];
        }
        else if (_stripPrinter is not null)
        {
            names = names.Where(n => !string.Equals(n, _stripPrinter, StringComparison.OrdinalIgnoreCase));
        }

        var printers = new List<DiscoveredPrinter>();
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (_announced.Add(name)) _logger.LogInformation("Printer discovered: CUPS queue {Queue}.", name);
            printers.Add(new DiscoveredPrinter(name, name, Manufacturer(name), name, Kind));
        }
        return printers;
    }

    public async Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken)
    {
        var queues = await QueuesAsync(cancellationToken);
        var present = queues?.ContainsKey(printer.DeviceKey) == true;
        var stripReady = _stripPrinter is not null && queues?.ContainsKey(_stripPrinter) == true;
        var formats = SpoolerQueueRouting.Formats(printer.DeviceKey, present,
            _configuredPrinter, _stripPrinter, stripReady);
        return new PrinterCapabilities(formats, Color: true);
    }

    public async Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken)
    {
        var queues = await QueuesAsync(cancellationToken);
        if (queues is null) return new PrinterObservedStatus("offline", "CUPS unavailable");
        return queues.TryGetValue(printer.DeviceKey, out var queue)
            ? CupsOutput.StatusOf(queue)
            : new PrinterObservedStatus("offline", "Queue missing in CUPS");
    }

    public async Task<PrintSubmissionResult> SubmitAsync(PrintSubmission submission,
        CancellationToken cancellationToken)
    {
        var queue = SpoolerQueueRouting.TargetQueue(submission.Format, submission.DeviceKey,
            _configuredPrinter, _stripPrinter);
        if (queue is null) return new PrintSubmissionResult(false, null, "format_unsupported");
        if (!File.Exists(submission.ArtifactPath))
            return new PrintSubmissionResult(false, null, "artifact_missing");

        var title = $"NubArca-{submission.JobId.ToString("N")[..8]}";
        // fit-to-page: the artifact is exactly the sheet's shape, so fitting it
        // fills the page; without it CUPS would print the JPEG at an assumed
        // pixel density and the photograph would come out the wrong size.
        var result = await _runner.RunAsync("lp",
            ["-d", queue, "-t", title, "-o", "fit-to-page", submission.ArtifactPath],
            SubmitTimeout, cancellationToken);
        Invalidate();
        if (result.Succeeded)
        {
            var reference = CupsOutput.ParseRequestId(result.StdOut);
            _logger.LogInformation("Print submitted: {Title} to CUPS queue {Queue} as {Reference}.",
                title, queue, reference ?? "(no request id)");
            return new PrintSubmissionResult(true, reference, null);
        }

        var code = CupsOutput.ClassifySubmitFailure(result.ExitCode, result.StdErr);
        _logger.LogWarning("Print submission failed: {Title} to CUPS queue {Queue} ({Code}).", title, queue, code);
        return new PrintSubmissionResult(false, null, code);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("lpstat", ["-r"], QueryTimeout, cancellationToken);
        return result.Succeeded && CupsOutput.SchedulerRunning(result.StdOut);
    }

    /// <summary>Every queue CUPS lists, or null when CUPS cannot be asked.</summary>
    private async Task<IReadOnlyDictionary<string, CupsQueue>?> QueuesAsync(CancellationToken cancellationToken)
    {
        await _snapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot is not null
                && Environment.TickCount64 - _snapshotAt < SnapshotLifetime.TotalMilliseconds)
                return _snapshot;

            var result = await _runner.RunAsync("lpstat", ["-l", "-p"], QueryTimeout, cancellationToken);
            // lpstat exits non-zero when no queue exists at all; that is an empty
            // CUPS, not an absent one. Only a scheduler that cannot be reached is.
            var unavailable = result.NotFound || result.TimedOut
                || (!result.Succeeded && CupsOutput.ClassifySubmitFailure(result.ExitCode, result.StdErr) == "cups_unavailable");
            if (_cupsAvailable != !unavailable)
            {
                _cupsAvailable = !unavailable;
                if (unavailable) _logger.LogWarning("CUPS unavailable: printers are reported offline until it answers.");
                else _logger.LogInformation("CUPS available.");
            }
            _snapshot = unavailable ? null : CupsOutput.ParseQueues(result.StdOut);
            _snapshotAt = Environment.TickCount64;
            return _snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    /// <summary>A submitted job changes what lpstat says; the next question asks again.</summary>
    private void Invalidate() => _snapshot = null;

    private static string? Manufacturer(string name) =>
        name.Contains("DNP", StringComparison.OrdinalIgnoreCase)
        || name.Contains("RX1", StringComparison.OrdinalIgnoreCase) ? "DNP" : null;
}

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
/// queue is listed too. 13x18 and 20x15 are advertised when the queue's driver
/// lists the 5x7 and 6x8 page sizes (<c>lpoptions -l</c>), and only those
/// jobs carry a PageSize: a 10x15 goes out exactly as it always has.
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

    /// <summary>
    /// The media count is asked once per physical printer per this window:
    /// a heartbeat, the local status page and a second heartbeat a few seconds
    /// later share one IPP query, and a submitted job clears it at once.
    /// </summary>
    private static readonly TimeSpan MediaLifetime = TimeSpan.FromSeconds(8);

    /// <summary>The IPP query is bounded twice: ipptool's own I/O timeout, and the process's.</summary>
    private const int IppToolTimeoutSeconds = 5;
    private static readonly TimeSpan MediaQueryTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A queue name that is safe as a path segment of the CUPS URI.</summary>
    private static readonly System.Text.RegularExpressions.Regex SafeQueue =
        new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,126}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly string _mediaQueryPath;
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly Dictionary<string, (long At, PrinterMediaStatus Status)> _media =
        new(StringComparer.OrdinalIgnoreCase);
    private long _mediaGeneration;

    /// <summary>The driver's page sizes change only when a queue is set up again.</summary>
    private static readonly TimeSpan PageSizeLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (long At, IReadOnlyList<string> Papers)> _papers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Gutenprint page size each paper beyond 10x15 is sent as.</summary>
    public static string? PageSizeFor(string format) => format switch
    {
        SpoolerQueueRouting.Photo13x18 => "w360h504",
        SpoolerQueueRouting.Photo20x15 => "w432h576",
        _ => null,
    };

    public CupsPrinterAdapter(string? configuredPrinter, string? stripPrinter,
        IProcessRunner runner, ILogger<CupsPrinterAdapter> logger, string? mediaQueryPath = null)
    {
        _configuredPrinter = string.IsNullOrWhiteSpace(configuredPrinter) ? null : configuredPrinter;
        _stripPrinter = string.IsNullOrWhiteSpace(stripPrinter) ? null : stripPrinter;
        _runner = runner;
        _logger = logger;
        _mediaQueryPath = mediaQueryPath ?? DefaultMediaQueryPath;
    }

    /// <summary>The IPP query shipped with the agent, beside its own binary.</summary>
    public static string DefaultMediaQueryPath =>
        Path.Combine(AppContext.BaseDirectory, "linux", "nubarca-media-status.test");

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
        var papers = present ? await PapersAsync(printer.DeviceKey, cancellationToken) : [];
        var formats = SpoolerQueueRouting.Formats(printer.DeviceKey, papers,
            _configuredPrinter, _stripPrinter, stripReady);
        return new PrinterCapabilities(formats, Color: true);
    }

    /// <summary>
    /// The papers a present queue prints: always 10x15, which is what the
    /// queue was set up for, plus each larger paper whose page size its driver
    /// lists. A driver that cannot be asked is a 10x15 printer, as before.
    /// </summary>
    private async Task<IReadOnlyList<string>> PapersAsync(string queue, CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        lock (_papers)
        {
            if (_papers.TryGetValue(queue, out var cached)
                && now - cached.At < PageSizeLifetime.TotalMilliseconds)
                return cached.Papers;
        }
        var papers = new List<string> { SpoolerQueueRouting.Photo10x15 };
        var options = await _runner.RunAsync("lpoptions", ["-p", queue, "-l"], QueryTimeout, cancellationToken);
        if (options.Succeeded)
        {
            var sizes = CupsQueueSetup.PageSizes(options.StdOut);
            papers.AddRange(SpoolerQueueRouting.Papers
                .Where(p => PageSizeFor(p) is { } size && sizes.Contains(size, StringComparer.Ordinal)));
        }
        lock (_papers) _papers[queue] = (now, papers);
        return papers;
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
        // A larger paper names its page size; a 10x15 keeps the queue's own.
        string[] arguments = PageSizeFor(submission.Format) is { } pageSize
            ? ["-d", queue, "-t", title, "-o", $"PageSize={pageSize}", "-o", "fit-to-page", submission.ArtifactPath]
            : ["-d", queue, "-t", title, "-o", "fit-to-page", submission.ArtifactPath];
        var result = await _runner.RunAsync("lp", arguments, SubmitTimeout, cancellationToken);
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

    /// <summary>
    /// The prints left on the loaded media, as Gutenprint reports them to
    /// CUPS — read with one IPP query per physical printer, through its PHOTO
    /// queue: the strip queue is the same printer and the same media, and is
    /// never asked or added. A ready printer whose driver gives no count is
    /// <see cref="PrinterMediaStatus.Unavailable"/>, never offline because of it.
    /// </summary>
    public async Task<PrinterMediaStatus> GetMediaStatusAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken)
    {
        // The strip queue, if it is ever asked about, is the photo queue's printer.
        var queue = _stripPrinter is not null && _configuredPrinter is not null
            && string.Equals(printer.DeviceKey, _stripPrinter, StringComparison.OrdinalIgnoreCase)
            ? _configuredPrinter
            : printer.DeviceKey;

        await _mediaGate.WaitAsync(cancellationToken);
        try
        {
            long generation;
            lock (_media)
            {
                if (_media.TryGetValue(queue, out var cached)
                    && Environment.TickCount64 - cached.At < MediaLifetime.TotalMilliseconds)
                    return cached.Status;
                generation = _mediaGeneration;
            }

            var status = await QueryMediaAsync(queue, cancellationToken);
            lock (_media)
            {
                // A job submitted while this query ran makes its answer older
                // than the cache it would land in: return it, keep nothing.
                if (generation == _mediaGeneration) _media[queue] = (Environment.TickCount64, status);
            }
            return status;
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private async Task<PrinterMediaStatus> QueryMediaAsync(string queue, CancellationToken cancellationToken)
    {
        if (!SafeQueue.IsMatch(queue))
        {
            Announce("media:unsafe:" + queue, () =>
                _logger.LogWarning("Media count not read: queue {Queue} is not a plain CUPS name.", queue));
            return PrinterMediaStatus.Unavailable;
        }
        if (!File.Exists(_mediaQueryPath))
        {
            Announce("media:query-missing", () =>
                _logger.LogWarning("Media count not read: the agent's IPP query file is missing."));
            return PrinterMediaStatus.Unavailable;
        }

        var result = await _runner.RunAsync("ipptool",
            ["-T", IppToolTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-X",
             $"ipp://localhost/printers/{queue}", _mediaQueryPath],
            MediaQueryTimeout, cancellationToken);
        if (result.NotFound)
        {
            Announce("media:ipptool-missing", () =>
                _logger.LogWarning("Media count not read: ipptool is not installed (package cups-ipp-utils)."));
            return PrinterMediaStatus.Unavailable;
        }
        // ipptool exits non-zero for a failed test; the plist still says why,
        // and a failed query is simply no count.
        var status = CupsMarkers.StatusOf(result.Succeeded ? CupsMarkers.ParsePlist(result.StdOut) : null);
        var key = "media:" + queue;
        if (status.RemainingPrints is int remaining)
        {
            lock (_announced) _announced.Remove(key + ":none");
            if (_lastRemaining.TryGetValue(queue, out var last) && last == remaining) return status;
            _lastRemaining[queue] = remaining;
            _logger.LogInformation(
                "print.media.remaining.observed queue={Queue} remainingPrints={RemainingPrints}", queue, remaining);
        }
        else
        {
            _lastRemaining.Remove(queue);
            Announce(key + ":none", () => _logger.LogInformation(
                "print.media.remaining.unavailable queue={Queue}", queue));
        }
        return status;
    }

    private readonly Dictionary<string, int> _lastRemaining = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logs a condition when it starts, not on every heartbeat it lasts.</summary>
    private void Announce(string key, Action log)
    {
        lock (_announced)
        {
            if (!_announced.Add(key)) return;
        }
        log();
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

    /// <summary>
    /// A submitted job changes what lpstat says, and soon what the media holds;
    /// the next question asks again. The count is NOT decremented here: the
    /// sheet still has to pass through CUPS, the driver and the printer, and
    /// the number converges when the printer reports it.
    /// </summary>
    private void Invalidate()
    {
        _snapshot = null;
        lock (_media)
        {
            _media.Clear();
            _mediaGeneration++;
        }
    }

    private static string? Manufacturer(string name) =>
        name.Contains("DNP", StringComparison.OrdinalIgnoreCase)
        || name.Contains("RX1", StringComparison.OrdinalIgnoreCase) ? "DNP" : null;
}

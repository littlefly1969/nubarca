using System.Text.RegularExpressions;

namespace NubArca.PrintAgent.Adapters;

/// <summary>One CUPS queue as <c>lpstat -l -p</c> describes it.</summary>
public sealed record CupsQueue(string Name, CupsQueueState State, IReadOnlyList<string> Reasons, string? Message);

public enum CupsQueueState { Idle, Printing, Disabled, Unknown }

/// <summary>
/// Reads what the CUPS command-line tools print, in the C locale the runner
/// forces. Pure, so every parse is tested without a CUPS server.
///
/// State comes from the header words and from the <c>Alerts:</c> line, which
/// carries IPP printer-state-reasons KEYWORDS (<c>offline-report</c>,
/// <c>media-empty-error</c>): those do not change with the language, so the
/// mapping leans on them wherever it can.
/// </summary>
public static partial class CupsOutput
{
    private const int MaxDetailChars = 160;

    [GeneratedRegex(@"^printer (\S+) (is idle|now printing|disabled since)", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"request id is (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex RequestId();

    // Keys of the long listing. Anything indented that is NOT one of these is
    // the printer-state-message, which lpstat prints on its own line.
    private static readonly string[] ListingKeys =
    [
        "Form mounted", "Content types", "Printer types", "Description", "Alerts", "Location",
        "Connection", "Interface", "On fault", "After fault", "Users allowed", "Forms allowed",
        "Banner required", "Charset sets", "Default pitch", "Default page size",
        "Default port settings", "(all)", "(none)",
    ];

    public static IReadOnlyDictionary<string, CupsQueue> ParseQueues(string lpstatOutput)
    {
        var queues = new Dictionary<string, CupsQueue>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        var state = CupsQueueState.Unknown;
        var reasons = new List<string>();
        string? message = null;

        void Flush()
        {
            if (name is not null) queues[name] = new CupsQueue(name, state, reasons.ToArray(), message);
        }

        foreach (var raw in lpstatOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var header = Header().Match(line);
            if (header.Success)
            {
                Flush();
                name = header.Groups[1].Value;
                state = header.Groups[2].Value switch
                {
                    "is idle" => CupsQueueState.Idle,
                    "now printing" => CupsQueueState.Printing,
                    "disabled since" => CupsQueueState.Disabled,
                    _ => CupsQueueState.Unknown,
                };
                reasons = [];
                message = null;
                continue;
            }
            if (name is null || line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;

            var text = line.Trim();
            if (text.StartsWith("Alerts:", StringComparison.Ordinal))
            {
                reasons.AddRange(text["Alerts:".Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(r => r != "none"));
                continue;
            }
            if (message is null && text.Length > 0
                && !ListingKeys.Any(key => text.StartsWith(key, StringComparison.Ordinal)))
            {
                message = text;
            }
        }
        Flush();
        return queues;
    }

    /// <summary>
    /// The NubArca device state for a queue, in the vocabulary the server
    /// accepts: ready, busy, offline, error, unknown.
    /// </summary>
    public static PrinterObservedStatus StatusOf(CupsQueue queue)
    {
        var offline = queue.Reasons.Any(r => r.StartsWith("offline", StringComparison.Ordinal)
            || r.StartsWith("connecting-to-device", StringComparison.Ordinal));
        var error = queue.Reasons.Any(r => r.EndsWith("-error", StringComparison.Ordinal));
        var state = offline ? "offline"
            : error || queue.State == CupsQueueState.Disabled ? "error"
            : queue.State switch
            {
                CupsQueueState.Idle => "ready",
                CupsQueueState.Printing => "busy",
                _ => "unknown",
            };
        return new PrinterObservedStatus(state, Detail(queue));
    }

    public static string? ParseRequestId(string lpOutput)
    {
        var match = RequestId().Match(lpOutput);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>A stable NubArca failure code for a refused <c>lp</c>, never its stderr.</summary>
    public static string ClassifySubmitFailure(int exitCode, string stderr)
    {
        if (exitCode == Execution.ProcessResult.NotFoundExitCode) return "cups_unavailable";
        if (Contains(stderr, "does not exist") || Contains(stderr, "No such destination")
            || Contains(stderr, "Unknown destination"))
            return "printer_not_found";
        if (Contains(stderr, "not accepting")) return "printer_not_accepting";
        if (Contains(stderr, "Unable to connect") || Contains(stderr, "scheduler is not running")
            || Contains(stderr, "Connection refused") || exitCode == Execution.ProcessResult.TimedOutExitCode)
            return "cups_unavailable";
        return "submit_failed";
    }

    public static bool SchedulerRunning(string lpstatROutput) =>
        lpstatROutput.Contains("scheduler is running", StringComparison.Ordinal);

    private static string? Detail(CupsQueue queue)
    {
        var detail = queue.Message ?? (queue.Reasons.Count > 0 ? string.Join(", ", queue.Reasons) : null);
        if (detail is null) return null;
        var clean = new string(detail.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= MaxDetailChars ? clean : clean[..MaxDetailChars];
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

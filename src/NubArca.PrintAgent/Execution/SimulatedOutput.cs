using System.Text.RegularExpressions;

namespace NubArca.PrintAgent.Execution;

/// <summary>
/// What the simulated twins wrote: one directory per twin queue under
/// <see cref="PrintSimulationOptions.OutputPath"/>, one file pair per job,
/// named by the NubArca job it came from (<c>&lt;job id&gt;.prn</c> and
/// <c>.txt</c>). A clear deletes exactly the jobs the server names, in the twin
/// queues of the printer it names — never another printer's, never a job
/// printed after the clear, and never by comparing clocks.
/// </summary>
public static partial class SimulatedOutput
{
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex Queue();

    /// <summary>Deletes the named jobs' files from the named twin queues; anything malformed is skipped.</summary>
    public static int Purge(string root, IEnumerable<string> queues, IEnumerable<string> jobIds, ILogger logger)
    {
        var ids = jobIds.Where(id => JobId().IsMatch(id)).Distinct(StringComparer.Ordinal).ToArray();
        var deleted = 0;
        foreach (var queue in queues.Where(q => Queue().IsMatch(q)).Distinct(StringComparer.Ordinal))
        {
            var directory = Path.Combine(root, queue);
            if (!Directory.Exists(directory)) continue;
            foreach (var id in ids)
            {
                foreach (var extension in new[] { ".prn", ".txt" })
                {
                    var path = Path.Combine(directory, id + extension);
                    try
                    {
                        if (!File.Exists(path)) continue;
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("Simulated output not deleted ({ExceptionType}).", ex.GetType().Name);
                    }
                }
            }
        }
        if (deleted > 0) logger.LogInformation("Simulated output cleared: {Count} files.", deleted);
        return deleted;
    }
}

/// <summary>
/// Applies the server's per-printer clears, each sequence once. Applying one
/// again — after a restart forgets which were applied — deletes nothing more.
/// </summary>
public sealed class SimulatedOutputPurger
{
    private readonly string _root;
    private readonly ILogger _logger;
    private readonly Dictionary<string, long> _applied = new(StringComparer.Ordinal);

    public SimulatedOutputPurger(string root, ILogger logger)
    {
        _root = root;
        _logger = logger;
    }

    public int Apply(IReadOnlyList<Api.AgentSimulatedOutputPurge>? purges, Adapters.IPrinterAdapter adapter)
    {
        var deleted = 0;
        foreach (var purge in purges ?? [])
        {
            if (_applied.TryGetValue(purge.DeviceKey, out var applied) && applied >= purge.Sequence) continue;
            deleted += SimulatedOutput.Purge(_root, adapter.SimulatedQueuesOf(purge.DeviceKey), purge.JobIds, _logger);
            _applied[purge.DeviceKey] = purge.Sequence;
        }
        return deleted;
    }
}

namespace NubArca.PrintAgent.Execution;

/// <summary>
/// What the simulated twins wrote: one directory per twin queue under
/// <see cref="PrintSimulationOptions.OutputPath"/>. Cleared when the printer's
/// owner clears the simulated prints in NubArca.
/// </summary>
public static class SimulatedOutput
{
    /// <summary>Deletes every file under <paramref name="root"/>'s queue directories written before <paramref name="before"/>.</summary>
    public static int PurgeOlderThan(string root, DateTime before, ILogger logger)
    {
        if (!Directory.Exists(root)) return 0;
        var deleted = 0;
        foreach (var queue in Directory.EnumerateDirectories(root))
        {
            foreach (var file in Directory.EnumerateFiles(queue))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= before) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Simulated output not deleted ({ExceptionType}).", ex.GetType().Name);
                }
            }
        }
        if (deleted > 0) logger.LogInformation("Simulated output cleared: {Count} files.", deleted);
        return deleted;
    }
}

namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// Where a job's files are kept between download and print. A joined 20x15
/// (<c>4x6x2</c>, <c>2x6x4</c>) is two 10x15 JPEGs, each downloaded on its own:
/// the first at the job's artifact path, the second beside it. Derived from the
/// path and the format, so the journal keeps one path for every job.
/// </summary>
public static class JobArtifactFiles
{
    public static IReadOnlyList<string> Of(string artifactPath, string format) =>
        SpoolerQueueRouting.LargeCuts.Contains(format) ? [artifactPath, Second(artifactPath)] : [artifactPath];

    private static string Second(string artifactPath) =>
        Path.Combine(Path.GetDirectoryName(artifactPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(artifactPath)}.2{Path.GetExtension(artifactPath)}");
}

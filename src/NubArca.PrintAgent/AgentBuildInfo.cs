namespace NubArca.PrintAgent;

/// <summary>
/// What this agent is: its version, and the source commit it was built from.
/// The commit comes from <c>source-commit.txt</c>, which only the CI release
/// writes beside the executable — a development build has none, and says so
/// by sending no build at all.
/// </summary>
public static class AgentBuildInfo
{
    public static readonly string Version =
        typeof(AgentBuildInfo).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static readonly string? Build = ReadBuild(AppContext.BaseDirectory);

    /// <summary>The 40-hex commit in <paramref name="directory"/>'s source-commit.txt, or null.</summary>
    public static string? ReadBuild(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "source-commit.txt");
            if (!File.Exists(path) || new FileInfo(path).Length > 256) return null;
            var value = File.ReadAllText(path).Trim().ToLowerInvariant();
            return value.Length == 40 && value.All(Uri.IsHexDigit) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The server refused to enroll an agent older than it still serves.</summary>
public sealed class AgentIncompatibleException(string? minimumVersion)
    : Exception($"This Print Agent is too old for the server; install {minimumVersion ?? "a newer version"} or later.")
{
    public string? MinimumVersion { get; } = minimumVersion;
}

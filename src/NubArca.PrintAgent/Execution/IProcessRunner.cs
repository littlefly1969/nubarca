namespace NubArca.PrintAgent.Execution;

/// <summary>What an external command did. Never carries the arguments it was given.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The shell convention for "command not found".</summary>
    public const int NotFoundExitCode = 127;

    /// <summary>The shell convention for "timed out".</summary>
    public const int TimedOutExitCode = 124;

    public bool Succeeded => ExitCode == 0;
    public bool NotFound => ExitCode == NotFoundExitCode;
    public bool TimedOut => ExitCode == TimedOutExitCode;

    public static ProcessResult Missing(string fileName) =>
        new(NotFoundExitCode, string.Empty, $"{fileName}: not found");

    public static ProcessResult Expired(string fileName) =>
        new(TimedOutExitCode, string.Empty, $"{fileName}: timed out");
}

/// <summary>
/// The ONE place the agent starts another program.
///
/// Every argument travels as its own argv entry — never through a shell, never
/// concatenated into a command line — so an SSID, a password or a queue name is
/// data to the program, whatever characters it contains. Implementations must
/// never log the arguments: a Wi-Fi password is one of them.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken);
}

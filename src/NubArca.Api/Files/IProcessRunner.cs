namespace NubArca.Api.Files;

/// <summary>
/// Abstraction for running an external process. Exists to make FFmpeg
/// invocation unit-testable without requiring a real binary.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessRunRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds,
    int MaxStdoutBytes);

// `OutputTruncated` distinguishes "the process wrote more than MaxStdoutBytes"
// (StdoutBytes is empty AND unusable) from "the process wrote nothing", which
// are legitimately different outcomes for a caller that parses stdout. Optional
// with a false default so existing callers and test fakes are unaffected.
public sealed record ProcessRunResult(
    int ExitCode,
    byte[] StdoutBytes,
    bool TimedOut,
    bool OutputTruncated = false);

/// <summary>
/// Abstraction for running an external process whose OUTPUT is a set of files
/// written into a working directory rather than stdout (video-hls slice 1:
/// ffmpeg's HLS muxer writes playlists + many segments). Kept separate from
/// <see cref="IProcessRunner"/> so existing stdout-capturing callers and their
/// test fakes are untouched. stdout/stderr are discarded, never captured or
/// logged (they may contain paths or other invocation details); callers see
/// only the exit code and the timed-out flag, and validate the produced files
/// themselves.
/// </summary>
public interface IDirectoryProcessRunner
{
    Task<ProcessDirectoryRunResult> RunAsync(
        ProcessDirectoryRunRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessDirectoryRunRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int TimeoutSeconds);

public sealed record ProcessDirectoryRunResult(
    int ExitCode,
    bool TimedOut);

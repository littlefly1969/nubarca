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

// `OutputTruncated` is OUTPUT LIMIT EXCEEDED: the process wrote more than
// MaxStdoutBytes and was killed at once (process tree included) — StdoutBytes
// is empty, never a partial prefix, and ExitCode is -1 with TimedOut false, so
// it is told apart from a timeout, from a failing exit code and from "the
// process wrote nothing". Optional with a false default so existing callers and
// test fakes are unaffected.
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

/// <summary>
/// Runs a process whose stdout is ONE large output — a decoded frame — and
/// streams it to a file, never holding it in memory. The byte cap is enforced
/// WHILE the output arrives: one byte past MaxOutputBytes and the process tree
/// is killed at once and the file deleted, so neither memory nor disk can grow
/// past the cap. A timeout or a cancellation also kills the tree and deletes
/// the file; only a normal exit within the cap leaves it in place.
/// </summary>
public interface IProcessFileRunner
{
    Task<ProcessFileRunResult> RunAsync(ProcessFileRunRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessFileRunRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string OutputPath,
    long MaxOutputBytes,
    int TimeoutSeconds);

/// <summary>
/// ExitCode is -1 when the process was stopped (TimedOut or
/// OutputLimitExceeded); in both cases, and on a non-zero exit, the output
/// file is not to be used — after a stop it no longer exists.
/// </summary>
public sealed record ProcessFileRunResult(
    int ExitCode,
    bool TimedOut,
    bool OutputLimitExceeded,
    long BytesWritten);

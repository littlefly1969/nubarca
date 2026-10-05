using System.Diagnostics;
using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

/// <summary>
/// The real process runner, on real processes: an output cap that stops a
/// writer at once instead of waiting for the timeout, and a cancellation that
/// leaves no process behind.
/// </summary>
public sealed class SystemProcessRunnerTests : IDisposable
{
    private const int Limit = 1000;
    private readonly string _dir = Directory.CreateTempSubdirectory("nubarca-runner-").FullName;
    private readonly SystemProcessRunner _runner = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ProcessRunRequest Shell(string script, int maxStdoutBytes = Limit, int timeoutSeconds = 30) =>
        new("/bin/sh", ["-c", script], timeoutSeconds, maxStdoutBytes);

    [SkippableFact]
    public async Task Output_Under_The_Limit_Is_Returned_Whole()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var result = await _runner.RunAsync(Shell("head -c 999 /dev/zero"), default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(999, result.StdoutBytes.Length);
        Assert.False(result.OutputTruncated);
        Assert.False(result.TimedOut);
    }

    [SkippableFact]
    public async Task Output_Exactly_At_The_Limit_Is_Still_Whole()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var result = await _runner.RunAsync(Shell($"head -c {Limit} /dev/zero"), default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Limit, result.StdoutBytes.Length);
        Assert.False(result.OutputTruncated);
    }

    [SkippableFact]
    public async Task One_Byte_Over_The_Limit_Is_Refused_And_Never_Returned_Partially()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var result = await _runner.RunAsync(Shell($"head -c {Limit + 1} /dev/zero"), default);

        Assert.True(result.OutputTruncated);
        Assert.Empty(result.StdoutBytes);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [SkippableFact]
    public async Task A_Process_That_Never_Stops_Writing_Is_Killed_At_Once_With_Its_Tree()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "writer.pid");
        var clock = Stopwatch.StartNew();

        // The writer is a CHILD of the shell, so killing only the shell would
        // leave it running.
        var result = await _runner.RunAsync(
            Shell($"yes & echo $! > '{pidFile}'; wait", maxStdoutBytes: 64 * 1024, timeoutSeconds: 60), default);

        Assert.True(result.OutputTruncated);
        Assert.False(result.TimedOut);
        Assert.Empty(result.StdoutBytes);
        // Stopped by the cap, nowhere near the 60 s timeout.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        await AssertGoneAsync(await ReadPidAsync(pidFile));
    }

    [SkippableFact]
    public async Task An_Outer_Cancellation_Kills_The_Process_Tree_Before_Propagating()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "sleeper.pid");
        using var cancel = new CancellationTokenSource();

        var run = _runner.RunAsync(Shell($"sleep 60 & echo $! > '{pidFile}'; wait", timeoutSeconds: 120), cancel.Token);
        var pid = await ReadPidAsync(pidFile);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AssertGoneAsync(pid);
    }

    [SkippableFact]
    public async Task The_Directory_Runner_Kills_Its_Tree_On_An_Outer_Cancellation_Too()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "dir-sleeper.pid");
        using var cancel = new CancellationTokenSource();

        var run = _runner.RunAsync(
            new ProcessDirectoryRunRequest("/bin/sh", ["-c", $"sleep 60 & echo $! > '{pidFile}'; wait"], _dir, 120),
            cancel.Token);
        var pid = await ReadPidAsync(pidFile);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AssertGoneAsync(pid);
    }

    [SkippableFact]
    public async Task A_Timeout_Is_Told_Apart_From_An_Exceeded_Limit()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var result = await _runner.RunAsync(Shell("sleep 30", timeoutSeconds: 1), default);

        Assert.True(result.TimedOut);
        Assert.False(result.OutputTruncated);
        Assert.Empty(result.StdoutBytes);
    }

    // --- stdout streamed to a file, capped as it arrives ----------------------

    private ProcessFileRunRequest ToFile(string script, long maxBytes = Limit, int timeoutSeconds = 30) =>
        new("/bin/sh", ["-c", script], Path.Combine(_dir, $"out-{Guid.NewGuid():N}"), maxBytes, timeoutSeconds);

    [SkippableTheory]
    [InlineData(999)]
    [InlineData(Limit)]
    public async Task Output_Up_To_The_Limit_Is_Streamed_To_The_File_Whole(int bytes)
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var request = ToFile($"head -c {bytes} /dev/zero");

        var result = await _runner.RunAsync(request, default);

        Assert.Equal(new ProcessFileRunResult(0, false, false, bytes), result);
        Assert.Equal(bytes, new FileInfo(request.OutputPath).Length);
    }

    [SkippableFact]
    public async Task One_Byte_Past_The_Limit_Stops_And_Leaves_No_File()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var request = ToFile($"head -c {Limit + 1} /dev/zero");

        var result = await _runner.RunAsync(request, default);

        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.BytesWritten <= Limit);
        Assert.False(File.Exists(request.OutputPath));
    }

    [SkippableFact]
    public async Task A_Writer_That_Never_Stops_Is_Stopped_By_The_File_Cap_At_Once_With_Its_Tree()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "file-writer.pid");
        var request = ToFile($"yes & echo $! > '{pidFile}'; wait", maxBytes: 1024 * 1024, timeoutSeconds: 60);
        var clock = Stopwatch.StartNew();

        var result = await _runner.RunAsync(request, default);

        Assert.True(result.OutputLimitExceeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.False(File.Exists(request.OutputPath));
        await AssertGoneAsync(await ReadPidAsync(pidFile));
    }

    [SkippableFact]
    public async Task A_Cancelled_File_Run_Kills_Its_Tree_And_Deletes_The_File()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "file-sleeper.pid");
        var request = ToFile($"head -c 10 /dev/zero; sleep 60 & echo $! > '{pidFile}'; wait", timeoutSeconds: 120);
        using var cancel = new CancellationTokenSource();

        var run = _runner.RunAsync(request, cancel.Token);
        var pid = await ReadPidAsync(pidFile);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AssertGoneAsync(pid);
        Assert.False(File.Exists(request.OutputPath));
    }

    [SkippableFact]
    public async Task A_File_Run_That_Times_Out_Leaves_No_File()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var request = ToFile("head -c 10 /dev/zero; sleep 30", timeoutSeconds: 1);

        var result = await _runner.RunAsync(request, default);

        Assert.True(result.TimedOut);
        Assert.False(result.OutputLimitExceeded);
        Assert.False(File.Exists(request.OutputPath));
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
            {
                return pid;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("the child never wrote its pid");
    }

    // Killed processes are reaped by init; give it a moment, then the pid is gone.
    private static async Task AssertGoneAsync(int pid)
    {
        for (var i = 0; i < 100 && IsRunning(pid); i++)
        {
            await Task.Delay(50);
        }
        Assert.False(IsRunning(pid), $"process {pid} is still running");
    }

    private static bool IsRunning(int pid)
    {
        var status = $"/proc/{pid}/status";
        if (!File.Exists(status)) return false;
        try
        {
            // A zombie has exited; only its reaping is pending.
            return !File.ReadAllLines(status).Any(l => l.StartsWith("State:") && l.Contains('Z'));
        }
        catch (IOException)
        {
            return false;
        }
    }
}

using System.Diagnostics;

namespace NubArca.Api.Files;

/// <summary>
/// Production implementation of IProcessRunner. Runs a real external process
/// via System.Diagnostics.Process, captures its stdout as bytes, and enforces
/// the requested timeout. Also implements IDirectoryProcessRunner (video-hls
/// slice 1) for processes whose output is files in a working directory.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner, IDirectoryProcessRunner, IProcessFileRunner
{
    public async Task<ProcessDirectoryRunResult> RunAsync(
        ProcessDirectoryRunRequest request,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var psi = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain both pipes without retaining content (a full pipe would block
        // the child; the content may contain paths, so it is never logged).
        var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, cts.Token);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, cts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out (not the outer cancellation token).
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation: kill the child before propagating so a
            // cancelled job never leaves an orphan ffmpeg running.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await WaitForKilledExitAsync(process);
            throw;
        }

        if (timedOut)
        {
            await WaitForKilledExitAsync(process);
        }

        try { await stdoutTask; } catch { /* ignore */ }
        try { await stderrTask; } catch { /* ignore */ }

        return new ProcessDirectoryRunResult(timedOut ? -1 : process.ExitCode, timedOut);
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, ct) > 0)
        {
            // discard
        }
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var psi = new ProcessStartInfo(request.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout as bytes with a cap. stderr is drained and discarded,
        // never retained: it may contain paths or other invocation details.
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream,
            request.MaxStdoutBytes, cts.Token);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, cts.Token);
        var exitTask = process.WaitForExitAsync(cts.Token);

        var outcome = RunOutcome.Exited;
        try
        {
            // OUTPUT LIMIT EXCEEDED: the reader stops draining the pipe as soon
            // as the cap is passed, and a child that keeps writing — FFmpeg —
            // would then block on the full pipe until the timeout. It is killed
            // at once instead, and its partial output is never used.
            if (await Task.WhenAny(stdoutTask, exitTask) == stdoutTask
                && stdoutTask.IsCompletedSuccessfully && stdoutTask.Result.Truncated)
            {
                outcome = RunOutcome.OutputLimitExceeded;
                Kill(process);
            }
            await exitTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out (not the outer cancellation token).
            if (outcome != RunOutcome.OutputLimitExceeded) outcome = RunOutcome.TimedOut;
            Kill(process);
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation: kill the child before propagating so a
            // cancelled job never leaves an orphan FFmpeg running.
            Kill(process);
            await WaitForKilledExitAsync(process);
            try { await stdoutTask; } catch { /* ignore */ }
            try { await stderrTask; } catch { /* ignore */ }
            throw;
        }

        if (outcome != RunOutcome.Exited)
        {
            await WaitForKilledExitAsync(process);
        }

        byte[] stdout = [];
        if (outcome == RunOutcome.Exited)
        {
            try
            {
                var captured = await stdoutTask;
                stdout = captured.Truncated ? [] : captured.Bytes;
                if (captured.Truncated) outcome = RunOutcome.OutputLimitExceeded;
            }
            catch
            {
                stdout = [];
            }
        }
        else
        {
            try { await stdoutTask; } catch { /* ignore */ }
        }
        try { await stderrTask; } catch { /* ignore */ }

        return outcome switch
        {
            RunOutcome.TimedOut => new ProcessRunResult(-1, [], TimedOut: true),
            // Distinct from a timeout and from a failing exit code: the
            // process was stopped because it wrote more than it may.
            RunOutcome.OutputLimitExceeded => new ProcessRunResult(-1, [], TimedOut: false, OutputTruncated: true),
            _ => new ProcessRunResult(process.ExitCode, stdout, TimedOut: false),
        };
    }

    private enum RunOutcome { Exited, TimedOut, OutputLimitExceeded }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    // After a kill the process ends at once; the wait is bounded all the same,
    // so a stuck reaper can never hold a caller.
    private static async Task WaitForKilledExitAsync(Process process)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(wait.Token); } catch { /* bounded */ }
    }

    public async Task<ProcessFileRunResult> RunAsync(
        ProcessFileRunRequest request,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var psi = new ProcessStartInfo(request.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var copyTask = CopyCappedToFileAsync(
            process.StandardOutput.BaseStream, request.OutputPath, request.MaxOutputBytes, cts.Token);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, cts.Token);
        var exitTask = process.WaitForExitAsync(cts.Token);

        var outcome = RunOutcome.Exited;
        try
        {
            // The cap is checked as the bytes arrive: past it, the producer is
            // stopped now, not when it is done or when the timeout fires.
            if (await Task.WhenAny(copyTask, exitTask) == copyTask
                && copyTask.IsCompletedSuccessfully && copyTask.Result.Exceeded)
            {
                outcome = RunOutcome.OutputLimitExceeded;
                Kill(process);
            }
            await exitTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (outcome != RunOutcome.OutputLimitExceeded) outcome = RunOutcome.TimedOut;
            Kill(process);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await WaitForKilledExitAsync(process);
            try { await copyTask; } catch { /* ignore */ }
            try { await stderrTask; } catch { /* ignore */ }
            TryDeleteFile(request.OutputPath);
            throw;
        }

        if (outcome != RunOutcome.Exited)
        {
            await WaitForKilledExitAsync(process);
        }

        long written = 0;
        try
        {
            // After a normal exit the copy finishes what is still in the pipe —
            // and can still find the cap passed at the very end.
            var copied = await copyTask;
            written = copied.Written;
            if (copied.Exceeded) outcome = RunOutcome.OutputLimitExceeded;
        }
        catch
        {
            if (outcome == RunOutcome.Exited) outcome = RunOutcome.TimedOut;
        }
        try { await stderrTask; } catch { /* ignore */ }

        if (outcome != RunOutcome.Exited)
        {
            // Never a partial output: what was written is deleted.
            TryDeleteFile(request.OutputPath);
            return new ProcessFileRunResult(-1, outcome == RunOutcome.TimedOut,
                outcome == RunOutcome.OutputLimitExceeded, written);
        }
        return new ProcessFileRunResult(process.ExitCode, false, false, written);
    }

    private static async Task<(long Written, bool Exceeded)> CopyCappedToFileAsync(
        Stream source, string path, long maxBytes, CancellationToken ct)
    {
        await using var file = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (written + read > maxBytes)
            {
                // Over the cap: not one byte of this chunk is written.
                return (written, true);
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }
        return (written, false);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(maxBytes, 4 * 1024 * 1024)];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                // Oversized — discard what we have and signal truncation, so a
                // caller never parses a partial report as if it were complete.
                ms.SetLength(0);
                return ([], true);
            }
            ms.Write(buffer, 0, read);
        }
        return (ms.ToArray(), false);
    }
}

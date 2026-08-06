using System.Diagnostics;

namespace NubArca.Api.Files;

/// <summary>
/// Production implementation of IProcessRunner. Runs a real external process
/// via System.Diagnostics.Process, captures its stdout as bytes, and enforces
/// the requested timeout. Also implements IDirectoryProcessRunner (video-hls
/// slice 1) for processes whose output is files in a working directory.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner, IDirectoryProcessRunner
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
            throw;
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

        // Read stdout as bytes with a cap. Do not log stderr (may contain
        // paths or other sensitive invocation details); discard it.
        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream,
            request.MaxStdoutBytes, cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

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

        byte[] stdout;
        var truncated = false;
        try
        {
            var captured = await stdoutTask;
            stdout = captured.Bytes;
            truncated = captured.Truncated;
        }
        catch
        {
            stdout = [];
        }

        // Discard stderr without reading sensitive content into logs.
        try { await stderrTask; } catch { /* ignore */ }

        return new ProcessRunResult(timedOut ? -1 : process.ExitCode, stdout, timedOut, truncated);
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

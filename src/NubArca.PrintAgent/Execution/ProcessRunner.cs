using System.ComponentModel;
using System.Diagnostics;

namespace NubArca.PrintAgent.Execution;

/// <summary>
/// Runs a program directly, with <see cref="ProcessStartInfo.ArgumentList"/> and
/// no shell.
///
/// The C locale is forced so CUPS and NetworkManager answer in the same words on
/// every box, whatever language the operating system was installed in: the
/// parsers read those words, and "printer X is idle" must not become Italian.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Output kept per stream; anything longer is a misbehaving tool, not data.</summary>
    private const int MaxCapturedChars = 256 * 1024;

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return ProcessResult.Missing(fileName);
        }
        catch (Win32Exception)
        {
            return ProcessResult.Missing(fileName);
        }
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return ProcessResult.Expired(fileName);
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = await reader.ReadToEndAsync();
        return text.Length <= MaxCapturedChars ? text : text[..MaxCapturedChars];
    }
}

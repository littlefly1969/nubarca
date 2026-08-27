using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Ai.Documents;

/// The baseline local OCR: Tesseract, over a process boundary.
///
/// A PROCESS, not a managed wrapper. The wrappers bind a native library into
/// this process, where a crash in a C++ image decoder handed an attacker-chosen
/// page takes the API down with it, and where a hang has no way to be
/// interrupted. A child process can be killed. That is the entire argument, and
/// it is why the timeout below is a real bound rather than a `Task` that stops
/// being awaited while the work continues.
///
/// STDIN AND STDOUT, so no private page is ever written to a temp file. A
/// person's scanned document does not become a file on disk that a cleanup bug,
/// a crash or another process could leave behind.
///
/// NO SHELL, EVER. Arguments go through `ArgumentList`, which passes them as a
/// vector rather than a string a shell re-parses — so nothing derived from a
/// document, a filename or configuration can become a second command. Nothing
/// user-controlled reaches the argument list anyway: the language is validated
/// against what is installed, and the page arrives on stdin.
public sealed class TesseractOcrProvider : IDocumentOcrProvider
{
    /// The engine binary, resolved from PATH. Deliberately not configurable as a
    /// path: an operator-supplied executable path is an execution primitive, and
    /// the distribution package is what the Docker image installs.
    private const string Executable = "tesseract";

    private readonly IOptions<DocumentExtractionOptions> _options;
    private readonly ILogger<TesseractOcrProvider> _log;

    /// INSTALLATION-WIDE, not per request. One `OwnerDocumentIndexer` loop is
    /// sequential, which bounds nothing: several owners indexing at once would
    /// otherwise be several engine processes each pinning a core. The gate is
    /// static because the resource it protects — this machine's CPU — is shared
    /// by every scope in the process.
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static int _configuredConcurrency = 4;
    private static readonly object ConcurrencyLock = new();

    public TesseractOcrProvider(
        IOptions<DocumentExtractionOptions> options, ILogger<TesseractOcrProvider> log)
    {
        _options = options;
        _log = log;
    }

    public string Provider => "tesseract";

    public OcrReadiness CheckReadiness()
    {
        var options = _options.Value;
        if (!options.OcrEnabled) return OcrReadiness.NotReady(DocumentExtractionReasons.OcrUnavailable);

        var installed = InstalledLanguages();
        if (installed is null) return OcrReadiness.NotReady(DocumentExtractionReasons.OcrUnavailable);

        // A CONFIGURED LANGUAGE THAT IS NOT INSTALLED IS NOT READY, and nothing
        // downloads it. A first-run fetch would be a network call at document
        // index time, from a component whose entire promise is that it makes
        // none.
        foreach (var language in ConfiguredLanguages(options))
        {
            if (!installed.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                return OcrReadiness.NotReady(DocumentExtractionReasons.OcrUnavailable);
            }
        }

        return OcrReadiness.Ready;
    }

    public async Task<OcrPageResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes,
        OcrPageRequest request,
        CancellationToken cancellationToken = default)
    {
        var readiness = CheckReadiness();
        if (!readiness.IsReady) return OcrPageResult.Failed(readiness.Reason!);

        ApplyConcurrency(_options.Value.EffectiveMaxConcurrentOcrPages);

        // Cancellation reaches here as itself. Waiting for the gate is where an
        // index run spends most of its time when several are queued, and a
        // cancel that only took effect after the wait would look like a hang.
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await RunAsync(imageBytes, request, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<OcrPageResult> RunAsync(
        ReadOnlyMemory<byte> imageBytes, OcrPageRequest request, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // `-` twice: read the image from stdin, write the text to stdout.
        info.ArgumentList.Add("-");
        info.ArgumentList.Add("-");
        info.ArgumentList.Add("-l");
        info.ArgumentList.Add(request.Language);

        using var process = new Process { StartInfo = info };

        try
        {
            if (!process.Start())
            {
                return OcrPageResult.Failed(DocumentExtractionReasons.OcrProcessFailed);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The binary is not there. An environment fact, and the reason code
            // says so — it must never become a verdict about the document.
            return OcrPageResult.Failed(DocumentExtractionReasons.OcrUnavailable);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var reasonOverride = (string?)null;
        try
        {
            var write = WriteImageAsync(process, imageBytes, deadline.Token);
            var read = ReadBoundedAsync(process, request.MaxCharacters, deadline.Token);
            // STDERR IS DRAINED AND DISCARDED. It carries progress chatter and,
            // on failure, paths — so it must be consumed to stop the pipe
            // filling and blocking the child, and must never be logged verbatim.
            var drain = DrainAsync(process.StandardError, deadline.Token);

            await write;
            var text = await read;
            await drain;

            await process.WaitForExitAsync(deadline.Token);

            if (process.ExitCode != 0)
            {
                return OcrPageResult.Failed(DocumentExtractionReasons.OcrProcessFailed);
            }

            return OcrPageResult.Recognized(text);
        }
        catch (OutputTooLargeException)
        {
            reasonOverride = DocumentExtractionReasons.OcrOutputTooLarge;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The DEADLINE won, not the caller. A timeout, and the process is
            // killed below — a budget that leaves the work running is a report
            // rather than a bound.
            reasonOverride = DocumentExtractionReasons.OcrTimeout;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            reasonOverride = DocumentExtractionReasons.OcrProcessFailed;
        }
        finally
        {
            KillQuietly(process);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancellation reaches the caller AS ITSELF. Reporting an operator's
            // deliberate stop as an OCR timeout would record a permanent-looking
            // failure for something nobody did wrong.
            cancellationToken.ThrowIfCancellationRequested();
        }

        return OcrPageResult.Failed(reasonOverride ?? DocumentExtractionReasons.OcrProcessFailed);
    }

    private static async Task WriteImageAsync(
        Process process, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            // Closing stdin is what tells the engine the page is complete. A
            // process still waiting for input never exits, and the timeout would
            // be the only thing that ever ended it.
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
    }

    /// Reads stdout under a HARD character cap.
    ///
    /// This is untrusted process output. The engine's own limits are not a bound
    /// NubArca controls, so the cap is enforced on what is actually read: one
    /// character past the limit aborts, and the caller kills the process.
    private static async Task<string> ReadBoundedAsync(
        Process process, int maxCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var reader = process.StandardOutput;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            if (builder.Length + read > maxCharacters)
            {
                throw new OutputTooLargeException();
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var seen = 0;
        while (seen < 1_000_000)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            seen += read;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Already gone. Nothing here is worth failing a document over.
        }
    }

    private static void ApplyConcurrency(int configured)
    {
        lock (ConcurrencyLock)
        {
            // The semaphore is created at its maximum and narrowed by holding
            // permits, because SemaphoreSlim cannot be resized. Widening again
            // releases them.
            while (_configuredConcurrency > configured)
            {
                if (!Gate.Wait(0)) break;
                _configuredConcurrency--;
            }
            while (_configuredConcurrency < configured && _configuredConcurrency < 4)
            {
                Gate.Release();
                _configuredConcurrency++;
            }
        }
    }

    /// Languages the engine actually has. Asked of the engine, never assumed.
    private static IReadOnlyCollection<string>? InstalledLanguages()
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--list-langs");

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            // The first line is a header naming the tessdata DIRECTORY — a
            // filesystem path, which is exactly what diagnostics must not carry.
            // Only the tokens after it are kept.
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains(':', StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Configured languages, validated as tokens before they are ever passed to
    /// a process. `[a-z_]{3,}` is what a Tesseract language name is; anything
    /// else is dropped rather than forwarded.
    public static IReadOnlyList<string> ConfiguredLanguages(DocumentExtractionOptions options)
        => (options.OcrLanguages ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length is >= 3 and <= 16 && t.All(c => char.IsAsciiLetterLower(c) || c == '_'))
            .ToArray();

    /// The `-l` value: validated tokens, joined the way the engine expects.
    public static string LanguageArgument(DocumentExtractionOptions options)
    {
        var languages = ConfiguredLanguages(options);
        return languages.Count == 0 ? "eng" : string.Join('+', languages);
    }

    private sealed class OutputTooLargeException : Exception;
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NubArca.Api.MailArchive;

/// <summary>
/// The Rust PST engine (tools/nubarca-pst-importer) seen from .NET. The engine
/// has no database, storage or credential access: it reads one PST copy and
/// writes per-message staging files below a work directory we give it.
/// </summary>
public interface IPstEngine
{
    bool IsAvailable { get; }

    Task<PstProbe> ProbeAsync(string pstPath, CancellationToken cancellationToken);

    IAsyncEnumerable<PstListEvent> ListAsync(string pstPath, CancellationToken cancellationToken);

    Task<IPstExtractionSession> StartSessionAsync(string pstPath, string workDir, CancellationToken cancellationToken);
}

public interface IPstExtractionSession : IAsyncDisposable
{
    Task<PstExtractResponse> ExtractAsync(string requestId, string messageKey, CancellationToken cancellationToken);
}

public sealed partial class ProcessPstEngine : IPstEngine
{
    // A protocol line longer than this is a broken engine, not data: the
    // documents themselves travel as files.
    private const int MaxLineChars = 4 * 1024 * 1024;

    private readonly MailArchiveOptions _options;
    private readonly ILogger<ProcessPstEngine> _logger;

    public ProcessPstEngine(IOptions<MailArchiveOptions> options, ILogger<ProcessPstEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ImporterPath)
        && File.Exists(_options.ImporterPath);

    public async Task<PstProbe> ProbeAsync(string pstPath, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var process = EngineProcess.Start(_options, _logger, ["probe", "--pst", pstPath]);
        var line = await process.ReadLineAsync(timeout.Token)
            ?? throw new PstEngineException("engine_no_output", "The PST engine produced no probe result.");
        var probe = Parse<PstProbe>(line);
        if (probe.Type == "fatal")
        {
            var fatal = Parse<PstListEvent>(line);
            throw new PstEngineException(fatal.Code ?? "fatal", Sanitize(fatal.Message));
        }
        if (probe.Type != "probe" || probe.Protocol != PstProtocol.Version)
        {
            throw new PstEngineException("protocol_mismatch", "The PST engine speaks an unexpected protocol.");
        }
        await process.WaitForExitAsync(timeout.Token);
        return probe;
    }

    public async IAsyncEnumerable<PstListEvent> ListAsync(
        string pstPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.ListTimeoutMinutes)));
        await using var process = EngineProcess.Start(_options, _logger, ["list", "--pst", pstPath]);
        var ended = false;
        while (await process.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            var evt = Parse<PstListEvent>(line);
            if (evt.Type == "fatal")
            {
                throw new PstEngineException(evt.Code ?? "fatal", Sanitize(evt.Message));
            }
            yield return evt;
            if (evt.Type == "end")
            {
                ended = true;
                break;
            }
        }
        if (!ended)
        {
            throw new PstEngineException("list_incomplete", "The PST engine stopped before the end of the archive.");
        }
        await process.WaitForExitAsync(timeout.Token);
    }

    public Task<IPstExtractionSession> StartSessionAsync(
        string pstPath, string workDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        var process = EngineProcess.Start(_options, _logger, ["serve", "--pst", pstPath, "--work-dir", workDir]);
        return Task.FromResult<IPstExtractionSession>(new Session(process, _options));
    }

    private static T Parse<T>(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, PstProtocol.Json)
                ?? throw new PstEngineException("protocol_error", "Empty protocol line.");
        }
        catch (JsonException)
        {
            throw new PstEngineException("protocol_error", "The PST engine wrote an unreadable protocol line.");
        }
    }

    // Engine messages are short technical sentences; keep them that way.
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "PST engine error.";
        }
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 200 ? oneLine : oneLine[..200];
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    internal static partial Regex RequestIdPattern();

    private sealed class Session : IPstExtractionSession
    {
        private readonly EngineProcess _process;
        private readonly MailArchiveOptions _options;
        private bool _broken;

        public Session(EngineProcess process, MailArchiveOptions options)
        {
            _process = process;
            _options = options;
        }

        public async Task<PstExtractResponse> ExtractAsync(
            string requestId, string messageKey, CancellationToken cancellationToken)
        {
            if (!RequestIdPattern().IsMatch(requestId))
            {
                throw new ArgumentException("Invalid request id.", nameof(requestId));
            }
            if (_broken)
            {
                throw new PstEngineException("engine_broken", "The PST engine session is no longer usable.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.ExtractTimeoutSeconds)));
            try
            {
                var request = JsonSerializer.Serialize(
                    new { op = "extract", requestId, messageId = messageKey }, PstProtocol.Json);
                await _process.WriteLineAsync(request, timeout.Token);
                var line = await _process.ReadLineAsync(timeout.Token)
                    ?? throw new PstEngineException("engine_exited", "The PST engine exited during extraction.");
                var response = Parse<PstExtractResponse>(line);
                if (response.RequestId != requestId)
                {
                    throw new PstEngineException("protocol_error", "The PST engine answered another request.");
                }
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _broken = true;
                throw new PstEngineException("timeout", "The PST engine did not answer in time.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _broken = true;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_broken)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _process.WriteLineAsync("{\"op\":\"shutdown\"}", cts.Token);
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (Exception)
                {
                    // Killed below.
                }
            }
            await _process.DisposeAsync();
        }
    }

    // One child process: argument list (never a shell), stdout read as bounded
    // UTF-8 lines, stderr drained so the pipe never fills (logged at debug,
    // truncated: the engine never writes content there).
    private sealed class EngineProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamReader _stdout;
        private readonly Task _stderrDrain;
        private readonly char[] _buffer = new char[8192];
        private int _bufferLength;
        private int _bufferPosition;

        private EngineProcess(Process process, ILogger logger)
        {
            _process = process;
            _stdout = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
            _stderrDrain = Task.Run(async () =>
            {
                var lines = 0;
                while (await process.StandardError.ReadLineAsync() is { } errLine)
                {
                    if (++lines <= 20)
                    {
                        logger.LogDebug("pst engine: {Line}", Sanitize(errLine));
                    }
                }
            });
        }

        public static EngineProcess Start(MailArchiveOptions options, ILogger logger, IReadOnlyList<string> args)
        {
            var info = new ProcessStartInfo(options.ImporterPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            info.ArgumentList.Add("--max-attachment-bytes");
            info.ArgumentList.Add(options.MaxAttachmentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--max-body-bytes");
            info.ArgumentList.Add(options.MaxBodyBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--max-document-bytes");
            info.ArgumentList.Add(options.MaxDocumentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--max-embedded-depth");
            info.ArgumentList.Add(options.MaxEmbeddedDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--max-attachments-per-message");
            info.ArgumentList.Add(options.MaxAttachmentsPerMessage.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var process = Process.Start(info)
                ?? throw new PstEngineException("engine_unavailable", "The PST engine could not be started.");
            return new EngineProcess(process, logger);
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            while (true)
            {
                if (_bufferPosition >= _bufferLength)
                {
                    _bufferLength = await _stdout.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    _bufferPosition = 0;
                    if (_bufferLength == 0)
                    {
                        return builder.Length > 0 ? builder.ToString() : null;
                    }
                }
                var span = _buffer.AsSpan(_bufferPosition, _bufferLength - _bufferPosition);
                var newline = span.IndexOf('\n');
                if (newline >= 0)
                {
                    builder.Append(span[..newline]);
                    _bufferPosition += newline + 1;
                    return builder.ToString().TrimEnd('\r');
                }
                builder.Append(span);
                _bufferPosition = _bufferLength;
                if (builder.Length > MaxLineChars)
                {
                    throw new PstEngineException("protocol_error", "The PST engine wrote an oversized protocol line.");
                }
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Best effort.
            }
            _stdout.Dispose();
            _process.Dispose();
        }
    }
}

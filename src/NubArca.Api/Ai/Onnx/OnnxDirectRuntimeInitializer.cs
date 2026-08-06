using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace NubArca.Api.Ai.Onnx;

// Stable, non-sensitive ONNX runtime diagnostics (no paths, secrets or native
// exception text). Surfaced at startup and available for restricted diagnostics.
public sealed record OnnxRuntimeInfo(
    string ManagedOrtVersion,
    string? NativeCoreVersion,
    string? OpenVinoVersion,
    IReadOnlyList<string> AvailableProviders,
    string ConfiguredProvider,
    bool AbiMatches);

// Gate 3C startup guard for openvino-direct: installs the OpenVINO native resolver
// BEFORE any ORT session is created anywhere in the process, fails closed on a
// managed/native ABI mismatch or a conflicting extra ORT core in the app dir, and
// logs sanitized version diagnostics. No-op for onnxruntime.
public sealed class OnnxDirectRuntimeInitializer : IHostedService
{
    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;
    private readonly ILogger<OnnxDirectRuntimeInitializer> _logger;

    public OnnxDirectRuntimeInitializer(
        IOptions<AiOptions> options,
        IOnnxInferenceSessionFactory factory,
        ILogger<OnnxDirectRuntimeInitializer> logger)
    {
        _options = options;
        _factory = factory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var onnx = _options.Value.Onnx;
        var provider = OnnxExecutionProviders.Normalize(onnx.ExecutionProvider);
        // Install the NativeLibrary resolver before the first ORT native load in
        // this process — required for ALL in-process providers because ORT 1.24.1
        // cannot self-resolve its native on Linux. onnxruntime selects the stock
        // CPU core; openvino-direct selects the OV core.
        _factory.EnsureNativeProviderInitialized();

        if (provider is OnnxExecutionProviders.OnnxRuntime)
        {
            _logger.LogInformation(
                "ONNX native resolver installed ({State}) for provider={Provider} (managedOrt={Managed}).",
                _factory.NativeCoreState, provider,
                typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown");
            return Task.CompletedTask;
        }

        var nativeDir = onnx.OpenVino.NativeDir;
        var managed = typeof(InferenceSession).Assembly.GetName().Version;
        var core = OnnxOpenVinoNativeResolver.FindCore(nativeDir);
        var nativeVersion = core is null ? null : ParseCoreVersion(core);

        // Fail closed: a managed/native ABI mismatch must never reach inference.
        if (core is null)
            throw new InvalidOperationException(
                "openvino-direct configured but no OpenVINO-enabled ORT core was found (onnx-openvino-native-missing).");
        if (managed is null || nativeVersion is null
            || managed.Major != nativeVersion.Major || managed.Minor != nativeVersion.Minor)
            throw new InvalidOperationException(
                $"ONNX Runtime ABI mismatch: managed {managed?.ToString() ?? "?"} vs native {nativeVersion?.ToString() ?? "?"} (onnx-abi-mismatch).");

        // Fail closed on a CONFLICTING extra ORT core (a different major.minor) in
        // the app directory that could be selected instead of the pinned core.
        foreach (var (file, ver) in DiscoverAppDirCores())
        {
            if (ver is not null && (ver.Major != managed.Major || ver.Minor != managed.Minor))
                throw new InvalidOperationException(
                    $"Conflicting ONNX Runtime core '{Path.GetFileName(file)}' (v{ver}) differs from managed {managed}.");
        }

        var info = GatherInfo(onnx, managed, nativeVersion);
        _logger.LogInformation(
            "ONNX direct runtime ready: managedOrt={Managed} nativeOrt={Native} openvino={OpenVino} providers=[{Providers}] provider={Provider} abiMatch={Abi}",
            info.ManagedOrtVersion, info.NativeCoreVersion, info.OpenVinoVersion,
            string.Join(",", info.AvailableProviders), info.ConfiguredProvider, info.AbiMatches);

        if (!info.AvailableProviders.Contains("OpenVINOExecutionProvider"))
            throw new InvalidOperationException("OpenVINOExecutionProvider is not available in the loaded ONNX Runtime.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Non-throwing diagnostics snapshot (safe to expose in restricted endpoints).
    public OnnxRuntimeInfo GatherInfo(AiOnnxOptions? onnx = null, Version? managed = null, Version? nativeVersion = null)
    {
        onnx ??= _options.Value.Onnx;
        managed ??= typeof(InferenceSession).Assembly.GetName().Version;
        var core = OnnxOpenVinoNativeResolver.FindCore(onnx.OpenVino.NativeDir);
        nativeVersion ??= core is null ? null : ParseCoreVersion(core);
        string? openvino = FindOpenVinoVersion(onnx.OpenVino.NativeDir);
        var providers = SafeProviders();
        var abi = managed is not null && nativeVersion is not null
            && managed.Major == nativeVersion.Major && managed.Minor == nativeVersion.Minor;
        return new OnnxRuntimeInfo(
            managed?.ToString() ?? "unknown", nativeVersion?.ToString(), openvino,
            providers, OnnxExecutionProviders.Normalize(onnx.ExecutionProvider), abi);
    }

    // Sanitized last error from the most recent provider probe (diagnostics only).
    public static string? LastProvidersError { get; private set; }

    private static IReadOnlyList<string> SafeProviders()
    {
        try { LastProvidersError = null; return OrtEnv.Instance().GetAvailableProviders().ToArray(); }
        catch (Exception ex) { LastProvidersError = ex.GetType().Name + ": " + ex.Message; return Array.Empty<string>(); }
    }

    private static IEnumerable<(string File, Version? Version)> DiscoverAppDirCores()
    {
        var dir = AppContext.BaseDirectory;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "libonnxruntime.so.*"))
            yield return (f, ParseCoreVersion(f));
    }

    private static Version? ParseCoreVersion(string coreFile)
    {
        var name = Path.GetFileName(coreFile);
        var i = name.IndexOf(".so.", StringComparison.Ordinal);
        return i >= 0 && Version.TryParse(name[(i + 4)..], out var v) ? v : null;
    }

    private static string? FindOpenVinoVersion(string? nativeDir)
    {
        if (string.IsNullOrWhiteSpace(nativeDir) || !Directory.Exists(nativeDir)) return null;
        foreach (var f in Directory.EnumerateFiles(nativeDir, "libopenvino.so.*"))
        {
            var name = Path.GetFileName(f);
            var i = name.IndexOf(".so.", StringComparison.Ordinal);
            // Prefer the dotted x.y.z form (e.g. libopenvino.so.2025.4.1).
            if (i >= 0 && name[(i + 4)..].Contains('.')) return name[(i + 4)..];
        }
        return null;
    }
}

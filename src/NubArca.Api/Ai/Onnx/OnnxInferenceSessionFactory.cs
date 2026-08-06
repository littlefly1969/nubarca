using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace NubArca.Api.Ai.Onnx;

// Concrete session factory. Sessions are created lazily and cached by a typed,
// immutable key (OnnxSessionCacheKey) with canonicalized paths; creation is
// single-flight (Lazy, ExecutionAndPublication) so a compile never races, and a
// FAILED init is evicted so a later Acquire can retry. All native/compile
// failures are converted to sanitized reason codes — raw native text (which can
// embed paths/devices) never reaches callers.
public sealed class OnnxInferenceSessionFactory : IOnnxInferenceSessionFactory
{
    // Sanitized reason tokens (stable; safe to log / surface to clients).
    internal const string ReasonModelNotFound = "onnx-model-not-found";
    internal const string ReasonProviderInvalid = "onnx-provider-invalid";
    internal const string ReasonNativeMissing = "onnx-openvino-native-missing";
    internal const string ReasonStockNativeMissing = "onnx-stock-native-missing";
    internal const string ReasonAbiMismatch = "onnx-abi-mismatch";
    internal const string ReasonProviderUnavailable = "onnx-openvino-provider-unavailable";
    internal const string ReasonDeviceUnavailable = "onnx-device-unavailable";
    internal const string ReasonNativeUnavailable = "onnx-native-unavailable";
    internal const string ReasonCompileFailed = "onnx-compile-failed";

    private readonly IOptions<AiOptions> _options;
    private readonly ILogger<OnnxInferenceSessionFactory> _logger;
    private readonly Func<OnnxSessionCreateSpec, IOnnxSession> _create;
    private readonly OnnxOpenVinoNativeResolver _resolver;
    private readonly ConcurrentDictionary<OnnxSessionCacheKey, Lazy<IOnnxSession>> _cache = new();
    // DUAL:CPU,GPU dispatchers, keyed by the model's DUAL placement. The sessions
    // they hand out live in _cache (disposed there); a pool owns only its scheduler.
    private readonly ConcurrentDictionary<OnnxSessionCacheKey, Lazy<DualSessionPool>> _dualPools = new();
    private readonly object _disposeGate = new();
    private volatile bool _disposed;

    public OnnxInferenceSessionFactory(
        IOptions<AiOptions> options, ILogger<OnnxInferenceSessionFactory> logger)
        : this(options, logger, sessionCreator: null) { }

    // Test seam: inject a fake session creator (exercise caching/lifecycle without
    // the OpenVINO native stack) and/or a no-op resolver installer (avoid the
    // process-global NativeLibrary registration side effect).
    internal OnnxInferenceSessionFactory(
        IOptions<AiOptions> options,
        ILogger<OnnxInferenceSessionFactory> logger,
        Func<OnnxSessionCreateSpec, IOnnxSession>? sessionCreator,
        Action<string>? installResolver = null)
    {
        _options = options;
        _logger = logger;
        _create = sessionCreator ?? CreateNativeSession;
        _resolver = new OnnxOpenVinoNativeResolver(installResolver);
    }

    public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec)
    {
        var onnx = _options.Value.Onnx;
        if (!OnnxExecutionProviders.IsKnown(onnx.ExecutionProvider))
            return OnnxSessionReadiness.NotReady(ReasonProviderInvalid);

        var provider = OnnxExecutionProviders.Normalize(onnx.ExecutionProvider);
        if (string.IsNullOrWhiteSpace(spec.ModelPath) || !File.Exists(spec.ModelPath))
            return OnnxSessionReadiness.NotReady(ReasonModelNotFound);

        if (provider == OnnxExecutionProviders.OnnxRuntime
            && OnnxOpenVinoNativeResolver.FindStockCore() is null)
        {
            // Distinct from the OpenVINO-native reason so operators can tell which
            // core is missing.
            return OnnxSessionReadiness.NotReady(ReasonStockNativeMissing);
        }

        if (provider == OnnxExecutionProviders.OpenVinoDirect)
        {
            var core = OnnxOpenVinoNativeResolver.FindCore(onnx.OpenVino.NativeDir);
            if (core is null)
                return OnnxSessionReadiness.NotReady(ReasonNativeMissing);
            // Fail closed on a managed/native ABI mismatch so a package downgrade
            // (or the pre-3C 1.20↔1.24 skew) can never load an incompatible core.
            if (!AbiMatches(core))
                return OnnxSessionReadiness.NotReady(ReasonAbiMismatch);
            // Install the resolver BEFORE the first ORT native touch below.
            _resolver.EnsureInstalled(onnx.OpenVino.NativeDir);
            if (!OpenVinoProviderAvailable())
                return OnnxSessionReadiness.NotReady(ReasonProviderUnavailable);
            // /dev/dri absence is a cheap NEGATIVE hint only — its presence never
            // classifies the GPU as usable. The authoritative device result is the
            // compile in Acquire (→ ReasonDeviceUnavailable on failure).
            if (IsGpu(onnx.OpenVino.DeviceFor(spec.Model)) && !Directory.Exists("/dev/dri"))
                return OnnxSessionReadiness.NotReady(ReasonDeviceUnavailable);
        }

        return OnnxSessionReadiness.Ready;
    }

    public IOnnxSessionLease Acquire(OnnxModelSpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var readiness = CheckReadiness(spec);
        if (!readiness.IsReady)
            throw new OnnxSessionUnavailableException(readiness.Reason!);

        // DUAL:CPU,GPU placement → an EXCLUSIVE executor from a bounded CPU+GPU pool
        // (graceful CPU-only when the GPU leg is unavailable). Only openvino-direct.
        if (IsDualPlacement(spec))
            return AcquireDual(spec);

        var session = GetOrCreateCachedSession(BuildCreateSpec(spec));

        // Dispose raced in after creation → do not hand out a live session. The
        // session may have been added AFTER Dispose()'s sweep, so dispose it here
        // (InferenceSession.Dispose is idempotent) to avoid a leak.
        if (_disposed)
        {
            try { session.Dispose(); } catch { /* best effort */ }
            throw new ObjectDisposedException(nameof(OnnxInferenceSessionFactory));
        }
        return new SharedLease(session);
    }

    // Gets or creates the cached session for a fully-resolved single-device spec.
    // Single-flight (Lazy, ExecutionAndPublication); a FAILED init is evicted so a
    // later call retries. Throws the sanitized OnnxSessionUnavailableException.
    private IOnnxSession GetOrCreateCachedSession(OnnxSessionCreateSpec createSpec)
    {
        var key = createSpec.Key;
        var lazy = _cache.GetOrAdd(key,
            _ => new Lazy<IOnnxSession>(() => CreateChecked(createSpec), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Never cache a failed initialization — evict so a later call retries
            // (Lazy caches the exception, so this exact instance must be removed).
            _cache.TryRemove(new KeyValuePair<OnnxSessionCacheKey, Lazy<IOnnxSession>>(key, lazy));
            throw;
        }
    }

    // openvino-direct + the model is configured for the DUAL:CPU,GPU placement.
    internal bool IsDualPlacement(OnnxModelSpec spec)
    {
        var onnx = _options.Value.Onnx;
        return OnnxExecutionProviders.Normalize(onnx.ExecutionProvider) == OnnxExecutionProviders.OpenVinoDirect
            && OnnxDevice.IsDual(onnx.OpenVino.DeviceFor(spec.Model));
    }

    // Borrows one exclusive executor from the model's CPU+GPU pool (blocks until a
    // device is free). The pool is compiled once (single-flight) and its sessions
    // live in the shared cache; a failed pool build is evicted so a later Acquire
    // retries.
    private IOnnxSessionLease AcquireDual(OnnxModelSpec spec)
    {
        var key = BuildCreateSpec(spec, OnnxDevice.DualCpuGpu).Key;
        var lazy = _dualPools.GetOrAdd(key,
            _ => new Lazy<DualSessionPool>(() => CreateDualPool(spec), LazyThreadSafetyMode.ExecutionAndPublication));
        DualSessionPool pool;
        try
        {
            pool = lazy.Value;
        }
        catch
        {
            _dualPools.TryRemove(new KeyValuePair<OnnxSessionCacheKey, Lazy<DualSessionPool>>(key, lazy));
            throw;
        }

        var lease = pool.Acquire();
        if (_disposed)
        {
            lease.Dispose(); // return the executor to the pool
            throw new ObjectDisposedException(nameof(OnnxInferenceSessionFactory));
        }
        return lease;
    }

    // Compiles the two device legs of a DUAL placement. The CPU leg is MANDATORY
    // (fail closed with a sanitized reason if even CPU cannot compile). The GPU leg
    // is GRACEFUL: an unavailable device degrades DUAL to a CPU-only pool — explicit
    // and logged, never a silent quality change (both legs are FP32).
    internal DualSessionPool CreateDualPool(OnnxModelSpec spec)
    {
        var executors = new List<DualExecutor>(2)
        {
            new(GetOrCreateCachedSession(BuildCreateSpec(spec, OnnxDevice.Cpu)), OnnxDevice.Cpu),
        };
        try
        {
            executors.Add(new(GetOrCreateCachedSession(BuildCreateSpec(spec, OnnxDevice.Gpu)), OnnxDevice.Gpu));
            _logger.LogInformation("ONNX DUAL image tower ready on CPU+GPU (2 executors, FP32).");
        }
        catch (OnnxSessionUnavailableException ex)
        {
            _logger.LogWarning(
                "ONNX DUAL image tower degraded to CPU-only: GPU executor unavailable ({Reason}).", ex.ReasonCode);
        }
        return new DualSessionPool(executors);
    }

    // ORT 1.24.1's managed layer cannot resolve its native by the default
    // "onnxruntime.dll" name on Linux, so EVERY in-process provider needs the
    // NativeLibrary resolver to explicit-load a core before the first ORT call:
    //   openvino-direct → the OpenVINO-enabled core (NativeDir)
    //   onnxruntime     → the stock NuGet linux core shipped in runtimes/<rid>/native
    public void EnsureNativeProviderInitialized()
    {
        var provider = OnnxExecutionProviders.Normalize(_options.Value.Onnx.ExecutionProvider);
        if (provider == OnnxExecutionProviders.OpenVinoDirect)
            _resolver.EnsureInstalled(_options.Value.Onnx.OpenVino.NativeDir);          // OpenVINO core
        else if (provider == OnnxExecutionProviders.OnnxRuntime)
            _resolver.EnsureInstalledCore(                                              // stock CPU core
                OnnxOpenVinoNativeResolver.FindStockCore(), OnnxNativeCoreState.StockCpuCore);
    }

    // Process-global native-core selection (diagnostics; immutable after first use).
    public OnnxNativeCoreState NativeCoreState => _resolver.State;

    // Resolves provider/device/precision/threads/paths for a model from options,
    // canonicalizing paths so equivalent spellings share one cached session. Pass
    // deviceOverride to force a concrete single device (used by the DUAL pool to
    // build its CPU and GPU legs); otherwise the per-model configured device is used.
    internal OnnxSessionCreateSpec BuildCreateSpec(OnnxModelSpec spec, string? deviceOverride = null)
    {
        var onnx = _options.Value.Onnx;
        var provider = OnnxExecutionProviders.Normalize(onnx.ExecutionProvider);
        var modelPath = CanonicalPath(spec.ModelPath);
        if (provider != OnnxExecutionProviders.OpenVinoDirect)
            return new OnnxSessionCreateSpec(
                modelPath, OnnxExecutionProviders.OnnxRuntime, "CPU", "FP32",
                onnx.IntraOpThreads, CacheDir: null, NativeDir: null);

        var device = (deviceOverride ?? onnx.OpenVino.DeviceFor(spec.Model)).Trim().ToUpperInvariant();
        // FP32 is mandatory for output-equivalence (matches the sidecar); CPU is
        // FP32 intrinsically, GPU is forced to FP32.
        return new OnnxSessionCreateSpec(
            modelPath, OnnxExecutionProviders.OpenVinoDirect, device, "FP32",
            onnx.IntraOpThreads, CanonicalDir(onnx.OpenVino.CacheDir), CanonicalDir(onnx.OpenVino.NativeDir));
    }

    private IOnnxSession CreateChecked(OnnxSessionCreateSpec spec)
    {
        try
        {
            return _create(spec);
        }
        catch (OnnxSessionUnavailableException)
        {
            throw; // already sanitized
        }
        catch (DllNotFoundException)
        {
            throw new OnnxSessionUnavailableException(ReasonNativeUnavailable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ONNX session init failed for provider={Provider} device={Device}: {Type}",
                spec.Provider, spec.Device, ex.GetType().Name);
            var reason = LooksLikeDeviceFailure(ex) ? ReasonDeviceUnavailable : ReasonCompileFailed;
            throw new OnnxSessionUnavailableException(reason);
        }
    }

    // Real session construction: applies EP, device, FP32, threads, OpenVINO cache.
    private IOnnxSession CreateNativeSession(OnnxSessionCreateSpec spec)
    {
        var so = new SessionOptions();
        if (spec.Provider != OnnxExecutionProviders.OpenVinoDirect)
        {
            if (spec.IntraOpThreads is > 0) so.IntraOpNumThreads = spec.IntraOpThreads.Value;
            return new OrtOnnxSession(new InferenceSession(spec.ModelPath, so));
        }

        _resolver.EnsureInstalled(spec.NativeDir); // before any ORT native call
        so.AppendExecutionProvider("OpenVINO", BuildOpenVinoOptions(spec));
        return new OrtOnnxSession(new InferenceSession(spec.ModelPath, so));
    }

    // Pure OpenVINO provider-option assembly (unit-tested): FP32 is set for GPU; an
    // optional compile cache and CPU thread cap are passed through.
    internal static Dictionary<string, string> BuildOpenVinoOptions(OnnxSessionCreateSpec spec)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_type"] = spec.Device,
        };
        if (IsGpu(spec.Device))
            opts["precision"] = "FP32";
        if (!string.IsNullOrWhiteSpace(spec.CacheDir))
            opts["cache_dir"] = spec.CacheDir!;
        if (!IsGpu(spec.Device) && spec.IntraOpThreads is > 0)
            opts["num_of_threads"] = spec.IntraOpThreads.Value.ToString();
        return opts;
    }

    private static bool IsGpu(string? device) =>
        device is not null && device.Trim().StartsWith("GPU", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalPath(string p) =>
        string.IsNullOrWhiteSpace(p) ? p : Path.GetFullPath(p);

    private static string? CanonicalDir(string? d) =>
        string.IsNullOrWhiteSpace(d) ? null : Path.GetFullPath(d.Trim());

    private static bool LooksLikeDeviceFailure(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("device", StringComparison.OrdinalIgnoreCase)
            || m.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            || m.Contains("CL_", StringComparison.Ordinal);
    }

    // Managed Microsoft.ML.OnnxRuntime major.minor must equal the native core's
    // (parsed from libonnxruntime.so.<ver>). Reading the managed assembly version
    // and the file name loads NO native symbols.
    private static bool AbiMatches(string coreFile)
    {
        var native = ParseCoreVersion(coreFile);
        var managed = typeof(InferenceSession).Assembly.GetName().Version;
        if (native is null || managed is null) return false;
        return native.Major == managed.Major && native.Minor == managed.Minor;
    }

    private static Version? ParseCoreVersion(string coreFile)
    {
        var name = Path.GetFileName(coreFile);
        var i = name.IndexOf(".so.", StringComparison.Ordinal);
        return i >= 0 && Version.TryParse(name[(i + 4)..], out var v) ? v : null;
    }

    private static bool OpenVinoProviderAvailable()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders().Contains("OpenVINOExecutionProvider");
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Dispose the DUAL dispatchers (scheduling primitives only) before their
        // backing sessions; a throwing Dispose must not stop the others.
        foreach (var lazy in _dualPools.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { lazy.Value.Dispose(); }
            catch (Exception ex) { _logger.LogWarning("ONNX DUAL pool dispose failed: {Type}", ex.GetType().Name); }
        }
        _dualPools.Clear();
        // Dispose every successfully-created session exactly once; a throwing
        // Dispose on one entry must not prevent the others from being disposed.
        foreach (var lazy in _cache.Values)
        {
            if (!lazy.IsValueCreated) continue; // never-evaluated / failed init
            try { lazy.Value.Dispose(); }
            catch (Exception ex) { _logger.LogWarning("ONNX session dispose failed: {Type}", ex.GetType().Name); }
        }
        _cache.Clear();
    }

    // Gate 3B: a shared, non-exclusive lease. ORT sessions are safe for concurrent
    // Run, so releasing the lease does not dispose the cached session. A later gate
    // turns this into an exclusive lease for DUAL executors.
    private sealed class SharedLease(IOnnxSession session) : IOnnxSessionLease
    {
        public IOnnxSession Session { get; } = session;
        public void Dispose() { }
    }

    // One device leg of a DUAL placement: a single-device session plus its device
    // label (diagnostics only). The session is owned by the factory cache.
    internal readonly record struct DualExecutor(IOnnxSession Session, string Device);

    // Bounded EXCLUSIVE dispatcher over N single-device executors (2 for CPU+GPU, or
    // 1 when the GPU leg degraded). Acquire blocks until an executor is free and
    // hands it out exclusively, so two concurrent image inferences land on two
    // DISTINCT devices — reproducing the benchmarked bounded CPU+GPU tandem. The
    // executor is returned to the pool on lease Dispose (idempotent), so a caller
    // that timed out or threw still frees its device. The pool owns only its
    // scheduling primitives; the sessions are disposed by the factory cache.
    internal sealed class DualSessionPool : IDisposable
    {
        private readonly SemaphoreSlim _available;
        private readonly ConcurrentQueue<DualExecutor> _idle;

        public DualSessionPool(IReadOnlyCollection<DualExecutor> executors)
        {
            if (executors is null || executors.Count == 0)
                throw new ArgumentException("A DUAL pool needs at least one executor.", nameof(executors));
            _idle = new ConcurrentQueue<DualExecutor>(executors);
            _available = new SemaphoreSlim(executors.Count, executors.Count);
            Count = executors.Count;
        }

        // Number of device executors (2 = CPU+GPU tandem, 1 = GPU-degraded CPU-only).
        public int Count { get; }

        public IOnnxSessionLease Acquire()
        {
            _available.Wait();
            if (!_idle.TryDequeue(out var exec))
            {
                _available.Release();
                throw new InvalidOperationException("DUAL pool invariant violated: no idle executor after semaphore.");
            }
            return new DualLease(this, exec);
        }

        private void Return(DualExecutor exec)
        {
            _idle.Enqueue(exec);
            _available.Release();
        }

        public void Dispose() => _available.Dispose(); // sessions disposed by the cache

        private sealed class DualLease(DualSessionPool pool, DualExecutor exec) : IOnnxSessionLease
        {
            private int _disposed;
            public IOnnxSession Session { get; } = exec.Session;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) pool.Return(exec);
            }
        }
    }
}

// Concrete session spec after option resolution. `Key` is the typed cache identity.
internal readonly record struct OnnxSessionCreateSpec(
    string ModelPath, string Provider, string Device, string Precision,
    int? IntraOpThreads, string? CacheDir, string? NativeDir)
{
    public OnnxSessionCacheKey Key =>
        new(ModelPath, Provider, Device, Precision, IntraOpThreads, CacheDir);
}

// Typed, immutable, structurally-compared cache key. NativeDir is intentionally
// excluded (process-global, constant); every session-affecting setting is present.
internal readonly record struct OnnxSessionCacheKey(
    string ModelPath, string Provider, string Device, string Precision,
    int? IntraOpThreads, string? CacheDir);

// Real InferenceSession adapter. Materializes outputs to float[] and disposes the
// native result collection so no ORT-native disposable escapes the factory.
internal sealed class OrtOnnxSession(InferenceSession session) : IOnnxSession
{
    public IReadOnlyList<string> InputNames { get; } = session.InputMetadata.Keys.ToArray();

    public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        using var results = session.Run(inputs);
        var outputs = new List<OnnxOutputTensor>(results.Count);
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            outputs.Add(new OnnxOutputTensor(r.Name, t.ToArray(), t.Dimensions.ToArray()));
        }
        return outputs;
    }

    public void Dispose() => session.Dispose();
}

// The process-global ONNX Runtime native core selection. Set exactly once, on the
// first ORT native call, and IMMUTABLE thereafter — switching providers requires a
// process restart (ExecutionProvider is bound at startup via IOptions).
public enum OnnxNativeCoreState { Uninitialized, StockCpuCore, OpenVinoCore, Failed }

// Installs, at most once and concurrency-safely, the supported NativeLibrary
// resolver mapping the ORT P/Invoke ("onnxruntime.dll" literal) to an EXPLICIT,
// application-relative, ELF-verified core file — the stock CPU core (onnxruntime /
// openvino-sidecar) or the OpenVINO-enabled core (openvino-direct). ORT 1.24.1
// cannot self-resolve its native by name on Linux, so this is required for every
// in-process provider. Never selects a Windows PE (see docs/openvino-direct-native.md).
// The install action is injectable so unit tests verify behavior without the
// process-global side effect.
internal sealed class OnnxOpenVinoNativeResolver
{
    private readonly Action<string> _install;
    private readonly object _gate = new();
    private OnnxNativeCoreState _state = OnnxNativeCoreState.Uninitialized;
    private string? _installedCore;

    // Number of times the underlying install action actually ran (tests assert 1).
    public int InstallCount;

    public OnnxNativeCoreState State { get { lock (_gate) { return _state; } } }

    public OnnxOpenVinoNativeResolver(Action<string>? install = null) =>
        _install = install ?? InstallReal;

    // Installs the OpenVINO-enabled core discovered in the native dir.
    public bool EnsureInstalled(string? nativeDir) =>
        EnsureInstalledCore(FindCore(nativeDir), OnnxNativeCoreState.OpenVinoCore);

    // Installs the resolver mapping the ORT P/Invoke to an explicit ELF core file,
    // recording the selected core KIND. The process-global core is immutable: a
    // later call with a DIFFERENT core is REJECTED (returns false, never swaps).
    // Blocks concurrent callers until the first install completes, so no ORT load
    // can precede a partial install and concurrent startup cannot pick two cores.
    public bool EnsureInstalledCore(string? core, OnnxNativeCoreState kind)
    {
        if (string.IsNullOrWhiteSpace(core)) return false;
        lock (_gate)
        {
            if (_state is OnnxNativeCoreState.StockCpuCore or OnnxNativeCoreState.OpenVinoCore)
                return string.Equals(_installedCore, core, StringComparison.Ordinal);
            // Verify a real ELF before loading — never select a Windows PE on Linux
            // and fail closed on a truncated / wrong-format file.
            if (!File.Exists(core) || !IsElf(core)) { _state = OnnxNativeCoreState.Failed; return false; }
            try { _install(core); }
            catch { _state = OnnxNativeCoreState.Failed; return false; }
            _installedCore = core;
            _state = kind;
            InstallCount++;
            return true;
        }
    }

    // The OpenVINO-enabled core (libonnxruntime.so.<ver>) in the pinned native dir.
    internal static string? FindCore(string? nativeDir)
    {
        if (string.IsNullOrWhiteSpace(nativeDir) || !Directory.Exists(nativeDir)) return null;
        return Directory.EnumerateFiles(nativeDir, "libonnxruntime.so.*")
            .OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
    }

    // The stock CPU ORT core, via a DETERMINISTIC application-relative search only
    // (never a broad filesystem scan): the framework-dependent publish layout
    // runtimes/<rid>/native, then a flattened app-root fallback.
    internal static string? FindStockCore()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var rid in new[] { "linux-x64", "linux-arm64" })
        {
            var p = Path.Combine(baseDir, "runtimes", rid, "native", "libonnxruntime.so");
            if (File.Exists(p)) return p;
        }
        var flat = Path.Combine(baseDir, "libonnxruntime.so");
        return File.Exists(flat) ? flat : null;
    }

    internal static bool IsElf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4
                && magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F';
        }
        catch { return false; }
    }

    private static void InstallReal(string core)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, (name, _, _) =>
                name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                && NativeLibrary.TryLoad(core, out var handle) ? handle : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for the assembly in this process.
        }
    }
}

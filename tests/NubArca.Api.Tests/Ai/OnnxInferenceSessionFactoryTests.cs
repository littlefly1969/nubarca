using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Tests.Ai;

// Gate 3B: session factory caching / single-flight / retry-on-failure / disposal /
// sanitized readiness / ABI fail-closed. The native session creator and the
// resolver installer are faked, so these run without the OpenVINO native stack,
// without any model weights, and without the process-global NativeLibrary side
// effect.
public sealed class OnnxInferenceSessionFactoryTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    private string TempModel()
    {
        var p = Path.GetTempFileName(); // exists on disk
        _tempFiles.Add(p);
        return p;
    }

    // Temp dir containing a dummy "libonnxruntime.so.<version>" core file.
    private string TempNativeDir(string coreVersion)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ovnat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Valid ELF magic (0x7F "ELF") so the resolver's ELF verification accepts it.
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        File.WriteAllBytes(Path.Combine(dir, $"libonnxruntime.so.{coreVersion}"), elf);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { /* best effort */ } }
        foreach (var d in _tempDirs) { try { Directory.Delete(d, true); } catch { /* best effort */ } }
    }

    private static IOptions<AiOptions> OnnxRuntimeOptions()
        => Options.Create(new AiOptions { Onnx = { ExecutionProvider = "onnxruntime" } });

    private static IOptions<AiOptions> DirectOptions(string nativeDir)
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = nativeDir;
        return Options.Create(o);
    }

    // No-op resolver installer so factory tests never touch the process-global
    // NativeLibrary registration.
    private static OnnxInferenceSessionFactory Factory(
        IOptions<AiOptions> options, Func<OnnxSessionCreateSpec, IOnnxSession> creator) =>
        new(options, NullLogger<OnnxInferenceSessionFactory>.Instance, creator, installResolver: _ => { });

    private sealed class FakeSession : IOnnxSession
    {
        public int DisposeCount;
        public bool ThrowOnDispose;
        public IReadOnlyList<string> InputNames => Array.Empty<string>();
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
            => throw new NotSupportedException();
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            if (ThrowOnDispose) throw new InvalidOperationException("dispose boom");
        }
    }

    // ---- caching / identity ----

    [Fact]
    public void Acquire_Caches_Session_By_Identity()
    {
        var model = TempModel();
        var calls = 0;
        using var factory = Factory(OnnxRuntimeOptions(), _ => { Interlocked.Increment(ref calls); return new FakeSession(); });
        var spec = new OnnxModelSpec(OnnxModel.FaceRecognizer, model);

        using var a = factory.Acquire(spec);
        using var b = factory.Acquire(spec);

        Assert.Same(a.Session, b.Session);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Acquire_Caches_By_Canonical_Path()
    {
        var model = TempModel();
        var noncanonical = Path.Combine(Path.GetDirectoryName(model)!, ".", Path.GetFileName(model));
        var calls = 0;
        using var factory = Factory(OnnxRuntimeOptions(), _ => { Interlocked.Increment(ref calls); return new FakeSession(); });

        using var a = factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, model));
        using var b = factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, noncanonical));

        Assert.Same(a.Session, b.Session); // "/x/./m" and "/x/m" resolve to one key
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Acquire_Distinct_Models_Create_Distinct_Sessions()
    {
        var calls = 0;
        using var factory = Factory(OnnxRuntimeOptions(), _ => { Interlocked.Increment(ref calls); return new FakeSession(); });

        using var a = factory.Acquire(new OnnxModelSpec(OnnxModel.FaceRecognizer, TempModel()));
        using var b = factory.Acquire(new OnnxModelSpec(OnnxModel.FaceDetector, TempModel()));

        Assert.NotSame(a.Session, b.Session);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Acquire_Is_Single_Flight_Under_Concurrency()
    {
        var model = TempModel();
        var calls = 0;
        using var factory = Factory(OnnxRuntimeOptions(), _ =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(40); // widen the race window
            return new FakeSession();
        });
        var spec = new OnnxModelSpec(OnnxModel.PhotoImage, model);

        var leases = new IOnnxSessionLease[16];
        Parallel.For(0, leases.Length, i => leases[i] = factory.Acquire(spec));

        Assert.Equal(1, calls);
        Assert.All(leases, l => Assert.Same(leases[0].Session, l.Session));
        foreach (var l in leases) l.Dispose();
    }

    // ---- retry on failed initialization ----

    [Fact]
    public void Failed_Init_Is_Evicted_And_Retried()
    {
        var model = TempModel();
        var calls = 0;
        FakeSession? good = null;
        using var factory = Factory(OnnxRuntimeOptions(), _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("first fails");
            good = new FakeSession();
            return good;
        });
        var spec = new OnnxModelSpec(OnnxModel.PhotoText, model);

        Assert.Throws<OnnxSessionUnavailableException>(() => factory.Acquire(spec)); // 1st fails
        using var lease = factory.Acquire(spec);                                     // 2nd retries + succeeds

        Assert.Equal(2, calls);
        Assert.Same(good, lease.Session);
    }

    [Fact]
    public void Concurrent_Failing_Init_Does_Not_Storm()
    {
        var model = TempModel();
        var calls = 0;
        using var factory = Factory(OnnxRuntimeOptions(), _ =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(20);
            throw new InvalidOperationException("always fails");
        });
        var spec = new OnnxModelSpec(OnnxModel.FaceDetector, model);

        var thrown = 0;
        Parallel.For(0, 16, _ =>
        {
            try { factory.Acquire(spec); }
            catch (OnnxSessionUnavailableException) { Interlocked.Increment(ref thrown); }
        });

        Assert.Equal(16, thrown);              // every caller fails closed
        Assert.InRange(calls, 1, 16);          // single-flight per wave — bounded, no storm
    }

    // ---- disposal ----

    [Fact]
    public void Dispose_Disposes_Cached_Session_Once_And_Blocks_Reacquire()
    {
        var model = TempModel();
        var fake = new FakeSession();
        var factory = Factory(OnnxRuntimeOptions(), _ => fake);
        var spec = new OnnxModelSpec(OnnxModel.PhotoText, model);
        factory.Acquire(spec).Dispose();

        factory.Dispose();
        factory.Dispose(); // idempotent

        Assert.Equal(1, fake.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => factory.Acquire(spec));
    }

    [Fact]
    public void Dispose_Disposes_Each_Session_Once_Even_If_One_Throws()
    {
        var created = new List<FakeSession>();
        var factory = Factory(OnnxRuntimeOptions(), _ =>
        {
            var s = new FakeSession();
            lock (created) created.Add(s);
            return s;
        });
        factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel()));
        factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoText, TempModel()));
        factory.Acquire(new OnnxModelSpec(OnnxModel.FaceDetector, TempModel()));
        created[1].ThrowOnDispose = true; // one entry throws on dispose

        factory.Dispose();

        Assert.Equal(3, created.Count);
        Assert.All(created, s => Assert.Equal(1, s.DisposeCount)); // all disposed once despite the throw
    }

    [Fact]
    public void Concurrent_Dispose_And_Acquire_Never_Returns_After_Dispose()
    {
        var model = TempModel();
        var factory = Factory(OnnxRuntimeOptions(), _ => new FakeSession());
        var spec = new OnnxModelSpec(OnnxModel.PhotoImage, model);
        factory.Acquire(spec); // pre-warm

        var unexpected = 0;
        Parallel.Invoke(
            () => factory.Dispose(),
            () => Parallel.For(0, 12, _ =>
            {
                try { using var l = factory.Acquire(spec); }
                catch (ObjectDisposedException) { /* expected once disposed */ }
                catch { Interlocked.Increment(ref unexpected); }
            }));

        Assert.Equal(0, unexpected); // only ObjectDisposedException may surface
        Assert.Throws<ObjectDisposedException>(() => factory.Acquire(spec)); // authoritative post-state
    }

    // ---- readiness / sanitized errors ----

    [Fact]
    public void Missing_Model_Is_NotReady_And_Acquire_Throws_Sanitized()
    {
        using var factory = Factory(OnnxRuntimeOptions(), _ => new FakeSession());
        var spec = new OnnxModelSpec(OnnxModel.PhotoImage, "/no/such/model.onnx");

        var readiness = factory.CheckReadiness(spec);
        Assert.False(readiness.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonModelNotFound, readiness.Reason);

        var ex = Assert.Throws<OnnxSessionUnavailableException>(() => factory.Acquire(spec));
        Assert.Equal(OnnxInferenceSessionFactory.ReasonModelNotFound, ex.ReasonCode);
    }

    [Fact]
    public void Direct_Without_Native_Is_NotReady()
    {
        using var factory = Factory(DirectOptions("/nonexistent-native-dir"), _ => new FakeSession());

        var readiness = factory.CheckReadiness(new OnnxModelSpec(OnnxModel.FaceRecognizer, TempModel()));
        Assert.False(readiness.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonNativeMissing, readiness.Reason);
    }

    [Fact]
    public void Direct_Abi_Mismatch_Is_NotReady_FailClosed()
    {
        // Native core 9.9.9 vs the managed Microsoft.ML.OnnxRuntime version → fail closed.
        using var factory = Factory(DirectOptions(TempNativeDir("9.9.9")), _ => new FakeSession());

        var readiness = factory.CheckReadiness(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel()));
        Assert.False(readiness.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonAbiMismatch, readiness.Reason);
    }

    [Fact]
    public void Direct_Matching_Abi_Without_Runtime_Is_Provider_Unavailable()
    {
        // Core whose version matches the managed ABI, but no OpenVINO EP is present
        // in the test environment → provider-unavailable (never a false "ready").
        var mv = typeof(InferenceSession).Assembly.GetName().Version!;
        using var factory = Factory(DirectOptions(TempNativeDir($"{mv.Major}.{mv.Minor}.{mv.Build}")), _ => new FakeSession());

        var readiness = factory.CheckReadiness(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel()));
        Assert.False(readiness.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonProviderUnavailable, readiness.Reason);
    }

    [Fact]
    public void Acquire_Sanitizes_Raw_Creator_Exception()
    {
        using var factory = Factory(OnnxRuntimeOptions(),
            _ => throw new InvalidOperationException("compile failed at /srv/ai-models/secret/model.onnx"));
        var ex = Assert.Throws<OnnxSessionUnavailableException>(
            () => factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel())));

        Assert.Equal(OnnxInferenceSessionFactory.ReasonCompileFailed, ex.ReasonCode);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("/srv/", ex.Message);
    }

    [Fact]
    public void Acquire_Maps_DllNotFound_To_Native_Unavailable()
    {
        using var factory = Factory(OnnxRuntimeOptions(), _ => throw new DllNotFoundException("libonnxruntime.so"));
        var ex = Assert.Throws<OnnxSessionUnavailableException>(
            () => factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel())));
        Assert.Equal(OnnxInferenceSessionFactory.ReasonNativeUnavailable, ex.ReasonCode);
    }

    [Fact]
    public void Acquire_Propagates_Sanitized_Device_Unavailable()
    {
        using var factory = Factory(OnnxRuntimeOptions(),
            _ => throw new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable));
        var ex = Assert.Throws<OnnxSessionUnavailableException>(
            () => factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, TempModel())));
        Assert.Equal(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, ex.ReasonCode);
    }

    // ---- FP32 GPU / provider-option assembly (pure) ----

    [Fact]
    public void OpenVino_Gpu_Options_Force_Fp32()
    {
        var spec = new OnnxSessionCreateSpec("/m.onnx", OnnxExecutionProviders.OpenVinoDirect, "GPU", "FP32", null, null, "/n");
        var opts = OnnxInferenceSessionFactory.BuildOpenVinoOptions(spec);
        Assert.Equal("GPU", opts["device_type"]);
        Assert.Equal("FP32", opts["precision"]);
    }

    [Fact]
    public void OpenVino_Cpu_Options_Have_No_Precision_And_Pass_Threads()
    {
        var spec = new OnnxSessionCreateSpec("/m.onnx", OnnxExecutionProviders.OpenVinoDirect, "CPU", "FP32", 6, "/cache", "/n");
        var opts = OnnxInferenceSessionFactory.BuildOpenVinoOptions(spec);
        Assert.Equal("CPU", opts["device_type"]);
        Assert.False(opts.ContainsKey("precision"));
        Assert.Equal("6", opts["num_of_threads"]);
        Assert.Equal("/cache", opts["cache_dir"]);
    }

    // ---- resolver idempotency (concurrent) ----

    [Fact]
    public void Resolver_Installs_Once_Under_Concurrency()
    {
        var dir = TempNativeDir("1.24.1");
        var installs = 0;
        var resolver = new OnnxOpenVinoNativeResolver(_ => Interlocked.Increment(ref installs));

        Parallel.For(0, 32, _ => resolver.EnsureInstalled(dir));

        Assert.Equal(1, installs);
        Assert.Equal(1, resolver.InstallCount);
    }

    [Fact]
    public void Resolver_Is_NoOp_When_Core_Absent()
    {
        var installs = 0;
        var resolver = new OnnxOpenVinoNativeResolver(_ => installs++);

        Assert.False(resolver.EnsureInstalled("/no/such/native/dir"));
        Assert.Equal(0, installs);
        Assert.Equal(OnnxNativeCoreState.Uninitialized, resolver.State);
    }

    private string ElfFile(string version = "1.24.1")
        => Path.Combine(TempNativeDir(version), $"libonnxruntime.so.{version}");

    private string PeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); _tempDirs.Add(dir);
        var pe = new byte[64]; pe[0] = (byte)'M'; pe[1] = (byte)'Z'; // Windows PE magic
        var p = Path.Combine(dir, "onnxruntime.dll");
        File.WriteAllBytes(p, pe);
        return p;
    }

    [Fact]
    public void FindStockCore_Uses_Deterministic_AppRelative_Elf()
    {
        // The test build is a normal framework-dependent layout, so the ORT NuGet
        // native lives under runtimes/<rid>/native — found via the app-relative
        // policy (no broad scan) and it is a real ELF, never a PE.
        var core = OnnxOpenVinoNativeResolver.FindStockCore();
        Assert.NotNull(core);
        Assert.EndsWith("libonnxruntime.so", core);
        Assert.True(OnnxOpenVinoNativeResolver.IsElf(core!));
    }

    [Fact]
    public void Resolver_Rejects_Windows_PE_And_Fails_Closed()
    {
        var installs = 0;
        var resolver = new OnnxOpenVinoNativeResolver(_ => installs++);

        Assert.False(resolver.EnsureInstalledCore(PeFile(), OnnxNativeCoreState.StockCpuCore));
        Assert.Equal(0, installs);                              // never loaded a PE
        Assert.Equal(OnnxNativeCoreState.Failed, resolver.State);
    }

    [Fact]
    public void Resolver_Selects_Kind_And_Is_Immutable_After_Init()
    {
        var installs = 0;
        var resolver = new OnnxOpenVinoNativeResolver(_ => installs++);
        var coreA = ElfFile();
        var coreB = ElfFile("1.24.2");

        Assert.True(resolver.EnsureInstalledCore(coreA, OnnxNativeCoreState.OpenVinoCore));
        Assert.Equal(OnnxNativeCoreState.OpenVinoCore, resolver.State);
        Assert.True(resolver.EnsureInstalledCore(coreA, OnnxNativeCoreState.OpenVinoCore)); // same core → ok
        Assert.False(resolver.EnsureInstalledCore(coreB, OnnxNativeCoreState.StockCpuCore)); // different → rejected
        Assert.Equal(1, installs);                                 // installed exactly once
        Assert.Equal(OnnxNativeCoreState.OpenVinoCore, resolver.State); // unchanged
    }
}

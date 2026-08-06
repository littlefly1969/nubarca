using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Tests.Ai;

// Gate 3C: startup ABI/conflict guard + version diagnostics. Hermetic — a fake
// factory means no ORT native load and no process-global resolver registration.
public sealed class OnnxDirectRuntimeInitializerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempNativeDir(string coreVersion)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ovinit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"libonnxruntime.so.{coreVersion}"), "x");
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs) { try { Directory.Delete(d, true); } catch { /* best effort */ } }
    }

    private sealed class NoopFactory : IOnnxInferenceSessionFactory
    {
        public int InitCount;
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;
        public IOnnxSessionLease Acquire(OnnxModelSpec spec) => throw new NotSupportedException();
        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.StockCpuCore;
        public void Dispose() { }
    }

    private static OnnxDirectRuntimeInitializer Init(AiOptions options, IOnnxInferenceSessionFactory factory) =>
        new(Options.Create(options), factory, NullLogger<OnnxDirectRuntimeInitializer>.Instance);

    [Fact]
    public async Task OnnxRuntime_Provider_Installs_Resolver()
    {
        var factory = new NoopFactory();
        var init = Init(new AiOptions { Onnx = { ExecutionProvider = "onnxruntime" } }, factory);

        await init.StartAsync(CancellationToken.None); // must not throw

        Assert.Equal(1, factory.InitCount); // stock-core resolver install attempted
    }

    [Fact]
    public async Task Direct_With_Abi_Mismatch_Fails_Closed_At_Startup()
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = TempNativeDir("9.9.9"); // != managed ORT major.minor
        var factory = new NoopFactory();
        var init = Init(o, factory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => init.StartAsync(CancellationToken.None));
        Assert.Contains("ABI mismatch", ex.Message);
        Assert.Equal(1, factory.InitCount); // resolver install attempted before the guard
    }

    [Fact]
    public async Task Direct_With_Missing_Native_Fails_Closed_At_Startup()
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = "/no/such/native/dir";
        var init = Init(o, new NoopFactory());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => init.StartAsync(CancellationToken.None));
        Assert.Contains("native-missing", ex.Message);
    }

    [Fact]
    public void GatherInfo_Reports_Sanitized_Versions()
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = TempNativeDir("9.9.9");
        var info = Init(o, new NoopFactory()).GatherInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.ManagedOrtVersion));
        Assert.Equal("9.9.9", info.NativeCoreVersion);
        Assert.Equal("openvino-direct", info.ConfiguredProvider);
        Assert.False(info.AbiMatches); // 9.9 != managed
    }
}

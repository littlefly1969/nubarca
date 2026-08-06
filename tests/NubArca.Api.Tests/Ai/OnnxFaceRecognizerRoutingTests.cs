using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Domain.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// Gate 3C: provider routing for the migrated face RECOGNIZER (glintr100). Fakes
// only — no GPU, no model weights, no HTTP. Proves:
//   onnxruntime / openvino-direct → IOnnxInferenceSessionFactory (in-process)
// no silent fallback, factory-owned session, lease disposed, normalization/dim
// checks retained, cancellation, and compile-backed readiness.
public sealed class OnnxFaceRecognizerRoutingTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facemodels-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, "antelopev2");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "scrfd_10g_bnkps.onnx"), "x");
        File.WriteAllText(Path.Combine(sub, "glintr100.onnx"), "x");
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs) { try { Directory.Delete(d, true); } catch { /* best effort */ } }
    }

    private static byte[] TinyCrop()
    {
        using var img = new Image<Rgb24>(112, 112);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static AiProfile Antelopev2Profile() => new()
    {
        Id = Guid.NewGuid(),
        Key = OnnxFaceModels.Antelopev2ProfileKey,
        ConfigHash = OnnxFaceModels.Antelopev2Key,
        Capability = AiCapabilities.FaceEmbedding,
        Modality = AiModalities.Face,
        Dimension = 512,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    private OnnxFaceBackend Build(string provider, FakeFactory factory, string? modelDir = null)
    {
        var onnx = new AiOnnxOptions { ModelDir = modelDir ?? TempModelDir(), ExecutionProvider = provider };
        if (provider == "openvino-direct") onnx.OpenVino.NativeDir = "/opt/ort";
        var options = Options.Create(new AiOptions { Onnx = onnx });
        return new OnnxFaceBackend(options, new OnnxFacePreprocessor(), NullLogger<OnnxFaceBackend>.Instance, factory);
    }

    // ---- fakes ----

    private sealed class FakeFactory : IOnnxInferenceSessionFactory
    {
        public int AcquireCount, InitCount, LeaseDisposeCount;
        public OnnxModel? LastModel;
        public Exception? AcquireThrows;
        public readonly FakeSession Session = new();
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;
        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
        {
            Interlocked.Increment(ref AcquireCount);
            LastModel = spec.Model;
            if (AcquireThrows is not null) throw AcquireThrows;
            return new Lease(this);
        }
        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.OpenVinoCore;
        public void Dispose() { }
        private sealed class Lease(FakeFactory f) : IOnnxSessionLease
        {
            public IOnnxSession Session => f.Session;
            public void Dispose() => Interlocked.Increment(ref f.LeaseDisposeCount);
        }
    }

    private sealed class FakeSession : IOnnxSession
    {
        public int DisposeCount;
        public float[] Output = NormalizableVector();
        public IReadOnlyList<string> InputNames => new[] { "input.1" };
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
            => new[] { new OnnxOutputTensor("683", Output, new[] { 1, Output.Length }) };
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static float[] NormalizableVector()
    {
        var v = new float[512];
        v[0] = 3f; v[1] = 4f; // ||v|| = 5 → normalizes to unit length
        return v;
    }

    // ---- routing ----

    [Fact]
    public async Task OnnxRuntime_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);

        var result = await backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile());

        Assert.Equal(512, result.Dimension);
        Assert.Equal(1, factory.AcquireCount);
        Assert.Equal(OnnxModel.FaceRecognizer, factory.LastModel);
        Assert.True(factory.InitCount >= 1);
        Assert.Equal(1, factory.LeaseDisposeCount);  // lease disposed after inference
        Assert.Equal(0, factory.Session.DisposeCount); // shared session NOT disposed per request
    }

    [Fact]
    public async Task Direct_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        var backend = Build("openvino-direct", factory);

        var result = await backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile());

        Assert.Equal(512, result.Dimension);
        Assert.Equal(1, factory.AcquireCount);
        Assert.True(factory.InitCount >= 1);
    }

    [Fact]
    public async Task Direct_Failure_Does_Not_Silently_Fall_Back()
    {
        var factory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var backend = Build("openvino-direct", factory);

        var ex = await Assert.ThrowsAsync<OnnxSessionUnavailableException>(
            () => backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile()));

        Assert.Equal(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, ex.ReasonCode);
        Assert.Equal(1, factory.AcquireCount);
    }

    [Fact]
    public async Task Session_Reused_And_Lease_Disposed_Across_Calls()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);
        var profile = Antelopev2Profile();
        var crop = TinyCrop();

        await backend.EmbedFaceAsync(crop, profile);
        await backend.EmbedFaceAsync(crop, profile);

        Assert.Equal(2, factory.AcquireCount);
        Assert.Equal(2, factory.LeaseDisposeCount);
        Assert.Equal(0, factory.Session.DisposeCount); // backend never disposes the shared session
    }

    // ---- output contracts ----

    [Fact]
    public async Task Embedding_Is_L2_Normalized()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);

        var result = await backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile());

        var norm = Math.Sqrt(result.Vector.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);
        Assert.All(result.Vector, x => Assert.False(float.IsNaN(x) || float.IsInfinity(x)));
    }

    [Fact]
    public async Task Dimension_Mismatch_Is_Rejected()
    {
        var factory = new FakeFactory();
        factory.Session.Output = new float[256]; // wrong dim vs profile 512
        var backend = Build("onnxruntime", factory);

        await Assert.ThrowsAsync<ArgumentException>(() => backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile()));
    }

    [Fact]
    public async Task Cancellation_Propagates_Before_Inference()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backend.EmbedFaceAsync(TinyCrop(), Antelopev2Profile(), cts.Token));
        Assert.Equal(0, factory.AcquireCount);
    }

    // ---- compile-backed readiness ----

    [Fact]
    public void Readiness_Direct_NotReady_When_Compilation_Fails()
    {
        var factory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonCompileFailed),
        };
        var backend = Build("openvino-direct", factory);

        var readiness = backend.CheckReadiness(Antelopev2Profile());

        Assert.False(readiness.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonCompileFailed, readiness.Reason);
        Assert.Equal(1, factory.AcquireCount); // readiness actually compiled
    }

    [Fact]
    public void Readiness_Direct_Ready_When_Compilation_Succeeds()
    {
        var factory = new FakeFactory();
        var backend = Build("openvino-direct", factory);

        var readiness = backend.CheckReadiness(Antelopev2Profile());

        Assert.True(readiness.IsReady);
        // Face AI milestone: direct-mode readiness now compile-checks BOTH the
        // detector AND the recognizer, so a healthy result means both models
        // actually compiled for their configured devices.
        Assert.Equal(2, factory.AcquireCount);
        Assert.Equal(OnnxModel.FaceRecognizer, factory.LastModel); // recognizer checked last
        Assert.True(factory.InitCount >= 1);
    }

    [Fact]
    public void Readiness_OnnxRuntime_Does_Not_Compile()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);

        var readiness = backend.CheckReadiness(Antelopev2Profile());

        Assert.True(readiness.IsReady);
        Assert.Equal(0, factory.AcquireCount); // file-check only for CPU
    }
}

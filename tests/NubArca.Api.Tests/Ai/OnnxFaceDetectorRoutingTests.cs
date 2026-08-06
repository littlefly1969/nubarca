using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Domain.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// Face AI milestone: provider routing for the migrated face DETECTOR (SCRFD) and
// the COMPLETE detect→decode→align→recognize→normalize pipeline. Fakes only — no
// GPU, no model weights, no HTTP. Proves:
//   onnxruntime / openvino-direct → IOnnxInferenceSessionFactory (in-process)
// no silent fallback, factory-owned session (FaceDetector spec), lease disposed &
// reused, cancellation, and the full two-model pipeline routed entirely in-process.
public sealed class OnnxFaceDetectorRoutingTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "facedet-" + Guid.NewGuid().ToString("N"));
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

    // A small RGB image; PrepareDetection letterboxes it to the 640 detector input.
    private static byte[] TinyImage(int w = 200, int h = 200)
    {
        using var img = new Image<Rgb24>(w, h, new Rgb24(120, 130, 140));
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

    // ---- SCRFD 640 synthetic outputs (one anchor per cell) --------------------
    // Builds a full 3-stride SCRFD output set for a 640 input with `count` hot
    // detections placed in the stride-8 branch (distinct, face-shaped landmarks so
    // alignment is non-degenerate). Everything else is zero → below threshold.
    private static IReadOnlyList<OnnxOutputTensor> Scrfd640(params (int row, int col, float score)[] hits)
    {
        var outputs = new List<OnnxOutputTensor>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var grid = 640 / stride;
            var cells = grid * grid;
            var scores = new float[cells];
            var bbox = new float[cells * 4];
            var kps = new float[cells * 10];

            if (stride == 8)
            {
                foreach (var (row, col, score) in hits)
                {
                    var j = row * grid + col;
                    scores[j] = score;
                    // Box distances (stride units) → a 64px box around the cell center.
                    bbox[j * 4 + 0] = 4f; bbox[j * 4 + 1] = 4f; bbox[j * 4 + 2] = 4f; bbox[j * 4 + 3] = 4f;
                    // 5 distinct, non-collinear landmark deltas (eyes/nose/mouth).
                    float[] dx = { -2f, 2f, 0f, -1.5f, 1.5f };
                    float[] dy = { -2f, -2f, 0f, 2f, 2f };
                    for (var k = 0; k < 5; k++)
                    {
                        kps[j * 10 + k * 2] = dx[k];
                        kps[j * 10 + k * 2 + 1] = dy[k];
                    }
                }
            }

            outputs.Add(new OnnxOutputTensor($"score_{stride}", scores, new[] { cells, 1 }));
            outputs.Add(new OnnxOutputTensor($"bbox_{stride}", bbox, new[] { cells, 4 }));
            outputs.Add(new OnnxOutputTensor($"kps_{stride}", kps, new[] { cells, 10 }));
        }

        return outputs;
    }

    private static float[] NormalizableVector()
    {
        var v = new float[512];
        v[0] = 3f; v[1] = 4f; // ||v|| = 5 → unit length after normalization
        return v;
    }

    // ---- fakes ----

    private sealed class FakeFactory : IOnnxInferenceSessionFactory
    {
        public int InitCount, DetectorAcquireCount, RecognizerAcquireCount, LeaseDisposeCount;
        public readonly List<OnnxModel> Models = new();
        public Exception? DetectorThrows;
        public readonly FakeSession Detector = new();
        public readonly FakeSession Recognizer = new() { Outputs = new[] { new OnnxOutputTensor("emb", NormalizableVector(), new[] { 1, 512 }) } };

        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
        {
            lock (Models) Models.Add(spec.Model);
            if (spec.Model == OnnxModel.FaceDetector)
            {
                Interlocked.Increment(ref DetectorAcquireCount);
                if (DetectorThrows is not null) throw DetectorThrows;
                return new Lease(this, Detector);
            }

            Interlocked.Increment(ref RecognizerAcquireCount);
            return new Lease(this, Recognizer);
        }

        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.OpenVinoCore;
        public void Dispose() { }

        private sealed class Lease(FakeFactory f, FakeSession s) : IOnnxSessionLease
        {
            public IOnnxSession Session => s;
            public void Dispose() => Interlocked.Increment(ref f.LeaseDisposeCount);
        }
    }

    private sealed class FakeSession : IOnnxSession
    {
        public int DisposeCount;
        public IReadOnlyList<OnnxOutputTensor> Outputs = Array.Empty<OnnxOutputTensor>();
        public IReadOnlyList<string> InputNames => new[] { "input.1" };
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs) => Outputs;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    // ---- routing ----

    [Fact]
    public async Task OnnxRuntime_Detector_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((30, 30, 0.9f)) } };
        var backend = Build("onnxruntime", factory);

        var result = await backend.DetectFacesAsync(TinyImage(), Antelopev2Profile());

        Assert.Single(result.Faces);
        Assert.Equal(1, factory.DetectorAcquireCount);
        Assert.Contains(OnnxModel.FaceDetector, factory.Models);
        Assert.True(factory.InitCount >= 1);
        Assert.Equal(1, factory.LeaseDisposeCount); // lease released after inference
        Assert.Equal(0, factory.Detector.DisposeCount); // shared session NOT disposed per request
    }

    [Fact]
    public async Task Direct_Detector_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((30, 30, 0.9f)) } };
        var backend = Build("openvino-direct", factory);

        var result = await backend.DetectFacesAsync(TinyImage(), Antelopev2Profile());

        Assert.Single(result.Faces);
        Assert.Equal(1, factory.DetectorAcquireCount);
    }

    [Fact]
    public async Task Direct_Detector_Failure_Does_Not_Silently_Fall_Back()
    {
        var factory = new FakeFactory
        {
            DetectorThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var backend = Build("openvino-direct", factory);

        var ex = await Assert.ThrowsAsync<OnnxSessionUnavailableException>(
            () => backend.DetectFacesAsync(TinyImage(), Antelopev2Profile()));

        Assert.Equal(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, ex.ReasonCode);
    }

    [Fact]
    public async Task Detector_Session_Reused_And_Lease_Disposed_Across_Calls()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((30, 30, 0.9f)) } };
        var backend = Build("onnxruntime", factory);
        var profile = Antelopev2Profile();
        var img = TinyImage();

        await backend.DetectFacesAsync(img, profile);
        await backend.DetectFacesAsync(img, profile);

        Assert.Equal(2, factory.DetectorAcquireCount);
        Assert.Equal(2, factory.LeaseDisposeCount);
        Assert.Equal(0, factory.Detector.DisposeCount);
    }

    [Fact]
    public async Task Detector_Cancellation_Propagates_Before_Inference()
    {
        var factory = new FakeFactory();
        var backend = Build("onnxruntime", factory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backend.DetectFacesAsync(TinyImage(), Antelopev2Profile(), cts.Token));
        Assert.Equal(0, factory.DetectorAcquireCount);
    }

    // ---- complete pipeline: detect → decode → align → recognize → normalize ----

    [Fact]
    public async Task Pipeline_No_Face_Produces_No_Embedding()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640(/* no hits */) } };
        var backend = Build("onnxruntime", factory);

        var result = await backend.RunAsync(TinyImage(), Antelopev2Profile(), OnnxFaceBackend.EmbedMode.First, 0);

        Assert.Empty(result.Faces);
        Assert.Null(result.Embedding);
        Assert.Equal(0, factory.RecognizerAcquireCount); // recognition never attempted
    }

    [Fact]
    public async Task Pipeline_One_Face_Produces_One_Normalized_Embedding()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((30, 30, 0.9f)) } };
        var backend = Build("onnxruntime", factory);

        var result = await backend.RunAsync(TinyImage(), Antelopev2Profile(), OnnxFaceBackend.EmbedMode.First, 0);

        Assert.Single(result.Faces);
        Assert.NotNull(result.Embedding);
        Assert.Equal(512, result.EmbeddingDimension);
        var norm = Math.Sqrt(result.Embedding!.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);
        Assert.Equal(1, factory.DetectorAcquireCount);
        Assert.Equal(1, factory.RecognizerAcquireCount); // both models routed in-process
    }

    [Fact]
    public async Task Pipeline_Multiple_Faces_Preserve_Deterministic_Ordering()
    {
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((10, 10, 0.75f), (30, 30, 0.95f)) } };
        var backend = Build("onnxruntime", factory);
        var profile = Antelopev2Profile();
        var img = TinyImage();

        var a = await backend.RunAsync(img, profile, OnnxFaceBackend.EmbedMode.None, 0);
        var b = await backend.RunAsync(img, profile, OnnxFaceBackend.EmbedMode.None, 0);

        Assert.Equal(2, a.Faces.Count);
        // NMS orders by descending score → highest first, stable across runs.
        Assert.True(a.Faces[0].Score >= a.Faces[1].Score);
        Assert.Equal(a.Faces.Select(f => f.Score), b.Faces.Select(f => f.Score));
    }

    [Fact]
    public async Task Pipeline_Recognition_Failure_Produces_No_Partial_Output()
    {
        // Recognizer returns a wrong-dimension output → Finalize rejects it; the
        // pipeline throws rather than emitting a partial "valid-looking" embedding.
        var factory = new FakeFactory { Detector = { Outputs = Scrfd640((30, 30, 0.9f)) } };
        factory.Recognizer.Outputs = new[] { new OnnxOutputTensor("emb", new float[256], new[] { 1, 256 }) };
        var backend = Build("onnxruntime", factory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.RunAsync(TinyImage(), Antelopev2Profile(), OnnxFaceBackend.EmbedMode.First, 0));
    }
}

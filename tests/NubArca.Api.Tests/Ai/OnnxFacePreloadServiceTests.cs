using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;

namespace NubArca.Api.Tests.Ai;

// Face AI milestone: startup preload + compile-backed readiness for the direct
// face pipeline. Fakes only — no GPU, no weights. Proves the state machine
// (STARTING → … → READY / FAILED), that BOTH models are compiled once and
// synthetic-validated, fail-closed behavior (compile/validation/timeout), clean
// cancellation, sanitized readiness, and that non-direct providers stay READY.
public sealed class OnnxFacePreloadServiceTests
{
    private const int DetectorSize = 640;
    private const int RecognizerDim = 512;

    private static AiOptions DirectOptions(string modelDir) => new()
    {
        FaceProfileKey = OnnxFaceModels.Antelopev2ProfileKey,
        TimeoutSeconds = 30,
        Onnx = new AiOnnxOptions
        {
            ModelDir = modelDir,
            ExecutionProvider = OnnxExecutionProviders.OpenVinoDirect,
            OpenVino = new AiOnnxOpenVinoOptions { NativeDir = "/opt/ort" },
        },
    };

    private static string TempModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "antelopev2"));
        return dir;
    }

    private static OnnxFacePreloadService Service(AiOptions options, FakeFactory factory, OnnxFacePreloadState state) =>
        new(Options.Create(options), factory, state, NullLogger<OnnxFacePreloadService>.Instance);

    // Valid SCRFD-shaped detector outputs for a 640 input (single anchor/cell),
    // all below threshold → decodes to zero faces, which is a valid integrity pass.
    private static IReadOnlyList<OnnxOutputTensor> ValidDetectorOutputs()
    {
        var outputs = new List<OnnxOutputTensor>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cells = (DetectorSize / stride) * (DetectorSize / stride);
            outputs.Add(new OnnxOutputTensor($"s{stride}", new float[cells], new[] { cells, 1 }));
            outputs.Add(new OnnxOutputTensor($"b{stride}", new float[cells * 4], new[] { cells, 4 }));
            outputs.Add(new OnnxOutputTensor($"k{stride}", new float[cells * 10], new[] { cells, 10 }));
        }

        return outputs;
    }

    private static IReadOnlyList<OnnxOutputTensor> ValidRecognizerOutputs()
    {
        var v = new float[RecognizerDim];
        v[0] = 3f; v[1] = 4f; // norm 5 → unit after normalization
        return new[] { new OnnxOutputTensor("emb", v, new[] { 1, RecognizerDim }) };
    }

    private sealed class FakeFactory : IOnnxInferenceSessionFactory
    {
        public int InitCount, DetectorAcquireCount, RecognizerAcquireCount;
        public Exception? DetectorThrows, RecognizerThrows;
        public int AcquireSleepMs;
        public readonly FakeSession Detector = new() { Outputs = ValidDetectorOutputs() };
        public readonly FakeSession Recognizer = new() { Outputs = ValidRecognizerOutputs() };

        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
        {
            if (AcquireSleepMs > 0) Thread.Sleep(AcquireSleepMs);
            if (spec.Model == OnnxModel.FaceDetector)
            {
                Interlocked.Increment(ref DetectorAcquireCount);
                if (DetectorThrows is not null) throw DetectorThrows;
                return new Lease(Detector);
            }

            Interlocked.Increment(ref RecognizerAcquireCount);
            if (RecognizerThrows is not null) throw RecognizerThrows;
            return new Lease(Recognizer);
        }

        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.OpenVinoCore;
        public void Dispose() { }

        private sealed class Lease(FakeSession s) : IOnnxSessionLease
        {
            public IOnnxSession Session => s;
            public void Dispose() { }
        }
    }

    private sealed class FakeSession : IOnnxSession
    {
        public IReadOnlyList<OnnxOutputTensor> Outputs = Array.Empty<OnnxOutputTensor>();
        public IReadOnlyList<string> InputNames => new[] { "input.1" };
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs) => Outputs;
        public void Dispose() { }
    }

    // ---- lifecycle ----

    [Fact]
    public void Direct_Mode_Starts_Not_Ready()
    {
        var state = new OnnxFacePreloadState();
        Assert.Equal(FacePreloadStates.Starting, state.Current.State);
        Assert.False(state.Current.IsReady);
    }

    [Fact]
    public async Task Both_Models_Compile_And_Validate_Then_Ready()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.True(state.Current.IsReady);
        Assert.Equal(FacePreloadStates.Ready, state.Current.State);
        Assert.Equal(1, factory.DetectorAcquireCount);   // detector compiled once
        Assert.Equal(1, factory.RecognizerAcquireCount);  // recognizer compiled once
        Assert.True(factory.InitCount >= 1);              // native runtime initialized
    }

    [Fact]
    public async Task Detector_Compile_Failure_Keeps_Readiness_Unhealthy()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory
        {
            DetectorThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadStates.Failed, state.Current.State);
        Assert.Equal(FacePreloadFailureCodes.OpenVinoDeviceUnavailable, state.Current.FailureCode);
        Assert.Equal(0, factory.RecognizerAcquireCount); // recognizer never attempted
    }

    [Fact]
    public async Task Recognizer_Compile_Failure_Keeps_Readiness_Unhealthy()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory
        {
            RecognizerThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonCompileFailed),
        };
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadFailureCodes.FaceRecognizerCompileFailed, state.Current.FailureCode);
        Assert.Equal(1, factory.DetectorAcquireCount); // detector had compiled first
    }

    [Fact]
    public async Task Detector_Validation_Failure_Keeps_Readiness_Unhealthy()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        // Malformed detector output: only a bbox branch → decoder shape mismatch.
        factory.Detector.Outputs = new[] { new OnnxOutputTensor("b", new float[16], new[] { 4, 4 }) };
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.Equal(FacePreloadFailureCodes.FaceDetectorValidationFailed, state.Current.FailureCode);
    }

    [Fact]
    public async Task Recognizer_Validation_Failure_On_Wrong_Dimension()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        factory.Recognizer.Outputs = new[] { new OnnxOutputTensor("emb", new float[256], new[] { 1, 256 }) };
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.Equal(FacePreloadFailureCodes.FaceRecognizerValidationFailed, state.Current.FailureCode);
    }

    [Fact]
    public async Task Timeout_Fails_Closed()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory { AcquireSleepMs = 300 };
        var svc = Service(DirectOptions(TempModelDir()), factory, state);
        svc.PreloadCeilingForTests = TimeSpan.FromMilliseconds(50);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadFailureCodes.PreloadTimeout, state.Current.FailureCode);
    }

    [Fact]
    public async Task Cancellation_Exits_Cleanly_Without_Marking_Failed()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(DirectOptions(TempModelDir()), factory, state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunPreloadAsync(cts.Token));

        Assert.False(state.Current.IsReady);
        Assert.NotEqual(FacePreloadStates.Failed, state.Current.State); // shutdown ≠ failure
    }

    [Fact]
    public async Task Readiness_Response_Is_Sanitized()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory
        {
            DetectorThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonNativeMissing),
        };
        var svc = Service(DirectOptions("/secret/abs/path/models"), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        var s = state.Current;
        Assert.Equal(FacePreloadFailureCodes.OrtNativeCoreMissing, s.FailureCode);
        // No path / native text leaks through the state or its detail.
        Assert.DoesNotContain("/secret", s.FailureCode ?? "");
        Assert.DoesNotContain("/secret", s.Detail ?? "");
        Assert.DoesNotContain("/", s.State);
    }

    [Fact]
    public async Task NonDirect_Provider_Is_Ready_Without_Compiling()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var options = DirectOptions(TempModelDir());
        options.Onnx.ExecutionProvider = OnnxExecutionProviders.OnnxRuntime;
        var svc = Service(options, factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.True(state.Current.IsReady);
        Assert.Equal(0, factory.DetectorAcquireCount); // nothing compiled
        Assert.Equal(0, factory.RecognizerAcquireCount);
    }

    [Fact]
    public async Task No_Face_Model_Configured_Is_Ready()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var options = DirectOptions(TempModelDir());
        options.FaceProfileKey = null; // no face model required by this process
        var svc = Service(options, factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.True(state.Current.IsReady);
        Assert.Equal(0, factory.DetectorAcquireCount);
    }

    [Fact]
    public async Task Concurrent_Readiness_Reads_Do_Not_Duplicate_Initialization()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(DirectOptions(TempModelDir()), factory, state);

        var preload = svc.RunPreloadAsync(CancellationToken.None);
        // Hammer the read side concurrently while preload runs.
        var readers = Enumerable.Range(0, 32).Select(n => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) { var snapshot = state.Current; _ = snapshot.State; }
        }));
        await Task.WhenAll(readers.Append(preload));

        Assert.True(state.Current.IsReady);
        Assert.Equal(1, factory.DetectorAcquireCount);  // reads never trigger a compile
        Assert.Equal(1, factory.RecognizerAcquireCount);
    }

    // ---- reason mapping ----

    [Fact]
    public void MapCompileReason_Maps_Detector_And_Recognizer_Stages()
    {
        Assert.Equal(FacePreloadFailureCodes.FaceDetectorModelMissing,
            OnnxFacePreloadService.MapCompileReason(OnnxInferenceSessionFactory.ReasonModelNotFound, FacePreloadStates.FaceDetectorCompiling));
        Assert.Equal(FacePreloadFailureCodes.FaceRecognizerModelMissing,
            OnnxFacePreloadService.MapCompileReason(OnnxInferenceSessionFactory.ReasonModelNotFound, FacePreloadStates.FaceRecognizerCompiling));
        Assert.Equal(FacePreloadFailureCodes.OrtAbiMismatch,
            OnnxFacePreloadService.MapCompileReason(OnnxInferenceSessionFactory.ReasonAbiMismatch, FacePreloadStates.FaceDetectorCompiling));
        Assert.Equal(FacePreloadFailureCodes.OpenVinoEpMissing,
            OnnxFacePreloadService.MapCompileReason(OnnxInferenceSessionFactory.ReasonProviderUnavailable, FacePreloadStates.FaceDetectorCompiling));
        Assert.Equal(FacePreloadFailureCodes.OpenVinoDeviceUnavailable,
            OnnxFacePreloadService.MapCompileReason(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, FacePreloadStates.FaceRecognizerCompiling));
    }

    // ---- validators (integrity checks) ----

    [Fact]
    public void ValidateRecognizer_Rejects_NonFinite_And_ZeroNorm()
    {
        var config = OnnxFaceModels.Catalog[OnnxFaceModels.Antelopev2Key];

        var nan = new float[512]; nan[0] = float.NaN;
        Assert.False(OnnxFacePreloadService.ValidateRecognizer(
            new[] { new OnnxOutputTensor("e", nan, new[] { 1, 512 }) }, config, out _));

        var zero = new float[512];
        Assert.False(OnnxFacePreloadService.ValidateRecognizer(
            new[] { new OnnxOutputTensor("e", zero, new[] { 1, 512 }) }, config, out var reason));
        Assert.Equal("zero-norm", reason);
    }

    [Fact]
    public void ValidateDetector_Rejects_NonFinite_Output()
    {
        var config = OnnxFaceModels.Catalog[OnnxFaceModels.Antelopev2Key];
        var bad = new float[16]; bad[0] = float.PositiveInfinity;
        Assert.False(OnnxFacePreloadService.ValidateDetector(
            new[] { new OnnxOutputTensor("s", bad, new[] { 16, 1 }) }, config, out var reason));
        Assert.Equal("non-finite-output", reason);
    }
}

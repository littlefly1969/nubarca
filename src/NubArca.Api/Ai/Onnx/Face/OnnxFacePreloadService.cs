using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NubArca.Api.Ai.Onnx.Face;

// Face AI milestone: bounded, run-once startup preload for the direct
// (openvino-direct) face pipeline. It compiles BOTH configured direct sessions
// through the SAME IOnnxInferenceSessionFactory cache the real requests use — so
// the first real request is warm and NO second compilation happens — and runs a
// bounded synthetic inference against each to validate the output contract. The
// process is LIVE while this runs (liveness stays green); readiness only turns
// healthy after both models compile AND validate. Fail-closed in direct mode: any
// failure leaves readiness FAILED with a sanitized code and never accepts AI face
// traffic. The non-direct provider (onnxruntime) preserves its
// prior startup behavior — nothing to compile, so readiness is immediately READY.
//
// Synthetic inputs are deterministic, in-memory tensors: NO PostgreSQL, storage,
// user photos, external files or private data. It is an INTEGRITY check (shape,
// finiteness, decode/normalize compatibility), never a biometric-quality test.
public sealed class OnnxFacePreloadService : BackgroundService
{
    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;
    private readonly OnnxFacePreloadState _state;
    private readonly ILogger<OnnxFacePreloadService> _logger;

    public OnnxFacePreloadService(
        IOptions<AiOptions> options,
        IOnnxInferenceSessionFactory factory,
        OnnxFacePreloadState state,
        ILogger<OnnxFacePreloadService> logger)
    {
        _options = options;
        _factory = factory;
        _state = state;
        _logger = logger;
    }

    // Test seam: overrides the computed overall preload ceiling so the fail-closed
    // PRELOAD_TIMEOUT path is exercisable without a multi-second wait. Null in
    // production (the ceiling is derived from Ai:TimeoutSeconds).
    internal TimeSpan? PreloadCeilingForTests { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never let an unexpected throw escape ExecuteAsync (that would fault the
        // host). Any failure is recorded as a sanitized FAILED state instead.
        try
        {
            await RunPreloadAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during preload → clean exit, no permanent failure recorded.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Face model preload aborted: {Type}", ex.GetType().Name);
            _state.Fail(FacePreloadFailureCodes.FaceDetectorCompileFailed, "unexpected");
        }
    }

    // Testable core. Runs the full preload lifecycle exactly once and advances the
    // shared readiness state. Bounded by Ai:TimeoutSeconds (per model) plus an
    // overall ceiling; cancellation exits cleanly without marking a failure.
    internal async Task RunPreloadAsync(CancellationToken stoppingToken)
    {
        var onnx = _options.Value.Onnx;
        var provider = OnnxExecutionProviders.Normalize(onnx.ExecutionProvider);

        // Non-direct providers had no compile-backed readiness before this milestone
        // and must keep booting exactly as they did: nothing to preload here.
        if (provider != OnnxExecutionProviders.OpenVinoDirect)
        {
            _state.Ready($"provider={provider}");
            _logger.LogInformation("Face model preload skipped (provider={Provider}); readiness READY.", provider);
            return;
        }

        // Resolve WHICH models this process runs from the configured profiles.
        // No profile configured → that engine is not required by this process.
        var profileKey = _options.Value.FaceProfileKey;
        var config = string.IsNullOrWhiteSpace(profileKey)
            ? null
            : OnnxFaceModels.ResolveConfig(configHash: null, profileKey: profileKey!);

        // SigLIP direct milestone: the API hosts the photo TEXT tower (semantic
        // query embedding). Preload it when the active photo profile has one and
        // image embeddings are enabled; skip otherwise (dev / face-only hosts).
        var photoConfig = ResolvePhotoTextConfig(_options.Value);

        if (config is null && photoConfig is null)
        {
            _state.Ready("no-direct-model-configured");
            _logger.LogInformation("Direct model preload: no direct model configured; readiness READY.");
            return;
        }

        var modelDir = onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            _state.Fail(
                config is not null
                    ? FacePreloadFailureCodes.FaceDetectorModelMissing
                    : FacePreloadFailureCodes.PhotoTextModelMissing,
                "modeldir-not-configured");
            return;
        }

        // Overall bounded ceiling so a permanently-hanging compile fails closed as
        // PRELOAD_TIMEOUT instead of blocking readiness forever. Sized generously
        // for observed GPU compile time (10x the per-op timeout, floor 120s).
        var perOp = Math.Max(1, _options.Value.TimeoutSeconds);
        var ceiling = PreloadCeilingForTests ?? TimeSpan.FromSeconds(Math.Max(120, perOp * 10L));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(ceiling);
        var ct = timeoutCts.Token;

        var sw = Stopwatch.StartNew();
        try
        {
            _state.Advance(FacePreloadStates.NativeRuntimeInitializing);
            _factory.EnsureNativeProviderInitialized();

            if (config is not null)
            {
                var detectorPath = Path.Combine(modelDir, config.PackageSubdir, config.DetectorFile);
                var recognitionPath = Path.Combine(modelDir, config.PackageSubdir, config.RecognitionFile);

                // ---- Detector: compile then synthetic-validate ----------------
                _state.Advance(FacePreloadStates.FaceDetectorCompiling);
                var detectorOutputs = CompileAndRunSynthetic(
                    new OnnxModelSpec(OnnxModel.FaceDetector, detectorPath),
                    BuildSyntheticChw(config.DetectorInputSize),
                    config.DetectorInputSize, ct, isDetector: true);

                _state.Advance(FacePreloadStates.FaceDetectorValidating);
                if (!ValidateDetector(detectorOutputs, config, out var detReason))
                {
                    _state.Fail(FacePreloadFailureCodes.FaceDetectorValidationFailed, detReason);
                    _logger.LogWarning("Face detector preload validation failed: {Reason}", detReason);
                    return;
                }

                // ---- Recognizer: compile then synthetic-validate --------------
                _state.Advance(FacePreloadStates.FaceRecognizerCompiling);
                var recognizerOutputs = CompileAndRunSynthetic(
                    new OnnxModelSpec(OnnxModel.FaceRecognizer, recognitionPath),
                    BuildSyntheticChw(config.RecognitionInputSize),
                    config.RecognitionInputSize, ct, isDetector: false);

                _state.Advance(FacePreloadStates.FaceRecognizerValidating);
                if (!ValidateRecognizer(recognizerOutputs, config, out var recReason))
                {
                    _state.Fail(FacePreloadFailureCodes.FaceRecognizerValidationFailed, recReason);
                    _logger.LogWarning("Face recognizer preload validation failed: {Reason}", recReason);
                    return;
                }
            }

            if (photoConfig is not null)
            {
                // ---- Photo TEXT tower: assets, compile, synthetic-validate ----
                var textModelPath = Path.Combine(modelDir, photoConfig.ModelSubdir, photoConfig.TextModelFile!);
                var tokenizerPath = Path.Combine(modelDir, photoConfig.ModelSubdir, photoConfig.TokenizerFile!);
                if (!File.Exists(textModelPath))
                {
                    _state.Fail(FacePreloadFailureCodes.PhotoTextModelMissing, "text-model-not-found");
                    return;
                }
                if (!File.Exists(tokenizerPath))
                {
                    _state.Fail(FacePreloadFailureCodes.PhotoTextTokenizerMissing, "tokenizer-not-found");
                    return;
                }

                _state.Advance(FacePreloadStates.PhotoTextCompiling);
                var textOutputs = CompileAndRunSyntheticText(
                    new OnnxModelSpec(OnnxModel.PhotoText, textModelPath), photoConfig, ct);

                _state.Advance(FacePreloadStates.PhotoTextValidating);
                if (!ValidatePhotoText(textOutputs, photoConfig, out var textReason))
                {
                    _state.Fail(FacePreloadFailureCodes.PhotoTextValidationFailed, textReason);
                    _logger.LogWarning("Photo text preload validation failed: {Reason}", textReason);
                    return;
                }
            }

            _state.Ready($"warm-in-{sw.ElapsedMilliseconds}ms");
            _logger.LogInformation(
                "Direct model preload READY in {Ms}ms (face={Face} photoText={PhotoText}, device detector={DetDev} recognizer={RecDev} photoText={TextDev}).",
                sw.ElapsedMilliseconds, config is not null, photoConfig is not null,
                onnx.OpenVino.FaceDetectorDevice, onnx.OpenVino.FaceRecognizerDevice,
                onnx.OpenVino.PhotoTextDevice);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // real shutdown → bubble to ExecuteAsync for a clean exit
        }
        catch (OperationCanceledException)
        {
            // Our own ceiling elapsed (stoppingToken NOT signalled) → fail closed.
            _state.Fail(FacePreloadFailureCodes.PreloadTimeout, $"after-{sw.ElapsedMilliseconds}ms");
            _logger.LogWarning("Face model preload timed out after {Ms}ms.", sw.ElapsedMilliseconds);
        }
        catch (OnnxSessionUnavailableException ex)
        {
            // Which stage failed is encoded by the current state.
            var stage = _state.Current.State;
            var code = MapCompileReason(ex.ReasonCode, stage);
            _state.Fail(code, ex.ReasonCode);
            _logger.LogWarning("Face model preload failed at {Stage}: {Code} ({Reason}).", stage, code, ex.ReasonCode);
        }
    }

    // The photo TEXT tower is required by this process when AI + image embeddings
    // are enabled and the active photo profile is an ONNX multimodal profile with
    // a text tower. Deterministic/none providers and image-only profiles → null.
    internal static OnnxImageModelConfig? ResolvePhotoTextConfig(AiOptions options)
    {
        if (!options.Enabled || !options.ImageEmbeddingsEnabled) return null;
        var key = options.PhotoSimilarityProfileKey;
        if (string.IsNullOrWhiteSpace(key)) return null;
        var config = OnnxImageModels.ResolveConfig(configHash: null, profileKey: key!);
        return config is { TextModelFile: not null, TokenizerFile: not null } ? config : null;
    }

    // Compile + one bounded synthetic inference for the text tower: fixed i64
    // inputs (pad ids, all-attended mask — the production fixed-padding policy),
    // exactly the production input names/shape. Integrity only, never quality.
    private IReadOnlyList<OnnxOutputTensor> CompileAndRunSyntheticText(
        OnnxModelSpec spec, OnnxImageModelConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = _factory.Acquire(spec); // compiles for the configured device
        var session = lease.Session;
        ct.ThrowIfCancellationRequested();
        var ids = new long[config.TextSequenceLength]; // id 0 (pad) is always valid
        var mask = OnnxTextEmbedder.BuildFixedPaddingAttentionMask(config.TextSequenceLength);
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(config.TextInputTensor,
                new DenseTensor<long>(ids, new[] { 1, config.TextSequenceLength })),
            NamedOnnxValue.CreateFromTensor(config.TextAttentionMaskTensor,
                new DenseTensor<long>(mask, new[] { 1, config.TextSequenceLength })),
        };
        return session.Run(inputs);
    }

    // Text-tower integrity: the named output present, profile-dimensional, finite,
    // non-zero norm, and normalizable via the exact production finalize path.
    internal static bool ValidatePhotoText(
        IReadOnlyList<OnnxOutputTensor> outputs, OnnxImageModelConfig config, out string reason) =>
        ValidateEmbeddingOutput(outputs, config.TextOutputTensor, config.Dimension, out reason);

    // Image-tower integrity (worker preload): same contract, image output tensor.
    internal static bool ValidatePhotoImage(
        IReadOnlyList<OnnxOutputTensor> outputs, OnnxImageModelConfig config, out string reason) =>
        ValidateEmbeddingOutput(outputs, config.OutputTensor, config.Dimension, out reason);

    private static bool ValidateEmbeddingOutput(
        IReadOnlyList<OnnxOutputTensor> outputs, string? outputName, int dimension, out string reason)
    {
        reason = "ok";
        if (outputs is null || outputs.Count == 0)
        {
            reason = "no-outputs";
            return false;
        }

        var match = outputName is { Length: > 0 }
            ? outputs.Where(o => string.Equals(o.Name, outputName, StringComparison.Ordinal))
                .Select(o => o.Data).FirstOrDefault()
            : outputs[0].Data;
        if (match is null)
        {
            reason = "output-tensor-missing";
            return false;
        }

        if (match.Length != dimension)
        {
            reason = "dimension-mismatch";
            return false;
        }

        double sumSquares = 0;
        foreach (var v in match)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                reason = "non-finite-output";
                return false;
            }

            sumSquares += (double)v * v;
        }

        if (Math.Sqrt(sumSquares) <= double.Epsilon)
        {
            reason = "zero-norm";
            return false;
        }

        try
        {
            var vector = OnnxImageEmbeddings.Finalize(match, dimension);
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            if (Math.Abs(norm - 1.0) > 1e-3)
            {
                reason = "not-unit-norm";
                return false;
            }
        }
        catch (ArgumentException)
        {
            reason = "finalize-rejected";
            return false;
        }

        return true;
    }

    // Acquire (→ compile + cache) a session and run one bounded synthetic inference
    // on it. The lease shares the cached session, so the first REAL request reuses
    // it and no second compile occurs.
    private IReadOnlyList<OnnxOutputTensor> CompileAndRunSynthetic(
        OnnxModelSpec spec, float[] chw, int size, CancellationToken ct, bool isDetector)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = _factory.Acquire(spec); // compiles for the configured device
        var session = lease.Session;
        ct.ThrowIfCancellationRequested();
        var inputName = session.InputNames.First();
        var tensor = new DenseTensor<float>(chw, new[] { 1, 3, size, size });
        return session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
    }

    // Deterministic, finite synthetic NCHW input (a fixed mid-scale ramp). Not an
    // image and not decoded — purely an integrity probe of the compiled graph.
    // Internal: the worker's photo-image preload (CLI host) reuses it.
    internal static float[] BuildSyntheticChw(int size)
    {
        var data = new float[3 * size * size];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = ((i % 255) / 255f) - 0.5f; // bounded [-0.5, 0.5), finite
        }

        return data;
    }

    // Detector integrity: outputs present, all finite, SCRFD-decodable (consistent
    // shapes → no hard shape diagnostic), and every decoded coordinate finite.
    internal static bool ValidateDetector(
        IReadOnlyList<OnnxOutputTensor> outputs, OnnxFaceModelConfig config, out string reason)
    {
        reason = "ok";
        if (outputs is null || outputs.Count == 0)
        {
            reason = "no-outputs";
            return false;
        }

        foreach (var o in outputs)
        {
            if (o.Data.Length == 0)
            {
                reason = "empty-output";
                return false;
            }

            foreach (var v in o.Data)
            {
                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    reason = "non-finite-output";
                    return false;
                }
            }
        }

        var raws = outputs.Select(o => new ScrfdDecoder.RawOutput(o.Data, o.Shape)).ToList();
        var decoded = ScrfdDecoder.Decode(
            raws, config.DetectorInputSize, config.DetectorInputSize,
            config.DetectorScoreThreshold, config.DetectorNmsThreshold, out var diagnostic);

        // A hard shape mismatch means the compiled graph is incompatible with the
        // decoder — a real integrity failure. (A synthetic input legitimately
        // yielding zero faces is fine and expected.)
        if (diagnostic == "detector-output-shape-unexpected")
        {
            reason = "decoder-incompatible";
            return false;
        }

        foreach (var f in decoded)
        {
            if (!IsFinite(f.X1) || !IsFinite(f.Y1) || !IsFinite(f.X2) || !IsFinite(f.Y2) || !IsFinite(f.Score))
            {
                reason = "non-finite-coordinate";
                return false;
            }
        }

        return true;
    }

    // Recognizer integrity: a single 512-d output, finite, non-zero norm, and
    // successfully L2-normalizable via the exact production finalize path.
    internal static bool ValidateRecognizer(
        IReadOnlyList<OnnxOutputTensor> outputs, OnnxFaceModelConfig config, out string reason)
    {
        reason = "ok";
        if (outputs is null || outputs.Count == 0)
        {
            reason = "no-outputs";
            return false;
        }

        var raw = outputs[0].Data;
        if (raw.Length != config.Dimension)
        {
            reason = "dimension-mismatch";
            return false;
        }

        double sumSquares = 0;
        foreach (var v in raw)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                reason = "non-finite-output";
                return false;
            }

            sumSquares += (double)v * v;
        }

        if (Math.Sqrt(sumSquares) <= double.Epsilon)
        {
            reason = "zero-norm";
            return false;
        }

        try
        {
            var vector = OnnxImageEmbeddings.Finalize(raw, config.Dimension);
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            if (Math.Abs(norm - 1.0) > 1e-3)
            {
                reason = "not-unit-norm";
                return false;
            }
        }
        catch (ArgumentException)
        {
            reason = "finalize-rejected";
            return false;
        }

        return true;
    }

    private static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));

    // Map a sanitized factory reason onto the stable per-stage failure code. The
    // stage (detector vs recognizer vs photo-text) is taken from the state at the
    // time of failure.
    internal static string MapCompileReason(string factoryReason, string stage)
    {
        var detector = stage is FacePreloadStates.FaceDetectorCompiling or FacePreloadStates.NativeRuntimeInitializing;
        var photoText = stage is FacePreloadStates.PhotoTextCompiling or FacePreloadStates.PhotoTextValidating;
        return factoryReason switch
        {
            OnnxInferenceSessionFactory.ReasonModelNotFound when photoText => FacePreloadFailureCodes.PhotoTextModelMissing,
            OnnxInferenceSessionFactory.ReasonModelNotFound =>
                detector ? FacePreloadFailureCodes.FaceDetectorModelMissing : FacePreloadFailureCodes.FaceRecognizerModelMissing,
            OnnxInferenceSessionFactory.ReasonNativeMissing => FacePreloadFailureCodes.OrtNativeCoreMissing,
            OnnxInferenceSessionFactory.ReasonStockNativeMissing => FacePreloadFailureCodes.OrtNativeCoreMissing,
            OnnxInferenceSessionFactory.ReasonNativeUnavailable => FacePreloadFailureCodes.OrtNativeLoadFailed,
            OnnxInferenceSessionFactory.ReasonAbiMismatch => FacePreloadFailureCodes.OrtAbiMismatch,
            OnnxInferenceSessionFactory.ReasonProviderUnavailable => FacePreloadFailureCodes.OpenVinoEpMissing,
            OnnxInferenceSessionFactory.ReasonDeviceUnavailable => FacePreloadFailureCodes.OpenVinoDeviceUnavailable,
            _ when photoText => FacePreloadFailureCodes.PhotoTextCompileFailed,
            _ => detector ? FacePreloadFailureCodes.FaceDetectorCompileFailed : FacePreloadFailureCodes.FaceRecognizerCompileFailed,
        };
    }
}

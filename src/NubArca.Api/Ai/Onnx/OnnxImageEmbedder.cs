using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Onnx;

// Phase 2A: local ONNX image-embedding backend (provider "onnx"). It is the real
// IImageEmbedder Phase 2B will reuse, but it stays UNAVAILABLE until a model is
// configured (Ai__Onnx__ModelDir) and present on disk — a missing model is an
// environment/config state, surfaced via CheckReadiness, never a content failure
// (so AI jobs no-op and write no per-blob skipped/failed rows).
//
// SigLIP direct milestone: the PHOTO IMAGE tower routes exactly like the face
// models — explicit, never silently falling back:
//   onnxruntime      → factory-owned CPU session
//   openvino-direct  → factory-owned OpenVINO session (FP32; device per config)
// Sessions are owned by IOnnxInferenceSessionFactory (one cache, one lifecycle);
// concurrency is bounded by Ai__MaxConcurrency and each inference is bounded by
// Ai__TimeoutSeconds. Outputs are dimension-validated, NaN/Infinity-rejected,
// and L2-normalized (OnnxImageEmbeddings.Finalize). Raw vectors never leave the
// process except as serialized EmbeddingBytes upstream.
public sealed class OnnxImageEmbedder : IImageEmbedder, IDisposable
{
    private readonly IOptions<AiOptions> _options;
    private readonly OnnxImagePreprocessor _preprocessor;
    private readonly ILogger<OnnxImageEmbedder> _logger;
    private readonly IOnnxInferenceSessionFactory _factory;
    private readonly SemaphoreSlim _gate;

    public OnnxImageEmbedder(
        IOptions<AiOptions> options,
        OnnxImagePreprocessor preprocessor,
        ILogger<OnnxImageEmbedder> logger,
        IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _preprocessor = preprocessor;
        _logger = logger;
        _factory = factory;
        var max = ResolveImageConcurrency(options.Value);
        _gate = new SemaphoreSlim(max, max);
    }

    // Max concurrent image inferences this embedder allows. The DUAL:CPU,GPU image
    // tower needs at least 2 in flight so the two worker job slots land on the two
    // device executors (the bounded CPU+GPU tandem) — otherwise the gate would
    // serialize them and the GPU would sit idle. This only raises the FLOOR; a
    // higher Ai__MaxConcurrency still wins. Single-device placement is unchanged.
    internal static int ResolveImageConcurrency(AiOptions ai)
    {
        var configured = Math.Max(1, ai.MaxConcurrency);
        var floor = IsDualImageTower(ai) ? 2 : 1;
        return Math.Max(configured, floor);
    }

    private static bool IsDualImageTower(AiOptions ai) =>
        OnnxExecutionProviders.Normalize(ai.Onnx.ExecutionProvider) == OnnxExecutionProviders.OpenVinoDirect
        && OnnxDevice.IsDual(ai.Onnx.OpenVino.DeviceFor(OnnxModel.PhotoImage));

    public string Provider => AiProviders.Onnx;

    public bool Supports(string capability) => capability == AiCapabilities.ImageEmbedding;

    public AiBackendReadiness CheckReadiness(AiProfile profile)
    {
        var config = OnnxImageModels.ResolveConfig(profile.ConfigHash, profile.Key);
        if (config is null)
        {
            return AiBackendReadiness.NotReady("onnx-unknown-model");
        }

        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            return AiBackendReadiness.NotReady("onnx-modeldir-not-configured");
        }

        var modelPath = ModelPath(modelDir, config);
        if (!File.Exists(modelPath))
        {
            return AiBackendReadiness.NotReady("onnx-model-not-found");
        }

        // SigLIP direct milestone: compile-backed readiness in direct mode, exactly
        // like the face backend. "Ready" means the OpenVINO session actually
        // COMPILED for the configured device (warm/cached for the first real
        // request); the factory returns a sanitized reason on failure.
        if (OnnxExecutionProviders.Normalize(_options.Value.Onnx.ExecutionProvider)
            == OnnxExecutionProviders.OpenVinoDirect)
        {
            _factory.EnsureNativeProviderInitialized();
            try
            {
                using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, modelPath));
                _ = lease.Session; // compiled + cached (warm)
            }
            catch (OnnxSessionUnavailableException ex)
            {
                return AiBackendReadiness.NotReady(ex.ReasonCode);
            }
        }

        return AiBackendReadiness.Ready;
    }

    public async Task<AiEmbeddingResult> EmbedImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
    {
        var config = OnnxImageModels.ResolveConfig(profile.ConfigHash, profile.Key)
            ?? throw new InvalidOperationException($"No ONNX image config for profile '{profile.Key}'.");
        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            throw new InvalidOperationException("Ai:Onnx:ModelDir is not configured.");
        }

        var modelPath = ModelPath(modelDir, config);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX model file not found.");
        }

        var dimensionForProfile = profile.Dimension is > 0 ? profile.Dimension.Value : config.Dimension;
        var bytes = imageBytes.ToArray();
        var timeoutSeconds = _options.Value.TimeoutSeconds;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var work = Task.Run(() => RunInference(modelPath, config, bytes), cancellationToken);
            var raw = timeoutSeconds > 0
                ? await work.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                : await work.WaitAsync(cancellationToken);

            var vector = OnnxImageEmbeddings.Finalize(raw, dimensionForProfile);
            var metric = string.IsNullOrWhiteSpace(profile.DistanceMetric)
                ? AiDistanceMetrics.Cosine
                : profile.DistanceMetric!;
            return new AiEmbeddingResult(vector, dimensionForProfile, metric);
        }
        finally
        {
            _gate.Release();
        }
    }

    private float[] RunInference(string modelPath, OnnxImageModelConfig config, byte[] bytes)
    {
        var pre = _preprocessor.Preprocess(bytes, config);
        // The tensor shape/dtype, input name, output selection and 1152-d raw
        // vector are unchanged; the lease shares the factory-cached session and is
        // released immediately (never created or disposed here). L2 normalization
        // stays in the caller (OnnxImageEmbeddings.Finalize).
        _factory.EnsureNativeProviderInitialized();
        using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoImage, modelPath));
        var tensor = new DenseTensor<float>(pre.Data, new[] { 1, pre.Channels, pre.Height, pre.Width });
        var outputs = lease.Session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(config.InputTensor, tensor),
        });
        var output = config.OutputTensor is { Length: > 0 } name
            ? outputs.First(o => string.Equals(o.Name, name, StringComparison.Ordinal))
            : outputs[0];
        return output.Data;
    }

    private static string ModelPath(string modelDir, OnnxImageModelConfig config)
        => Path.Combine(modelDir, config.ModelSubdir, config.ModelFile);

    public void Dispose()
    {
        // Sessions are owned and disposed by IOnnxInferenceSessionFactory; the
        // embedder only owns the concurrency gate.
        _gate.Dispose();
    }
}

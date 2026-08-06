using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using HuggingFaceTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace NubArca.Api.Ai.Onnx;

// Text query tower for the active multimodal PHOTO profile. This intentionally
// supports the image-embedding capability: the text vector is not a document
// embedding and is never persisted; it is a query in the exact same SigLIP2
// space as the profile's image vectors.
//
// SigLIP direct milestone: tokenization stays fully in .NET (the exported
// tokenizer.json via Tokenizers.HuggingFace — UNCHANGED); only the text-tower
// inference routes like every other ONNX model — explicit, never silently
// falling back:
//   onnxruntime      → factory-owned CPU session
//   openvino-direct  → factory-owned OpenVINO session (FP32; device per config)
public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;
    private readonly ConcurrentDictionary<string, Lazy<HuggingFaceTokenizer>> _tokenizers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;

    public OnnxTextEmbedder(
        IOptions<AiOptions> options,
        IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _factory = factory;
        _gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrency));
    }

    public string Provider => AiProviders.Onnx;

    public bool Supports(string capability) => capability == AiCapabilities.ImageEmbedding;

    public AiBackendReadiness CheckReadiness(AiProfile profile)
    {
        var config = OnnxImageModels.ResolveConfig(profile.ConfigHash, profile.Key);
        if (config?.TextModelFile is null || config.TokenizerFile is null)
        {
            return AiBackendReadiness.NotReady("onnx-text-model-unsupported");
        }

        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            return AiBackendReadiness.NotReady("onnx-modeldir-not-configured");
        }

        if (!File.Exists(TextModelPath(modelDir, config)))
        {
            return AiBackendReadiness.NotReady("onnx-text-model-not-found");
        }

        if (!File.Exists(TokenizerPath(modelDir, config)))
        {
            return AiBackendReadiness.NotReady("onnx-tokenizer-not-found");
        }

        // SigLIP direct milestone: compile-backed readiness in direct mode, exactly
        // like the face backend and the image embedder. The factory returns a
        // sanitized reason on failure; compilation is warm/cached.
        if (OnnxExecutionProviders.Normalize(_options.Value.Onnx.ExecutionProvider)
            == OnnxExecutionProviders.OpenVinoDirect)
        {
            _factory.EnsureNativeProviderInitialized();
            try
            {
                using var lease = _factory.Acquire(
                    new OnnxModelSpec(OnnxModel.PhotoText, TextModelPath(modelDir, config)));
                _ = lease.Session; // compiled + cached (warm)
            }
            catch (OnnxSessionUnavailableException ex)
            {
                return AiBackendReadiness.NotReady(ex.ReasonCode);
            }
        }

        return AiBackendReadiness.Ready;
    }

    public async Task<AiEmbeddingResult> EmbedTextAsync(
        string text, AiProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var config = OnnxImageModels.ResolveConfig(profile.ConfigHash, profile.Key)
            ?? throw new InvalidOperationException($"No ONNX multimodal config for profile '{profile.Key}'.");
        if (config.TextModelFile is null || config.TokenizerFile is null)
        {
            throw new InvalidOperationException("The active photo profile has no text tower.");
        }

        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            throw new InvalidOperationException("Ai:Onnx:ModelDir is not configured.");
        }

        var modelPath = TextModelPath(modelDir, config);
        var tokenizerPath = TokenizerPath(modelDir, config);
        var expectedDimension = profile.Dimension is > 0 ? profile.Dimension.Value : config.Dimension;
        var timeoutSeconds = _options.Value.TimeoutSeconds;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var work = Task.Run(() =>
            {
                var tokenizer = _tokenizers.GetOrAdd(
                    tokenizerPath,
                    p => new Lazy<HuggingFaceTokenizer>(
                        () => HuggingFaceTokenizer.FromFile(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                var encoding = tokenizer.Encode(
                    text, addSpecialTokens: true, includeAttentionMask: true).First();

                // Quality invariant: the exported tokenizer.json MUST contain
                // SigLIP2's training-time fixed truncation + padding policy.
                // Refuse a subtly incompatible asset rather than padding or
                // truncating an already-tokenized sequence by hand.
                if (encoding.Ids.Count != config.TextSequenceLength)
                {
                    throw new InvalidOperationException(
                        $"Tokenizer produced {encoding.Ids.Count} ids; expected exactly {config.TextSequenceLength}.");
                }
                if (encoding.AttentionMask.Count != config.TextSequenceLength)
                {
                    throw new InvalidOperationException("Tokenizer attention mask length does not match input ids.");
                }

                var ids = encoding.Ids.Select(id => (long)id).ToArray();
                // SigLIP2 FixRes was trained with fixed 64-token padding and no
                // attention mask. Hugging Face AutoProcessor therefore calls
                // get_text_features without one. The exported graph keeps the
                // input for a stable ONNX contract, so all positions MUST be
                // attended. Passing the tokenizer's 0-for-padding mask changes
                // the embedding space and destroys text/image alignment.
                var mask = BuildFixedPaddingAttentionMask(config.TextSequenceLength);
                var tensor = new DenseTensor<long>(ids, new[] { 1, config.TextSequenceLength });
                var maskTensor = new DenseTensor<long>(mask, new[] { 1, config.TextSequenceLength });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(config.TextInputTensor, tensor),
                    NamedOnnxValue.CreateFromTensor(config.TextAttentionMaskTensor, maskTensor),
                };
                // The lease shares the factory-cached session and is released
                // immediately (never created or disposed here). Input names, i64
                // dtype, fixed 64-token shape and output selection are unchanged.
                _factory.EnsureNativeProviderInitialized();
                using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.PhotoText, modelPath));
                var outputs = lease.Session.Run(inputs);
                var raw = outputs.First(o => string.Equals(
                    o.Name, config.TextOutputTensor, StringComparison.Ordinal)).Data;
                return OnnxImageEmbeddings.Finalize(raw, expectedDimension);
            }, cancellationToken);

            var vector = timeoutSeconds > 0
                ? await work.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                : await work.WaitAsync(cancellationToken);
            return new AiEmbeddingResult(vector, expectedDimension, AiDistanceMetrics.Cosine);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string TextModelPath(string modelDir, OnnxImageModelConfig config) =>
        Path.Combine(modelDir, config.ModelSubdir, config.TextModelFile!);

    private static string TokenizerPath(string modelDir, OnnxImageModelConfig config) =>
        Path.Combine(modelDir, config.ModelSubdir, config.TokenizerFile!);

    internal static long[] BuildFixedPaddingAttentionMask(int sequenceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceLength);
        return Enumerable.Repeat(1L, sequenceLength).ToArray();
    }

    public void Dispose()
    {
        // Sessions are owned and disposed by IOnnxInferenceSessionFactory; the
        // embedder owns the tokenizer cache and the concurrency gate.
        foreach (var tokenizer in _tokenizers.Values)
        {
            if (tokenizer.IsValueCreated) tokenizer.Value.Dispose();
        }
        _tokenizers.Clear();
        _gate.Dispose();
    }
}

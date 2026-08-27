using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using HuggingFaceTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace NubArca.Api.Ai.TextEmbeddings;

/// Local text embeddings under ONNX Runtime, in this process.
///
/// There is no hosted alternative anywhere in this substrate, and that is a
/// design decision rather than an omission. Embedding is how NubArca decides
/// WHAT to send to a chat model; routing that decision through a third party
/// would send the entire corpus — and, when owner-private domains arrive, a
/// person's own documents — to an external service in order to work out what is
/// allowed to leave. A missing model file is an availability condition with a
/// reason code, and retrieval falls back to lexical.
///
/// Weights are not committed and are never downloaded: the model directory is
/// operator configuration, exactly like the photo and face towers.
///
/// It owns the model-specific preprocessing so RAG does not have to know any of
/// it — the `query: ` / `passage: ` prefixes, truncation that preserves the
/// closing separator token, masked mean pooling, and L2 normalization are all
/// facts about this checkpoint, stated in RagTextEmbeddingModels and applied
/// here.
public sealed class OnnxTextEmbeddingProvider : ITextEmbeddingProvider, IDisposable
{
    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;
    private readonly ConcurrentDictionary<string, Lazy<HuggingFaceTokenizer>> _tokenizers =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;

    public OnnxTextEmbeddingProvider(IOptions<AiOptions> options, IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _factory = factory;
        _gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrency));
    }

    public string Provider => AiProviders.Onnx;

    public TextEmbeddingReadiness CheckReadiness(AiProfile profile)
    {
        if (profile.Capability != AiCapabilities.TextEmbedding
            || profile.Modality != AiModalities.Text)
        {
            return TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingProfileUnavailable);
        }

        var config = RagTextEmbeddingModels.ResolveConfig(profile.ConfigHash, profile.Key);
        if (config is null)
        {
            return TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingProfileUnavailable);
        }
        if (profile.Dimension is not int dimension || dimension != config.Dimension)
        {
            return TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingDimensionUnsupported);
        }

        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir)
            || !File.Exists(ModelPath(modelDir, config))
            || !File.Exists(TokenizerPath(modelDir, config)))
        {
            // One reason for all three: an operator reads their own
            // configuration, and a log line is not the place to publish which
            // path was missing from a filesystem layout.
            return TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingModelUnavailable);
        }

        if (OnnxExecutionProviders.Normalize(_options.Value.Onnx.ExecutionProvider)
            == OnnxExecutionProviders.OpenVinoDirect)
        {
            _factory.EnsureNativeProviderInitialized();
            try
            {
                using var lease = _factory.Acquire(
                    new OnnxModelSpec(OnnxModel.RagText, ModelPath(modelDir, config)));
                _ = lease.Session; // compiled + cached (warm)
            }
            catch (OnnxSessionUnavailableException)
            {
                return TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingModelUnavailable);
            }
        }

        return TextEmbeddingReadiness.Ready;
    }

    public async Task<TextEmbeddingResult> EmbedAsync(
        AiProfile profile,
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var readiness = CheckReadiness(profile);
        if (!readiness.IsReady)
        {
            throw new TextEmbeddingUnavailableException(readiness.Reason!);
        }

        var config = RagTextEmbeddingModels.ResolveConfig(profile.ConfigHash, profile.Key)!;
        var modelDir = _options.Value.Onnx.ModelDir!;
        var modelPath = ModelPath(modelDir, config);
        var tokenizerPath = TokenizerPath(modelDir, config);
        var timeoutSeconds = _options.Value.TimeoutSeconds;

        // The prefix is applied HERE, not by the caller. RAG states whether this
        // is a question or a passage; which literal string that becomes is a
        // property of the checkpoint.
        var prepared = RagTextEmbeddingModels.PrefixFor(config, inputKind) + text;

        await _gate.WaitAsync(cancellationToken);

        // THE SLOT BELONGS TO THE NATIVE WORK, NOT TO THE WAIT.
        //
        // ONNX Runtime's Run is a blocking native call. A timeout on WaitAsync
        // proves only that WE stopped waiting — the native inference is still
        // running, still holding a session and still using CPU. Releasing the
        // semaphore in a `finally` at that moment let the next caller start a
        // second inference immediately, so a configured concurrency of 1 could
        // become 2, 3, N under a slow model: exactly the unbounded native
        // parallelism the gate exists to prevent, and the failure mode that
        // took the evaluation host down once already.
        //
        // So the release is attached to the WORK's completion, not to this
        // method's exit. On timeout the caller gets a sanitized failure now, and
        // the slot stays held until the native call actually returns.
        var work = Task.Run(() => Run(prepared, config, modelPath, tokenizerPath), CancellationToken.None);
        var released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) _gate.Release();
        }
        _ = work.ContinueWith(
            _ => ReleaseOnce(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var vector = timeoutSeconds > 0
                ? await work.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                : await work.WaitAsync(cancellationToken);
            return new TextEmbeddingResult(vector, config.Dimension, AiDistanceMetrics.Cosine);
        }
        catch (TimeoutException)
        {
            // Resumable, not fatal: the indexer keeps the text it already wrote
            // and reports the reason, and retrieval falls back to lexical.
            throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TextEmbeddingUnavailableException)
        {
            throw;
        }
        catch
        {
            // A native failure's message can name a model path. It never leaves
            // this method.
            throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingFailed);
        }
    }

    /// The inference call itself, as a seam a test can replace with controlled
    /// blocking work. Overridable rather than an interface because everything
    /// around it — the gate, the timeout, the sanitizing — is what is under
    /// test, and extracting those would test a different object.
    internal Func<string, TextEmbeddingModelConfig, string, string, float[]>? RunOverride { get; set; }

    private float[] Run(
        string text, TextEmbeddingModelConfig config, string modelPath, string tokenizerPath)
    {
        if (RunOverride is { } over) return over(text, config, modelPath, tokenizerPath);

        var tokenizer = _tokenizers.GetOrAdd(
            tokenizerPath,
            p => new Lazy<HuggingFaceTokenizer>(
                () => HuggingFaceTokenizer.FromFile(p),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        var encoding = tokenizer.Encode(text, addSpecialTokens: true, includeAttentionMask: true).First();
        var (ids, mask) = Truncate(
            encoding.Ids.Select(id => (long)id).ToArray(),
            encoding.AttentionMask.Select(m => (long)m).ToArray(),
            config.MaxTokens);

        var length = ids.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                config.InputIdsTensor, new DenseTensor<long>(ids, new[] { 1, length })),
            NamedOnnxValue.CreateFromTensor(
                config.AttentionMaskTensor, new DenseTensor<long>(mask, new[] { 1, length })),
        };

        _factory.EnsureNativeProviderInitialized();
        using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.RagText, modelPath));
        var session = lease.Session;

        // Some exports of BERT-family encoders keep `token_type_ids` in the
        // graph and some drop it. Supplying an input the graph does not declare
        // is an error, and omitting one it does declare is a different error, so
        // the graph is asked rather than assumed.
        if (session.InputNames.Contains(config.TokenTypeIdsTensor, StringComparer.Ordinal))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                config.TokenTypeIdsTensor, new DenseTensor<long>(new long[length], new[] { 1, length })));
        }

        var outputs = session.Run(inputs);
        var output = outputs.FirstOrDefault(o =>
                         string.Equals(o.Name, config.OutputTensor, StringComparison.Ordinal))
                     is { Name: not null } named && named.Data.Length > 0
            ? named
            : outputs.First();

        var pooled = Pool(output, mask, config);
        return Finalize(pooled, config);
    }

    /// Truncation that keeps the sequence WELL-FORMED.
    ///
    /// A plain `Take(max)` drops the closing separator token, and the model was
    /// trained to expect it — the resulting vector is not obviously wrong, it is
    /// quietly displaced. So the tail token is carried onto the end of the
    /// truncated sequence.
    internal static (long[] Ids, long[] Mask) Truncate(
        IReadOnlyList<long> ids, IReadOnlyList<long> attentionMask, int maxTokens)
    {
        var limit = Math.Max(2, maxTokens);
        if (ids.Count <= limit) return (ids.ToArray(), attentionMask.ToArray());

        var separator = ids[^1];
        var kept = ids.Take(limit - 1).Append(separator).ToArray();
        // Every kept position is real text, so every position is attended. The
        // source mask's trailing zeros belonged to padding we just removed.
        var mask = attentionMask.Take(limit - 1).Append(1L).ToArray();
        return (kept, mask);
    }

    private static float[] Pool(OnnxOutputTensor output, long[] mask, TextEmbeddingModelConfig config)
    {
        var dimension = config.Dimension;

        // A pre-pooled 2-D output ([1, dim]) is already a sentence vector.
        if (output.Shape.Count == 2 && output.Shape[1] == dimension)
        {
            return output.Data[..dimension].ToArray();
        }

        if (output.Shape.Count != 3 || output.Shape[2] != dimension)
        {
            throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingDimensionUnsupported);
        }

        var tokens = output.Shape[1];
        var pooled = new float[dimension];

        if (config.Pooling == TextEmbeddingPooling.Cls)
        {
            Array.Copy(output.Data, 0, pooled, 0, dimension);
            return pooled;
        }

        // Masked mean. Unmasked mean would average the padding positions in,
        // which moves short texts toward one point in the space.
        double weight = 0;
        for (var t = 0; t < tokens; t++)
        {
            if (t < mask.Length && mask[t] == 0) continue;
            weight++;
            var offset = t * dimension;
            for (var d = 0; d < dimension; d++) pooled[d] += output.Data[offset + d];
        }
        if (weight <= 0) throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingFailed);
        for (var d = 0; d < dimension; d++) pooled[d] = (float)(pooled[d] / weight);
        return pooled;
    }

    private static float[] Finalize(float[] vector, TextEmbeddingModelConfig config)
    {
        double sumSquares = 0;
        foreach (var value in vector)
        {
            // A NaN reaching pgvector is a row that poisons every subsequent
            // distance computation, so it is refused at the source.
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingFailed);
            }
            sumSquares += (double)value * value;
        }

        if (!config.Normalize) return vector;

        var norm = Math.Sqrt(sumSquares);
        if (norm <= double.Epsilon)
        {
            throw new TextEmbeddingUnavailableException(RagFailureReasons.EmbeddingFailed);
        }
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    private static string ModelPath(string modelDir, TextEmbeddingModelConfig config)
        => Path.Combine(modelDir, config.ModelSubdir, config.ModelFile);

    private static string TokenizerPath(string modelDir, TextEmbeddingModelConfig config)
        => Path.Combine(modelDir, config.ModelSubdir, config.TokenizerFile);

    public void Dispose()
    {
        // Sessions are owned and disposed by IOnnxInferenceSessionFactory; this
        // provider owns the tokenizer cache and the concurrency gate.
        foreach (var tokenizer in _tokenizers.Values)
        {
            if (tokenizer.IsValueCreated) tokenizer.Value.Dispose();
        }
        _tokenizers.Clear();
        _gate.Dispose();
    }
}

/// A local embedding could not be produced. Carries a sanitized reason code and
/// nothing else — no path, no native message, no text.
public sealed class TextEmbeddingUnavailableException(string reasonCode)
    : Exception($"Local text embedding unavailable ({reasonCode}).")
{
    public string ReasonCode { get; } = reasonCode;
}

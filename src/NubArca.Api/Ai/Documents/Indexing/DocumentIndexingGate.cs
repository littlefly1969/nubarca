using Microsoft.Extensions.Options;

namespace NubArca.Api.Ai.Documents.Indexing;

/// The process-wide resource bounds of automatic document indexing.
///
/// A singleton, so every job in this process shares it:
///
///  - `Jobs` — at most `Ai:DocumentIndexing:MaxConcurrentJobs` document jobs at
///    once (one by default), whatever `Jobs:MaxConcurrentJobs` allows;
///  - `Models` — the text-embedding and visual-embedding phases take this
///    one-slot gate, so the two large models never infer concurrently unless
///    `ParallelTextAndVisualModels` says they may. Each embedder's own
///    `Ai:MaxConcurrency` gate still applies underneath.
///
/// What it does NOT do is unload a model: an ONNX session, once created, stays
/// cached by its provider for the life of the process. The gate bounds the
/// compute peak (two inferences' activations at once), not residency.
public sealed class DocumentIndexingGate
{
    private readonly SemaphoreSlim _models = new(1, 1);
    private readonly bool _parallelModels;

    public DocumentIndexingGate(IOptions<AiOptions> options)
    {
        var indexing = options.Value.DocumentIndexing;
        Jobs = new SemaphoreSlim(indexing.EffectiveMaxConcurrentJobs, indexing.EffectiveMaxConcurrentJobs);
        _parallelModels = indexing.ParallelTextAndVisualModels;
    }

    public SemaphoreSlim Jobs { get; }

    /// Enter the model gate; dispose to leave. A no-op when parallel models are
    /// configured.
    public async Task<IDisposable> EnterModelAsync(CancellationToken cancellationToken)
    {
        if (_parallelModels) return NoopRelease.Instance;
        await _models.WaitAsync(cancellationToken);
        return new Release(_models);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    private sealed class NoopRelease : IDisposable
    {
        public static readonly NoopRelease Instance = new();
        public void Dispose() { }
    }
}

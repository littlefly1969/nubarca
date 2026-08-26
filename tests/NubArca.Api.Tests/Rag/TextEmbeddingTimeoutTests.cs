using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// What a slow model must NOT be able to do.
//
// ONNX Runtime's Run is a blocking native call, and the provider waits on it
// with a timeout. Timing out the WAIT proves only that we stopped waiting: the
// native inference is still running, still holding a session, still using CPU.
// Releasing the concurrency slot at that moment let the next caller start a
// second one immediately, so a configured concurrency of 1 could become N under
// a model slow enough to time out repeatedly — which is exactly the unbounded
// native parallelism the gate exists to prevent, and which has taken an
// evaluation host down before.
//
// No model weights are needed to prove any of this. The inference call is a
// seam, and controlled blocking work exercises the gate, the timeout and the
// sanitizing — which is all of what is under test.
public sealed class TextEmbeddingTimeoutTests
{
    private static readonly AiProfile Profile = new()
    {
        Id = Guid.NewGuid(),
        Key = RagTextEmbeddingModels.MultilingualE5SmallProfileKey,
        Capability = AiCapabilities.TextEmbedding,
        Modality = AiModalities.Text,
        Dimension = RagTextEmbeddingModels.MultilingualE5SmallDimension,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    [Fact]
    public async Task TimedOutInference_DoesNotReleaseConcurrencyUntilUnderlyingWorkCompletes()
    {
        using var blocking = new ManualResetEventSlim(false);
        var started = new SemaphoreSlim(0);
        var concurrent = 0;
        var peak = 0;

        using var provider = Provider(timeoutSeconds: 1, maxConcurrency: 1);
        provider.RunOverride = (_, config, _, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            started.Release();
            blocking.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Decrement(ref concurrent);
            return Vector(config.Dimension);
        };

        // First caller takes the slot and times out waiting.
        var first = Assert.ThrowsAsync<TextEmbeddingUnavailableException>(
            () => provider.EmbedAsync(Profile, "prima", TextEmbeddingInputKind.Query));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        var failure = await first;
        Assert.Equal(RagFailureReasons.EmbeddingTimeout, failure.ReasonCode);

        // The caller has its answer, and the native work is still running. A
        // second inference must NOT start.
        var second = provider.EmbedAsync(Profile, "seconda", TextEmbeddingInputKind.Query);
        var startedAgain = await started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(startedAgain,
            "a second inference started while the timed-out one was still running natively");
        Assert.Equal(1, Volatile.Read(ref peak));

        // Once the native call actually returns, the slot is handed over.
        blocking.Set();
        var result = await second.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(Profile.Dimension, result.Dimension);
        Assert.Equal(1, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task EmbeddingTimeout_IsSanitized()
    {
        using var provider = Provider(timeoutSeconds: 1);
        provider.RunOverride = (_, _, modelPath, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException($"native failure at {modelPath}");
        };

        var failure = await Assert.ThrowsAsync<TextEmbeddingUnavailableException>(
            () => provider.EmbedAsync(Profile, "domanda", TextEmbeddingInputKind.Query));

        Assert.Equal(RagFailureReasons.EmbeddingTimeout, failure.ReasonCode);
        // A reason code an operator maps to their own configuration, never a
        // filesystem layout and never a native message.
        Assert.DoesNotContain('/', failure.ReasonCode);
        Assert.DoesNotContain("/model", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Native_Failure_Is_Sanitized_Too()
    {
        using var provider = Provider(timeoutSeconds: 30);
        provider.RunOverride = (_, _, modelPath, _)
            => throw new InvalidOperationException($"boom at {modelPath}/model.onnx");

        var failure = await Assert.ThrowsAsync<TextEmbeddingUnavailableException>(
            () => provider.EmbedAsync(Profile, "domanda", TextEmbeddingInputKind.Passage));

        Assert.Equal(RagFailureReasons.EmbeddingFailed, failure.ReasonCode);
        Assert.DoesNotContain("model.onnx", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Successful_Call_Releases_Its_Slot()
    {
        // The other direction: the fix must not leak slots on the happy path,
        // or the second question ever asked would hang forever.
        using var provider = Provider(timeoutSeconds: 30, maxConcurrency: 1);
        provider.RunOverride = (_, config, _, _) => Vector(config.Dimension);

        for (var i = 0; i < 5; i++)
        {
            var result = await provider
                .EmbedAsync(Profile, $"domanda {i}", TextEmbeddingInputKind.Query)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Profile.Dimension, result.Dimension);
        }
    }

    [Fact]
    public void The_Timeout_Reason_Is_Distinct_From_A_Failure()
    {
        // An operator fixes them differently: a timeout means re-run the index
        // and it continues, a failure means the model is wrong or broken.
        Assert.NotEqual(RagFailureReasons.EmbeddingFailed, RagFailureReasons.EmbeddingTimeout);
        Assert.Equal("embedding-timeout", RagFailureReasons.ShortFallback(RagFailureReasons.EmbeddingTimeout));
    }

    // ---- harness -------------------------------------------------------------

    private static OnnxTextEmbeddingProvider Provider(int timeoutSeconds, int maxConcurrency = 1)
        => new(
            Options.Create(new AiOptions
            {
                TimeoutSeconds = timeoutSeconds,
                MaxConcurrency = maxConcurrency,
                // Readiness is satisfied by the seam below; the model directory
                // is asserted separately in TextEmbeddingProviderTests.
                Onnx = new AiOnnxOptions { ModelDir = TestModelDir() },
            }),
            new UnusedSessionFactory());

    /// A directory holding the two files CheckReadiness looks for. Their content
    /// is irrelevant: RunOverride replaces the only code that would open them.
    private static string TestModelDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "nubarca-rag-model-stub",
            RagTextEmbeddingModels.MultilingualE5SmallKey);
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "model.onnx", "tokenizer.json" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) File.WriteAllText(path, "stub");
        }
        return Path.GetDirectoryName(dir)!;
    }

    private static float[] Vector(int dimension)
    {
        var vector = new float[dimension];
        vector[0] = 1f;
        return vector;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed class UnusedSessionFactory : IOnnxInferenceSessionFactory
    {
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec)
            => OnnxSessionReadiness.Ready;

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
            => throw new InvalidOperationException("The inference seam is overridden in these tests.");

        public void EnsureNativeProviderInitialized() { }

        public OnnxNativeCoreState NativeCoreState => default;

        public void Dispose() { }
    }
}

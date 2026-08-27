using System.Security.Cryptography;
using System.Text;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Text;

namespace NubArca.Api.Ai.TextEmbeddings;

/// A hashed bag-of-tokens embedder for tests and offline development.
///
/// IT IS NOT A SEMANTIC MODEL and does not claim to be one. Two texts that mean
/// the same thing in different words land nowhere near each other; two texts
/// that share vocabulary land close. It exists so the substrate around
/// embeddings — profile scoping, canonical persistence, pgvector sync, ANN
/// ordering, dimension validation, idempotent reindexing — can be tested
/// exhaustively, in milliseconds, with no model weights and no Docker.
///
/// Retrieval QUALITY is never measured against it. That is what the golden
/// evaluation against the configured ONNX model is for, and conflating the two
/// would produce a benchmark that improves while the product gets worse.
///
/// The input kind still changes the vector, because a provider that ignored it
/// would let a bug where every passage is embedded as a query pass every test.
public sealed class DeterministicTextEmbeddingProvider : ITextEmbeddingProvider
{
    /// The dev/test profile key. Its dimension is the same 384 as the ONNX
    /// profile so both can exercise the same pgvector table.
    public const string ProfileKey = "rag-text-deterministic-v1";
    public const string ModelKey = "rag-text-deterministic-v1";
    public const int Dimension = 384;

    public string Provider => AiProviders.Deterministic;

    public TextEmbeddingReadiness CheckReadiness(AiProfile profile)
        => profile.Dimension is > 0
            ? TextEmbeddingReadiness.Ready
            : TextEmbeddingReadiness.NotReady(RagFailureReasons.EmbeddingProfileUnavailable);

    public Task<TextEmbeddingResult> EmbedAsync(
        AiProfile profile,
        string text,
        TextEmbeddingInputKind inputKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dimension = profile.Dimension is > 0 ? profile.Dimension.Value : Dimension;
        return Task.FromResult(new TextEmbeddingResult(
            Embed(text, inputKind, dimension), dimension, AiDistanceMetrics.Cosine));
    }

    /// Public and static so a test can compute the expected vector without a
    /// container, a profile or a service scope.
    public static float[] Embed(string? text, TextEmbeddingInputKind inputKind, int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        var vector = new float[dimension];

        // The same tokenizer the lexical path uses, so a fixture that reads
        // sensibly to a person also behaves sensibly here.
        var tokens = RagText.ContentTokens(text);
        var kindSalt = inputKind == TextEmbeddingInputKind.Query ? "q" : "p";

        foreach (var token in tokens)
        {
            var (bucket, sign) = Bucket($"{kindSalt}:{token}", dimension);
            vector[bucket] += sign;
        }

        // A text with no content tokens still needs a usable vector: an all-zero
        // one has no cosine direction and would be rejected downstream.
        if (tokens.Count == 0)
        {
            var (bucket, sign) = Bucket($"{kindSalt}:∅", dimension);
            vector[bucket] += sign;
        }

        Normalize(vector);
        return vector;
    }

    private static (int Bucket, float Sign) Bucket(string token, int dimension)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var value = BitConverter.ToUInt32(hash, 0);
        // The sign bit turns the hash into a signed random projection, so two
        // different tokens landing in one bucket are as likely to cancel as to
        // reinforce — which keeps collisions from reading as similarity.
        var sign = (hash[4] & 1) == 0 ? 1f : -1f;
        return ((int)(value % (uint)dimension), sign);
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        foreach (var value in vector) sumSquares += (double)value * value;
        var norm = Math.Sqrt(sumSquares);
        if (norm <= double.Epsilon) return;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }
}

using System.Security.Cryptography;
using System.Text;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Backends;

// ============================================================================
// DEV/TEST INFRASTRUCTURE ONLY — NOT A REAL MODEL.
//
// The deterministic backend produces STABLE outputs from stable inputs with no
// external dependency, no model file, and no network call. Its embeddings are
// reproducible (same bytes/string + capability => same vector) but carry NO
// semantic meaning: they exist so the substrate's plumbing (resolution,
// serialization, later jobs/search) can be exercised end-to-end in dev/tests
// before any real model exists. NEVER enable this provider in production for
// real results.
// ============================================================================
public sealed class DeterministicAiBackend
    : IImageEmbedder, ITextEmbedder, ITextExtractor, IFaceDetector, IFaceEmbedder, IImageCaptioner, IAiTagger
{
    // Fallback dimension when a profile does not specify one (embeddings only).
    private const int DefaultDimension = 32;

    private static readonly string[] SupportedCapabilities =
    {
        AiCapabilities.ImageEmbedding,
        AiCapabilities.DocumentEmbedding,
        AiCapabilities.DocumentExtraction,
        AiCapabilities.FaceDetection,
        AiCapabilities.FaceEmbedding,
        AiCapabilities.Tagging,
        AiCapabilities.Captioning,
    };

    public string Provider => AiProviders.Deterministic;

    public bool Supports(string capability) =>
        Array.IndexOf(SupportedCapabilities, capability) >= 0;

    public Task<AiEmbeddingResult> EmbedImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        => Task.FromResult(Embed(imageBytes.Span, AiCapabilities.ImageEmbedding, profile));

    public Task<AiEmbeddingResult> EmbedTextAsync(
        string text, AiProfile profile, CancellationToken cancellationToken = default)
        => Task.FromResult(Embed(Encoding.UTF8.GetBytes(text ?? string.Empty), AiCapabilities.DocumentEmbedding, profile));

    public Task<AiEmbeddingResult> EmbedFaceAsync(
        ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default)
        => Task.FromResult(Embed(faceCropBytes.Span, AiCapabilities.FaceEmbedding, profile));

    public Task<AiTextExtractionResult> ExtractTextAsync(
        ReadOnlyMemory<byte> contentBytes, string mimeType, AiProfile profile, CancellationToken cancellationToken = default)
    {
        // Stable placeholder text; not real OCR/extraction.
        var token = ShortToken(contentBytes.Span, AiCapabilities.DocumentExtraction);
        return Task.FromResult(new AiTextExtractionResult($"[deterministic:{token}]", "deterministic", null));
    }

    public Task<AiFaceDetectionResult> DetectFacesAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        => Task.FromResult(new AiFaceDetectionResult(DetectFacesDeterministic(imageBytes.Span)));

    // Stable, NON-SEMANTIC face detections so the persistence/embedding/coverage
    // plumbing can be exercised end-to-end in dev/tests without real weights:
    // 1 or 2 faces derived from the bytes, each with a normalized bbox + 5
    // landmarks. Same bytes => same faces (so re-detection is idempotent). Empty
    // input yields zero faces (exercises the legitimate zero-face path).
    private static IReadOnlyList<DetectedFace> DetectFacesDeterministic(ReadOnlySpan<byte> input)
    {
        if (input.Length == 0)
        {
            return Array.Empty<DetectedFace>();
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(AiCapabilities.FaceDetection));
        hasher.AppendData(input.ToArray());
        var h = hasher.GetHashAndReset();

        var faceCount = 1 + (h[0] % 2); // 1 or 2
        var faces = new List<DetectedFace>(faceCount);
        for (var i = 0; i < faceCount; i++)
        {
            var baseX = 0.1 + 0.4 * i;                 // 0.1 or 0.5
            var w = 0.30;
            var hgt = 0.30;
            var y = 0.20;
            var landmarks = new List<FaceLandmark>(5)
            {
                new(baseX + 0.08, y + 0.10), // left eye
                new(baseX + 0.22, y + 0.10), // right eye
                new(baseX + 0.15, y + 0.17), // nose
                new(baseX + 0.10, y + 0.24), // left mouth
                new(baseX + 0.20, y + 0.24), // right mouth
            };
            faces.Add(new DetectedFace(
                baseX, y, w, hgt,
                Confidence: 0.90 - 0.05 * i,
                Landmarks: landmarks));
        }

        return faces;
    }

    public Task<AiCaptionResult> CaptionImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
    {
        var token = ShortToken(imageBytes.Span, AiCapabilities.Captioning);
        return Task.FromResult(new AiCaptionResult($"[deterministic caption {token}]"));
    }

    public Task<AiTaggingResult> TagImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        // No semantic tags from a deterministic backend.
        => Task.FromResult(new AiTaggingResult(Array.Empty<AiTag>()));

    private static AiEmbeddingResult Embed(ReadOnlySpan<byte> input, string capabilitySalt, AiProfile profile)
    {
        var dimension = profile.Dimension is > 0 ? profile.Dimension.Value : DefaultDimension;
        var metric = string.IsNullOrWhiteSpace(profile.DistanceMetric)
            ? AiDistanceMetrics.Cosine
            : profile.DistanceMetric!;

        var vector = DeterministicVector(input, capabilitySalt, dimension);
        // Cosine-friendly: unit-normalize so deterministic vectors are directly
        // comparable by cosine/inner product in later phases.
        Normalize(vector);
        return new AiEmbeddingResult(vector, dimension, metric);
    }

    // Stable pseudo-vector: SHA-256(salt || input) seeds a SplitMix64 stream
    // expanded to `dimension` finite floats in [-1, 1]. Deterministic and never
    // NaN/Infinity. The capability salt makes the SAME bytes embed differently
    // per capability (e.g. image-embedding vs face-embedding).
    private static float[] DeterministicVector(ReadOnlySpan<byte> input, string salt, int dimension)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(salt));
        hasher.AppendData(input.ToArray());
        var seedBytes = hasher.GetHashAndReset();

        ulong state = BitConverter.ToUInt64(seedBytes, 0);
        var vector = new float[dimension];
        for (int i = 0; i < dimension; i++)
        {
            var u = NextDouble(ref state);    // [0, 1)
            vector[i] = (float)(u * 2.0 - 1.0); // [-1, 1)
        }

        return vector;
    }

    private static double NextDouble(ref ulong state)
    {
        // SplitMix64.
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        // Top 53 bits → double in [0, 1).
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        foreach (var v in vector)
        {
            sumSquares += (double)v * v;
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm <= double.Epsilon)
        {
            return;
        }

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }

    private static string ShortToken(ReadOnlySpan<byte> input, string salt)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(salt));
        hasher.AppendData(input.ToArray());
        var hash = hasher.GetHashAndReset();
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}

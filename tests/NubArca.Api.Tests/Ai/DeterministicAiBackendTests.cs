using NubArca.Api.Ai.Backends;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

// The deterministic backend is dev/test infrastructure: stable, non-semantic.
public sealed class DeterministicAiBackendTests
{
    private readonly DeterministicAiBackend _backend = new();

    private static AiProfile EmbeddingProfile(string capability) => new()
    {
        Id = Guid.NewGuid(),
        Key = "det-" + capability,
        Capability = capability,
        Modality = AiModalities.Image,
        Dimension = 32,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    [Fact]
    public void Provider_Is_Deterministic_And_Supports_Backend_Capabilities()
    {
        Assert.Equal(AiProviders.Deterministic, _backend.Provider);
        Assert.True(_backend.Supports(AiCapabilities.ImageEmbedding));
        Assert.True(_backend.Supports(AiCapabilities.FaceEmbedding));
        Assert.True(_backend.Supports(AiCapabilities.DocumentEmbedding));
        Assert.False(_backend.Supports("totally-unknown-capability"));
    }

    [Fact]
    public async Task EmbedImage_Is_Stable_For_Same_Bytes()
    {
        var profile = EmbeddingProfile(AiCapabilities.ImageEmbedding);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var a = await _backend.EmbedImageAsync(bytes, profile);
        var b = await _backend.EmbedImageAsync(bytes, profile);

        Assert.Equal(32, a.Dimension);
        Assert.Equal(a.Vector, b.Vector);
        Assert.All(a.Vector, v => Assert.True(float.IsFinite(v)));
        // Unit-normalized (cosine-friendly).
        Assert.Equal(1.0, Math.Sqrt(a.Vector.Sum(v => (double)v * v)), precision: 4);
    }

    [Fact]
    public async Task EmbedImage_Differs_For_Different_Bytes()
    {
        var profile = EmbeddingProfile(AiCapabilities.ImageEmbedding);

        var a = await _backend.EmbedImageAsync(new byte[] { 1, 2, 3 }, profile);
        var b = await _backend.EmbedImageAsync(new byte[] { 9, 9, 9 }, profile);

        Assert.NotEqual(a.Vector, b.Vector);
    }

    [Fact]
    public async Task Same_Bytes_Embed_Differently_Across_Capabilities()
    {
        // The capability salt makes image-embedding vs face-embedding produce
        // different vectors for the same input bytes.
        var bytes = new byte[] { 7, 7, 7, 7 };

        var image = await _backend.EmbedImageAsync(bytes, EmbeddingProfile(AiCapabilities.ImageEmbedding));
        var face = await _backend.EmbedFaceAsync(bytes, EmbeddingProfile(AiCapabilities.FaceEmbedding));

        Assert.NotEqual(image.Vector, face.Vector);
    }

    [Fact]
    public async Task EmbedText_Is_Stable_And_Differs_By_Content()
    {
        var profile = EmbeddingProfile(AiCapabilities.DocumentEmbedding);

        var hello1 = await _backend.EmbedTextAsync("hello", profile);
        var hello2 = await _backend.EmbedTextAsync("hello", profile);
        var world = await _backend.EmbedTextAsync("world", profile);

        Assert.Equal(hello1.Vector, hello2.Vector);
        Assert.NotEqual(hello1.Vector, world.Vector);
    }

    [Fact]
    public async Task Text_And_Image_Embeddings_Differ_For_Equivalent_Input()
    {
        // "abc" as text vs the same bytes as an image embed to different vectors.
        var bytes = new byte[] { (byte)'a', (byte)'b', (byte)'c' };

        var text = await _backend.EmbedTextAsync("abc", EmbeddingProfile(AiCapabilities.DocumentEmbedding));
        var image = await _backend.EmbedImageAsync(bytes, EmbeddingProfile(AiCapabilities.ImageEmbedding));

        Assert.NotEqual(text.Vector, image.Vector);
    }

    [Fact]
    public async Task Non_Embedding_Capabilities_Return_Stable_Placeholders()
    {
        var profile = EmbeddingProfile(AiCapabilities.Captioning);

        var caption = await _backend.CaptionImageAsync(new byte[] { 1 }, profile);
        var extraction = await _backend.ExtractTextAsync(new byte[] { 1 }, "text/plain", profile);
        var tags = await _backend.TagImageAsync(new byte[] { 1 }, profile);

        Assert.False(string.IsNullOrWhiteSpace(caption.Caption));
        Assert.Equal("deterministic", extraction.Source);
        Assert.Empty(tags.Tags);

        // Face Substrate v0: the deterministic backend now emits STABLE, non-
        // semantic faces (with landmarks) so the face persistence/embedding
        // plumbing can be exercised in dev/tests without real weights. Same bytes
        // => same faces (idempotent); empty input => zero faces.
        var faces = await _backend.DetectFacesAsync(new byte[] { 1 }, profile);
        var facesAgain = await _backend.DetectFacesAsync(new byte[] { 1 }, profile);
        Assert.NotEmpty(faces.Faces);
        Assert.All(faces.Faces, f => Assert.NotNull(f.Landmarks));
        Assert.Equal(faces.Faces.Count, facesAgain.Faces.Count);
        Assert.Empty((await _backend.DetectFacesAsync(Array.Empty<byte>(), profile)).Faces);
    }
}

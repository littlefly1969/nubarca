using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// WHICH MODEL READS DOCUMENTS HERE — decided by one configuration key, and
// FAILING CLOSED on everything else.
//
// The substrate rule is that model identity is explicit. There is no "latest
// installed", no timestamp comparison and no capability default to fall back
// to, because a fallback is how a second profile appearing in the catalogue
// silently reinterprets everybody's documents in a different coordinate system.
//
// Every refusal below degrades to the ALREADY-VALID text retrieval path and is
// never recorded as a verdict about somebody's document.
public sealed class DocumentVisualProfileTests : IDisposable
{
    private readonly DocumentVisualHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private DocumentVisualProfileResolver Resolver(
        DocumentVisualOptions options, params IAiBackend[] backends)
        => new(
            new AiBackendResolver(
                Options.Create(new AiOptions { Enabled = true, Provider = AiProviders.Onnx }),
                new AiProfileRegistry(_harness.Db, TimeProvider.System),
                backends),
            new AiProfileRegistry(_harness.Db, TimeProvider.System),
            Options.Create(options));

    private static DocumentVisualOptions Enabled(string? key = null) => new()
    {
        Enabled = true,
        DenseProfileKey = key ?? DocumentVisualProfiles.DenseSiglip2So400m,
    };

    [Fact]
    public async Task A_Correctly_Configured_Profile_Resolves_Both_Towers()
    {
        _harness.SeedProfile();

        var resolution = await Resolver(Enabled(), new BothTowers()).ResolveAsync();

        Assert.True(resolution.IsAvailable, resolution.Reason);
        Assert.NotNull(resolution.Pages);
        Assert.NotNull(resolution.Queries);
        Assert.Equal(DocumentVisualProfiles.DenseDimension, resolution.Profile!.Dimension);
    }

    [Fact]
    public async Task Disabled_Is_The_Default_And_Resolves_To_Nothing()
    {
        _harness.SeedProfile();

        var resolution = await Resolver(new DocumentVisualOptions(), new BothTowers()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DocumentVisualReasons.Disabled, resolution.Reason);
    }

    [Fact]
    public async Task An_Unknown_Profile_Key_Fails_Closed()
    {
        _harness.SeedProfile();

        var resolution = await Resolver(Enabled("no-such-profile"), new BothTowers()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DocumentVisualReasons.ModelUnavailable, resolution.Reason);
    }

    [Fact]
    public async Task Pointing_It_At_The_Photo_Profile_Is_Refused()
    {
        // SAME WEIGHTS, SAME DIMENSION — so this would "work", and would write
        // document vectors under the photo profile's identity where a photo
        // reindex would later delete them. The capability is checked rather than
        // inferred from the key.
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = OnnxImageModels.SiglipSo400mKey,
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = OnnxImageModels.SiglipSo400mProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _harness.Db.SaveChangesAsync();

        var resolution = await Resolver(
            Enabled(OnnxImageModels.SiglipSo400mProfileKey), new BothTowers()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DocumentVisualReasons.ModelUnavailable, resolution.Reason);
    }

    [Fact]
    public async Task A_Wrong_Dimension_Is_Refused_Rather_Than_Coerced()
    {
        var profile = _harness.SeedProfile();
        var tracked = await _harness.Db.AiProfiles.SingleAsync(p => p.Id == profile.Id);
        tracked.Dimension = 768;
        await _harness.Db.SaveChangesAsync();

        var resolution = await Resolver(Enabled(), new BothTowers()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DocumentVisualReasons.ModelOutputUnsupported, resolution.Reason);
    }

    [Fact]
    public async Task A_Disabled_Profile_Is_Refused()
    {
        var profile = _harness.SeedProfile();
        var tracked = await _harness.Db.AiProfiles.SingleAsync(p => p.Id == profile.Id);
        tracked.Enabled = false;
        await _harness.Db.SaveChangesAsync();

        var resolution = await Resolver(Enabled(), new BothTowers()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal(DocumentVisualReasons.ModelUnavailable, resolution.Reason);
    }

    [Fact]
    public async Task A_Missing_Text_Tower_Makes_The_Whole_Profile_Unavailable()
    {
        // BOTH TOWERS OR NEITHER. Indexing pages into a space no question can be
        // asked in would spend hours of local inference building a corpus
        // nothing can search — worse than being unavailable, because it looks
        // like progress.
        _harness.SeedProfile();

        var resolution = await Resolver(Enabled(), new ImageTowerOnly()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.NotNull(resolution.Reason);
    }

    [Fact]
    public async Task A_Model_That_Is_Not_On_Disk_Reports_Unavailable_And_Downloads_Nothing()
    {
        // The readiness reason travels through unchanged, and NOTHING in the
        // resolution path can fetch a checkpoint: there is no HTTP client here,
        // no URL, and no code that writes to the model directory.
        _harness.SeedProfile();

        var resolution = await Resolver(Enabled(), new NotOnDisk()).ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Equal("onnx-model-not-found", resolution.Reason);
    }

    [Fact]
    public void The_Document_Profile_Key_Is_Not_The_Photo_Profile_Key()
    {
        // Shared weights, separate identity. If these ever became equal, a
        // document reindex would touch the photo library and `ai status` could
        // not say which of the two is configured.
        Assert.NotEqual(
            OnnxImageModels.SiglipSo400mProfileKey, DocumentVisualProfiles.DenseSiglip2So400m);
        Assert.NotEqual(
            AiCapabilities.ImageEmbedding, AiCapabilities.DocumentVisualEmbedding);
    }

    [Fact]
    public async Task Seeding_Reuses_The_Existing_SigLIP2_Model_Row_And_Is_Idempotent()
    {
        var registry = new AiProfileRegistry(_harness.Db, TimeProvider.System);

        var first = await registry.SeedDocumentVisualProfilesAsync();
        Assert.Equal(1, first.ProfilesCreated);

        var second = await registry.SeedDocumentVisualProfilesAsync();
        Assert.Equal(0, second.ProfilesCreated);
        Assert.Equal(0, second.ModelsCreated);

        var profile = await _harness.Db.AiProfiles
            .SingleAsync(p => p.Key == DocumentVisualProfiles.DenseSiglip2So400m);
        Assert.Equal(AiCapabilities.DocumentVisualEmbedding, profile.Capability);
        Assert.Equal(DocumentVisualProfiles.DenseDimension, profile.Dimension);
        // NEVER THE CAPABILITY DEFAULT: which profile embeds document pages is
        // stated explicitly, so a newer one cannot become active by existing.
        Assert.False(profile.IsDefault);

        // One model row, shared with the photo profile.
        Assert.Equal(1, await _harness.Db.AiModels.CountAsync(m => m.Key == OnnxImageModels.SiglipSo400mKey));
    }

    // ---- towers --------------------------------------------------------------

    private sealed class BothTowers : IImageEmbedder, ITextEmbedder
    {
        public string Provider => AiProviders.Onnx;
        public bool Supports(string capability) =>
            capability is AiCapabilities.ImageEmbedding or AiCapabilities.DocumentVisualEmbedding;
        public AiBackendReadiness CheckReadiness(AiProfile profile) => AiBackendReadiness.Ready;
        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(
                new float[DocumentVisualHarness.Dimension], DocumentVisualHarness.Dimension,
                AiDistanceMetrics.Cosine));
        public Task<AiEmbeddingResult> EmbedTextAsync(
            string text, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(
                new float[DocumentVisualHarness.Dimension], DocumentVisualHarness.Dimension,
                AiDistanceMetrics.Cosine));
    }

    private sealed class ImageTowerOnly : IImageEmbedder
    {
        public string Provider => AiProviders.Onnx;
        public bool Supports(string capability) =>
            capability is AiCapabilities.ImageEmbedding or AiCapabilities.DocumentVisualEmbedding;
        public AiBackendReadiness CheckReadiness(AiProfile profile) => AiBackendReadiness.Ready;
        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(
                new float[DocumentVisualHarness.Dimension], DocumentVisualHarness.Dimension,
                AiDistanceMetrics.Cosine));
    }

    private sealed class NotOnDisk : IImageEmbedder, ITextEmbedder
    {
        public string Provider => AiProviders.Onnx;
        public bool Supports(string capability) =>
            capability is AiCapabilities.ImageEmbedding or AiCapabilities.DocumentVisualEmbedding;
        public AiBackendReadiness CheckReadiness(AiProfile profile)
            => AiBackendReadiness.NotReady("onnx-model-not-found");
        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AiEmbeddingResult> EmbedTextAsync(
            string text, AiProfile profile, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

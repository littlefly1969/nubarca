using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

// Phase 2A: pure ONNX post-processing + backend readiness. No ONNX weights and
// no DB are required — CheckReadiness only inspects config + file presence, and
// Finalize is a pure function.
public sealed class OnnxImageEmbeddingTests
{
    // ---- OnnxImageEmbeddings.Finalize (dimension / NaN / L2) ----

    [Fact]
    public void Finalize_Rejects_Dimension_Mismatch()
    {
        Assert.Throws<ArgumentException>(() => OnnxImageEmbeddings.Finalize(new[] { 1f, 2f, 3f }, expectedDimension: 4));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Finalize_Rejects_Non_Finite(float bad)
    {
        Assert.Throws<ArgumentException>(() => OnnxImageEmbeddings.Finalize(new[] { 1f, bad }, expectedDimension: 2));
    }

    [Fact]
    public void Finalize_L2_Normalizes()
    {
        var v = OnnxImageEmbeddings.Finalize(new[] { 3f, 4f }, expectedDimension: 2);
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);
        Assert.Equal(0.6f, v[0], precision: 5);
        Assert.Equal(0.8f, v[1], precision: 5);
    }

    [Fact]
    public void Finalize_Returns_Zero_Vector_Unchanged()
    {
        var v = OnnxImageEmbeddings.Finalize(new[] { 0f, 0f, 0f }, expectedDimension: 3);
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    // ---- OnnxImageEmbedder.CheckReadiness (environment/config state) ----

    private static OnnxImageEmbedder Embedder(string? modelDir)
    {
        var options = Options.Create(new AiOptions { Onnx = new AiOnnxOptions { ModelDir = modelDir } });
        return new OnnxImageEmbedder(
            options, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance,
            new OnnxInferenceSessionFactory(options, NullLogger<OnnxInferenceSessionFactory>.Instance));
    }

    private static AiProfile SiglipSo400mProfile() => new()
    {
        Id = Guid.NewGuid(),
        Key = OnnxImageModels.SiglipSo400mProfileKey,
        ConfigHash = OnnxImageModels.SiglipSo400mKey,
        Capability = AiCapabilities.ImageEmbedding,
        Modality = AiModalities.Image,
        Dimension = 1152,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    [Fact]
    public void Readiness_NotReady_When_ModelDir_Not_Configured()
    {
        var r = Embedder(modelDir: null).CheckReadiness(SiglipSo400mProfile());
        Assert.False(r.IsReady);
        Assert.Equal("onnx-modeldir-not-configured", r.Reason);
    }

    [Fact]
    public void Readiness_NotReady_When_Model_File_Missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"onnx-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var r = Embedder(modelDir: dir).CheckReadiness(SiglipSo400mProfile());
            Assert.False(r.IsReady);
            Assert.Equal("onnx-model-not-found", r.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Readiness_NotReady_For_Unknown_Model()
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "not-a-known-onnx-profile",
            ConfigHash = null,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Enabled = true,
        };
        var r = Embedder(modelDir: "/tmp").CheckReadiness(profile);
        Assert.False(r.IsReady);
        Assert.Equal("onnx-unknown-model", r.Reason);
    }

    [Fact]
    public void Readiness_Ready_When_Model_File_Present()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"onnx-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, OnnxImageModels.SiglipSo400mKey));
        File.WriteAllText(Path.Combine(dir, OnnxImageModels.SiglipSo400mKey, OnnxImageModels.DefaultModelFile), "dummy");
        try
        {
            var r = Embedder(modelDir: dir).CheckReadiness(SiglipSo400mProfile());
            Assert.True(r.IsReady);
            Assert.Null(r.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Provider_And_Supports()
    {
        var e = Embedder(modelDir: null);
        Assert.Equal(AiProviders.Onnx, e.Provider);
        Assert.True(e.Supports(AiCapabilities.ImageEmbedding));
        Assert.False(e.Supports(AiCapabilities.FaceEmbedding));
    }
}

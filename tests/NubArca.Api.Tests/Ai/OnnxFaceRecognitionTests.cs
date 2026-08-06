using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

// Pure face-harness math + backend readiness. No ONNX weights and no DB required:
// the SCRFD anchor decode + NMS and the 5-point similarity alignment are pure
// functions, and CheckReadiness only inspects config + file presence.
public sealed class OnnxFaceRecognitionTests
{
    // ---- OnnxFaceBackend.CheckReadiness (environment/config state) ----

    private static OnnxFaceBackend Backend(string? modelDir)
    {
        var options = Options.Create(new AiOptions { Onnx = new AiOnnxOptions { ModelDir = modelDir } });
        var factory = new NubArca.Api.Ai.Onnx.OnnxInferenceSessionFactory(
            options, NullLogger<NubArca.Api.Ai.Onnx.OnnxInferenceSessionFactory>.Instance);
        return new OnnxFaceBackend(options, new OnnxFacePreprocessor(), NullLogger<OnnxFaceBackend>.Instance, factory);
    }

    private static AiProfile Antelopev2Profile() => new()
    {
        Id = Guid.NewGuid(),
        Key = OnnxFaceModels.Antelopev2ProfileKey,
        ConfigHash = OnnxFaceModels.Antelopev2Key,
        Capability = AiCapabilities.FaceEmbedding,
        Modality = AiModalities.Face,
        Dimension = 512,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    [Fact]
    public void Readiness_NotReady_When_ModelDir_Not_Configured()
    {
        var r = Backend(modelDir: null).CheckReadiness(Antelopev2Profile());
        Assert.False(r.IsReady);
        Assert.Equal("onnx-modeldir-not-configured", r.Reason);
    }

    [Fact]
    public void Readiness_NotReady_When_Detector_Missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"face-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var r = Backend(modelDir: dir).CheckReadiness(Antelopev2Profile());
            Assert.False(r.IsReady);
            Assert.Equal("onnx-face-detector-not-found", r.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Readiness_NotReady_When_Recognition_Missing()
    {
        var config = OnnxFaceModels.Catalog[OnnxFaceModels.Antelopev2Key];
        var dir = Path.Combine(Path.GetTempPath(), $"face-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, config.PackageSubdir));
        File.WriteAllText(Path.Combine(dir, config.PackageSubdir, config.DetectorFile), "dummy");
        try
        {
            var r = Backend(modelDir: dir).CheckReadiness(Antelopev2Profile());
            Assert.False(r.IsReady);
            Assert.Equal("onnx-face-recognition-not-found", r.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Readiness_Ready_When_Both_Present()
    {
        var config = OnnxFaceModels.Catalog[OnnxFaceModels.Antelopev2Key];
        var dir = Path.Combine(Path.GetTempPath(), $"face-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, config.PackageSubdir));
        File.WriteAllText(Path.Combine(dir, config.PackageSubdir, config.DetectorFile), "d");
        File.WriteAllText(Path.Combine(dir, config.PackageSubdir, config.RecognitionFile), "r");
        try
        {
            var r = Backend(modelDir: dir).CheckReadiness(Antelopev2Profile());
            Assert.True(r.IsReady);
            Assert.Null(r.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Readiness_NotReady_For_Unknown_Face_Model()
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "not-a-known-face-profile",
            ConfigHash = null,
            Capability = AiCapabilities.FaceEmbedding,
            Enabled = true,
        };
        var r = Backend(modelDir: "/tmp").CheckReadiness(profile);
        Assert.False(r.IsReady);
        Assert.Equal("onnx-unknown-face-model", r.Reason);
    }

    [Fact]
    public void Provider_And_Supports()
    {
        var b = Backend(modelDir: null);
        Assert.Equal(AiProviders.Onnx, b.Provider);
        Assert.True(b.Supports(AiCapabilities.FaceDetection));
        Assert.True(b.Supports(AiCapabilities.FaceEmbedding));
        Assert.False(b.Supports(AiCapabilities.ImageEmbedding));
    }

    // ---- ScrfdDecoder (pure anchor decode + NMS) ----

    // Single stride-8 branch on a 16×16 input → 2×2 grid, one anchor per cell.
    private static IReadOnlyList<ScrfdDecoder.RawOutput> SingleCellOutputs(
        float score, float l, float t, float r, float b, int hotIndex)
    {
        var scores = new float[4];
        scores[hotIndex] = score;
        var bbox = new float[16];
        bbox[hotIndex * 4 + 0] = l;
        bbox[hotIndex * 4 + 1] = t;
        bbox[hotIndex * 4 + 2] = r;
        bbox[hotIndex * 4 + 3] = b;
        return new List<ScrfdDecoder.RawOutput>
        {
            new(scores, new[] { 4, 1 }),
            new(bbox, new[] { 4, 4 }),
        };
    }

    [Fact]
    public void Scrfd_Decodes_A_Single_Box_In_Input_Pixel_Space()
    {
        // Hot cell index 1 = (row 0, col 1) → center (8, 0) at stride 8.
        var outputs = SingleCellOutputs(score: 0.9f, l: 0.5f, t: 0f, r: 0.5f, b: 1f, hotIndex: 1);
        var faces = ScrfdDecoder.Decode(outputs, 16, 16, scoreThreshold: 0.5f, nmsThreshold: 0.4f, out var diag);

        Assert.Null(diag);
        var f = Assert.Single(faces);
        Assert.Equal(0.9f, f.Score, 3);
        // cx=8, cy=0; distances *stride(8): l=4,t=0,r=4,b=8.
        Assert.Equal(4f, f.X1, 3);
        Assert.Equal(0f, f.Y1, 3);
        Assert.Equal(12f, f.X2, 3);
        Assert.Equal(8f, f.Y2, 3);
        Assert.Null(f.Landmarks); // no kps branch supplied
    }

    [Fact]
    public void Scrfd_Filters_By_Score_Threshold()
    {
        var outputs = SingleCellOutputs(score: 0.3f, l: 1f, t: 1f, r: 1f, b: 1f, hotIndex: 2);
        var faces = ScrfdDecoder.Decode(outputs, 16, 16, scoreThreshold: 0.5f, nmsThreshold: 0.4f, out _);
        Assert.Empty(faces);
    }

    [Fact]
    public void Scrfd_Nms_Suppresses_Overlapping_Lower_Score_Box()
    {
        // Two adjacent hot cells whose decoded boxes overlap heavily; NMS keeps
        // only the higher-scoring one.
        var scores = new float[4];
        scores[0] = 0.95f; // cell (0,0), center (0,0)
        scores[1] = 0.80f; // cell (0,1), center (8,0)
        var bbox = new float[16];
        // Both boxes cover roughly the same large region → high IoU.
        for (var i = 0; i < 4; i++)
        {
            bbox[0 * 4 + i] = 2f;
            bbox[1 * 4 + i] = 2f;
        }
        var outputs = new List<ScrfdDecoder.RawOutput>
        {
            new(scores, new[] { 4, 1 }),
            new(bbox, new[] { 4, 4 }),
        };

        var faces = ScrfdDecoder.Decode(outputs, 16, 16, scoreThreshold: 0.5f, nmsThreshold: 0.4f, out _);
        var f = Assert.Single(faces);
        Assert.Equal(0.95f, f.Score, 3);
    }

    [Fact]
    public void Scrfd_Reports_Diagnostic_On_Unexpected_Shapes()
    {
        // Only a bbox branch, no score branch → shape mismatch.
        var outputs = new List<ScrfdDecoder.RawOutput> { new(new float[16], new[] { 4, 4 }) };
        var faces = ScrfdDecoder.Decode(outputs, 16, 16, 0.5f, 0.4f, out var diag);
        Assert.Empty(faces);
        Assert.Equal("detector-output-shape-unexpected", diag);
    }

    [Fact]
    public void Scrfd_Decodes_Landmarks_When_Kps_Branch_Present()
    {
        var scores = new float[4];
        scores[0] = 0.9f;
        var bbox = new float[16];
        for (var i = 0; i < 4; i++) bbox[i] = 1f;
        var kps = new float[40]; // 4 cells × 10
        // 5 landmarks for cell 0 (center 0,0): each (1,1) in stride units → (8,8).
        for (var k = 0; k < 10; k++) kps[k] = 1f;
        var outputs = new List<ScrfdDecoder.RawOutput>
        {
            new(scores, new[] { 4, 1 }),
            new(bbox, new[] { 4, 4 }),
            new(kps, new[] { 4, 10 }),
        };

        var faces = ScrfdDecoder.Decode(outputs, 16, 16, 0.5f, 0.4f, out _);
        var f = Assert.Single(faces);
        Assert.NotNull(f.Landmarks);
        Assert.Equal(10, f.Landmarks!.Length);
        Assert.Equal(8f, f.Landmarks[0], 3);
        Assert.Equal(8f, f.Landmarks[1], 3);
    }

    // ---- FaceAlignment (5-point least-squares similarity) ----

    private static readonly float[] Reference112 =
    {
        38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f,
    };

    [Fact]
    public void Alignment_Identity_When_Landmarks_Equal_Reference()
    {
        Assert.True(FaceAlignment.TryEstimateSimilarity(Reference112, 112, out var m));
        for (var i = 0; i < 5; i++)
        {
            var p = Vector2.Transform(new Vector2(Reference112[i * 2], Reference112[i * 2 + 1]), m);
            Assert.Equal(Reference112[i * 2], p.X, 3);
            Assert.Equal(Reference112[i * 2 + 1], p.Y, 3);
        }
    }

    [Fact]
    public void Alignment_Recovers_Scale_And_Translation()
    {
        // Source = reference scaled ×2 and shifted by (10, 20). The estimated
        // transform must map each source point back onto the reference.
        var src = new float[10];
        for (var i = 0; i < 5; i++)
        {
            src[i * 2] = Reference112[i * 2] * 2f + 10f;
            src[i * 2 + 1] = Reference112[i * 2 + 1] * 2f + 20f;
        }

        Assert.True(FaceAlignment.TryEstimateSimilarity(src, 112, out var m));
        for (var i = 0; i < 5; i++)
        {
            var p = Vector2.Transform(new Vector2(src[i * 2], src[i * 2 + 1]), m);
            Assert.Equal(Reference112[i * 2], p.X, 2);
            Assert.Equal(Reference112[i * 2 + 1], p.Y, 2);
        }
    }

    [Fact]
    public void Alignment_Fails_On_Too_Few_Landmarks()
    {
        Assert.False(FaceAlignment.TryEstimateSimilarity(new float[] { 1, 2, 3, 4 }, 112, out _));
    }
}

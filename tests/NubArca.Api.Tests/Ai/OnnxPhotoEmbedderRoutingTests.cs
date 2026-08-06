using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// SigLIP direct milestone: provider routing for the migrated PHOTO IMAGE and
// PHOTO TEXT towers. Fakes only — no GPU, no model weights. Proves:
//   onnxruntime / openvino-direct → IOnnxInferenceSessionFactory (in-process)
// no silent fallback, factory-owned session, lease disposed, normalization/dim
// checks retained, cancellation, and compile-backed readiness. Tokenization
// stays in .NET in EVERY mode (the minimal fixed-64 tokenizer fixture below).
public sealed class OnnxPhotoEmbedderRoutingTests : IDisposable
{
    private const int Dim = 1152;
    private readonly List<string> _tempDirs = new();

    // Minimal valid HF tokenizer with SigLIP2's fixed-64 truncation+padding
    // policy (same fixture as OnnxTextEmbeddingTests).
    private const string TokenizerJson = """
    {
      "version":"1.0",
      "truncation":{"direction":"Right","max_length":64,"strategy":"LongestFirst","stride":0},
      "padding":{"strategy":{"Fixed":64},"direction":"Right","pad_to_multiple_of":null,"pad_id":0,"pad_type_id":0,"pad_token":"<pad>"},
      "added_tokens":[
        {"id":0,"content":"<pad>","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
        {"id":1,"content":"<unk>","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true}
      ],
      "normalizer":{"type":"Lowercase"},
      "pre_tokenizer":{"type":"Whitespace"},
      "post_processor":null,
      "decoder":null,
      "model":{"type":"WordLevel","vocab":{"<pad>":0,"<unk>":1,"hello":2},"unk_token":"<unk>"}
    }
    """;

    private string TempModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "photomodels-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, OnnxImageModels.SiglipSo400mKey);
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultModelFile), "x");
        File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultTextModelFile), "x");
        File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultTokenizerFile), TokenizerJson);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs) { try { Directory.Delete(d, true); } catch { /* best effort */ } }
    }

    private static byte[] TinyImage()
    {
        using var img = new Image<Rgb24>(32, 32);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static AiProfile PhotoProfile() => new()
    {
        Id = Guid.NewGuid(),
        Key = OnnxImageModels.SiglipSo400mProfileKey,
        ConfigHash = OnnxImageModels.SiglipSo400mKey,
        Capability = AiCapabilities.ImageEmbedding,
        Modality = AiModalities.Image,
        Dimension = Dim,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
    };

    private static AiOptions BuildOptions(string provider, string modelDir)
    {
        var onnx = new AiOnnxOptions { ModelDir = modelDir, ExecutionProvider = provider };
        if (provider == "openvino-direct") onnx.OpenVino.NativeDir = "/opt/ort";
        return new AiOptions { Onnx = onnx };
    }

    private OnnxImageEmbedder BuildImage(string provider, FakeFactory factory, string? modelDir = null)
    {
        var options = Options.Create(BuildOptions(provider, modelDir ?? TempModelDir()));
        return new OnnxImageEmbedder(
            options, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance, factory);
    }

    private OnnxTextEmbedder BuildText(string provider, FakeFactory factory, string? modelDir = null)
    {
        var options = Options.Create(BuildOptions(provider, modelDir ?? TempModelDir()));
        return new OnnxTextEmbedder(options, factory);
    }

    // ---- fakes ----

    private sealed class FakeFactory : IOnnxInferenceSessionFactory
    {
        public int AcquireCount, InitCount, LeaseDisposeCount;
        public OnnxModel? LastModel;
        public Exception? AcquireThrows;
        public readonly FakeSession Session = new();
        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;
        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
        {
            Interlocked.Increment(ref AcquireCount);
            LastModel = spec.Model;
            if (AcquireThrows is not null) throw AcquireThrows;
            return new Lease(this);
        }
        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.OpenVinoCore;
        public void Dispose() { }
        private sealed class Lease(FakeFactory f) : IOnnxSessionLease
        {
            public IOnnxSession Session => f.Session;
            public void Dispose() => Interlocked.Increment(ref f.LeaseDisposeCount);
        }
    }

    private sealed class FakeSession : IOnnxSession
    {
        public int DisposeCount;
        public string OutputName = "image_embeds";
        public float[] Output = NormalizableVector(Dim);
        public IReadOnlyList<string> InputNames => new[] { "pixel_values" };
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
            => new[] { new OnnxOutputTensor(OutputName, Output, new[] { 1, Output.Length }) };
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static float[] NormalizableVector(int dim)
    {
        var v = new float[dim];
        v[0] = 3f; v[1] = 4f; // ||v|| = 5 → normalizes to unit length
        return v;
    }

    // ---- image routing ----

    [Fact]
    public async Task Image_OnnxRuntime_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        var embedder = BuildImage("onnxruntime", factory);

        var result = await embedder.EmbedImageAsync(TinyImage(), PhotoProfile());

        Assert.Equal(Dim, result.Dimension);
        Assert.Equal(1, factory.AcquireCount);
        Assert.Equal(OnnxModel.PhotoImage, factory.LastModel);
        Assert.True(factory.InitCount >= 1);
        Assert.Equal(1, factory.LeaseDisposeCount);    // lease disposed after inference
        Assert.Equal(0, factory.Session.DisposeCount); // shared session NOT disposed per request
    }

    [Fact]
    public async Task Image_Direct_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        var embedder = BuildImage("openvino-direct", factory);

        var result = await embedder.EmbedImageAsync(TinyImage(), PhotoProfile());

        Assert.Equal(Dim, result.Dimension);
        Assert.Equal(OnnxModel.PhotoImage, factory.LastModel);
    }

    [Fact]
    public async Task Image_Direct_Failure_Does_Not_Silently_Fall_Back()
    {
        var factory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var embedder = BuildImage("openvino-direct", factory);

        var ex = await Assert.ThrowsAsync<OnnxSessionUnavailableException>(
            () => embedder.EmbedImageAsync(TinyImage(), PhotoProfile()));

        Assert.Equal(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, ex.ReasonCode);
    }

    [Fact]
    public async Task Image_Session_Reused_And_Lease_Disposed_Across_Calls()
    {
        var factory = new FakeFactory();
        var embedder = BuildImage("onnxruntime", factory);
        var profile = PhotoProfile();
        var bytes = TinyImage();

        await embedder.EmbedImageAsync(bytes, profile);
        await embedder.EmbedImageAsync(bytes, profile);

        Assert.Equal(2, factory.AcquireCount);
        Assert.Equal(2, factory.LeaseDisposeCount);
        Assert.Equal(0, factory.Session.DisposeCount); // embedder never disposes the shared session
    }

    [Fact]
    public async Task Image_Embedding_Is_L2_Normalized()
    {
        var factory = new FakeFactory();
        var embedder = BuildImage("onnxruntime", factory);

        var result = await embedder.EmbedImageAsync(TinyImage(), PhotoProfile());

        var norm = Math.Sqrt(result.Vector.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);
        Assert.All(result.Vector, x => Assert.False(float.IsNaN(x) || float.IsInfinity(x)));
    }

    [Fact]
    public async Task Image_Dimension_Mismatch_Is_Rejected()
    {
        var factory = new FakeFactory();
        factory.Session.Output = new float[768]; // wrong dim vs profile 1152
        var embedder = BuildImage("onnxruntime", factory);

        await Assert.ThrowsAsync<ArgumentException>(() => embedder.EmbedImageAsync(TinyImage(), PhotoProfile()));
    }

    [Fact]
    public async Task Image_Cancellation_Propagates_Before_Inference()
    {
        var factory = new FakeFactory();
        var embedder = BuildImage("onnxruntime", factory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => embedder.EmbedImageAsync(TinyImage(), PhotoProfile(), cts.Token));
        Assert.Equal(0, factory.AcquireCount);
    }

    [Fact]
    public void Image_Readiness_Direct_Is_Compile_Backed()
    {
        var okFactory = new FakeFactory();
        var ready = BuildImage("openvino-direct", okFactory).CheckReadiness(PhotoProfile());
        Assert.True(ready.IsReady);
        Assert.Equal(1, okFactory.AcquireCount); // readiness actually compiled
        Assert.Equal(OnnxModel.PhotoImage, okFactory.LastModel);

        var badFactory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonCompileFailed),
        };
        var notReady = BuildImage("openvino-direct", badFactory).CheckReadiness(PhotoProfile());
        Assert.False(notReady.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonCompileFailed, notReady.Reason);
    }

    [Fact]
    public void Image_Readiness_OnnxRuntime_Does_Not_Compile()
    {
        var factory = new FakeFactory();
        var readiness = BuildImage("onnxruntime", factory).CheckReadiness(PhotoProfile());

        Assert.True(readiness.IsReady);
        Assert.Equal(0, factory.AcquireCount); // file-check only for CPU
    }

    // ---- text routing ----

    [Fact]
    public async Task Text_OnnxRuntime_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        factory.Session.OutputName = "text_embeds";
        var embedder = BuildText("onnxruntime", factory);

        var result = await embedder.EmbedTextAsync("hello", PhotoProfile());

        Assert.Equal(Dim, result.Dimension);
        Assert.Equal(1, factory.AcquireCount);
        Assert.Equal(OnnxModel.PhotoText, factory.LastModel);
        Assert.True(factory.InitCount >= 1);
        Assert.Equal(1, factory.LeaseDisposeCount);    // lease disposed after inference
        Assert.Equal(0, factory.Session.DisposeCount); // shared session NOT disposed per request
    }

    [Fact]
    public async Task Text_Direct_Routes_To_Factory_Not_Sidecar()
    {
        var factory = new FakeFactory();
        factory.Session.OutputName = "text_embeds";
        var embedder = BuildText("openvino-direct", factory);

        var result = await embedder.EmbedTextAsync("hello", PhotoProfile());

        Assert.Equal(Dim, result.Dimension);
        Assert.Equal(OnnxModel.PhotoText, factory.LastModel);
    }

    [Fact]
    public async Task Text_Direct_Failure_Does_Not_Silently_Fall_Back()
    {
        var factory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var embedder = BuildText("openvino-direct", factory);

        var ex = await Assert.ThrowsAsync<OnnxSessionUnavailableException>(
            () => embedder.EmbedTextAsync("hello", PhotoProfile()));

        Assert.Equal(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, ex.ReasonCode);
    }

    [Fact]
    public async Task Text_Embedding_Is_L2_Normalized_And_Dimension_Checked()
    {
        var factory = new FakeFactory();
        factory.Session.OutputName = "text_embeds";
        var embedder = BuildText("onnxruntime", factory);

        var result = await embedder.EmbedTextAsync("hello", PhotoProfile());
        var norm = Math.Sqrt(result.Vector.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);

        factory.Session.Output = new float[768]; // wrong dim vs profile 1152
        await Assert.ThrowsAsync<ArgumentException>(() => embedder.EmbedTextAsync("hello", PhotoProfile()));
    }

    [Fact]
    public async Task Text_Cancellation_Propagates_Before_Inference()
    {
        var factory = new FakeFactory();
        var embedder = BuildText("onnxruntime", factory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => embedder.EmbedTextAsync("hello", PhotoProfile(), cts.Token));
        Assert.Equal(0, factory.AcquireCount);
    }

    [Fact]
    public void Text_Readiness_Direct_Is_Compile_Backed()
    {
        var okFactory = new FakeFactory();
        var ready = BuildText("openvino-direct", okFactory).CheckReadiness(PhotoProfile());
        Assert.True(ready.IsReady);
        Assert.Equal(1, okFactory.AcquireCount); // readiness actually compiled
        Assert.Equal(OnnxModel.PhotoText, okFactory.LastModel);

        var badFactory = new FakeFactory
        {
            AcquireThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonProviderUnavailable),
        };
        var notReady = BuildText("openvino-direct", badFactory).CheckReadiness(PhotoProfile());
        Assert.False(notReady.IsReady);
        Assert.Equal(OnnxInferenceSessionFactory.ReasonProviderUnavailable, notReady.Reason);
    }

    [Fact]
    public async Task Text_Empty_Input_Is_Rejected_Before_Any_Inference()
    {
        var factory = new FakeFactory();
        var embedder = BuildText("onnxruntime", factory);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => embedder.EmbedTextAsync("   ", PhotoProfile()));
        Assert.Equal(0, factory.AcquireCount);
    }
}

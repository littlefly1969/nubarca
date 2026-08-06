using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Cli;

namespace NubArca.Api.Tests.Ai;

// SigLIP direct milestone: startup preload for the photo towers. Fakes only —
// no GPU, no weights. API side: the hosted preloader compiles + synthetic-
// validates the photo TEXT tower (when the active photo profile has one) with
// DISTINCT sanitized failure codes, without changing the face stages. Worker
// side: the `jobs worker` inline preload compiles + validates the photo IMAGE
// tower, logging sanitized codes and never crashing the worker.
public sealed class OnnxPhotoDirectPreloadTests : IDisposable
{
    private const int Dim = 1152;
    private readonly List<string> _tempDirs = new();

    private string TempModelDir(bool textModel = true, bool tokenizer = true, bool imageModel = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "photopreload-" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(dir, OnnxImageModels.SiglipSo400mKey);
        Directory.CreateDirectory(sub);
        if (imageModel) File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultModelFile), "x");
        if (textModel) File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultTextModelFile), "x");
        if (tokenizer) File.WriteAllText(Path.Combine(sub, OnnxImageModels.DefaultTokenizerFile), "{}");
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs) { try { Directory.Delete(d, true); } catch { /* best effort */ } }
    }

    private static AiOptions PhotoDirectOptions(string modelDir, bool withFace = false) => new()
    {
        Enabled = true,
        ImageEmbeddingsEnabled = true,
        PhotoSimilarityProfileKey = OnnxImageModels.SiglipSo400mProfileKey,
        FaceProfileKey = withFace ? OnnxFaceModels.Antelopev2ProfileKey : null,
        TimeoutSeconds = 30,
        Onnx = new AiOnnxOptions
        {
            ModelDir = modelDir,
            ExecutionProvider = OnnxExecutionProviders.OpenVinoDirect,
            OpenVino = new AiOnnxOpenVinoOptions { NativeDir = "/opt/ort" },
        },
    };

    private static OnnxFacePreloadService Service(AiOptions options, FakeFactory factory, OnnxFacePreloadState state) =>
        new(Options.Create(options), factory, state, NullLogger<OnnxFacePreloadService>.Instance);

    private static IReadOnlyList<OnnxOutputTensor> ValidTextOutputs()
    {
        var v = new float[Dim];
        v[0] = 3f; v[1] = 4f;
        return new[] { new OnnxOutputTensor("text_embeds", v, new[] { 1, Dim }) };
    }

    private static IReadOnlyList<OnnxOutputTensor> ValidImageOutputs()
    {
        var v = new float[Dim];
        v[0] = 3f; v[1] = 4f;
        return new[] { new OnnxOutputTensor("image_embeds", v, new[] { 1, Dim }) };
    }

    private sealed class FakeFactory : IOnnxInferenceSessionFactory
    {
        public int InitCount, PhotoTextAcquireCount, PhotoImageAcquireCount, OtherAcquireCount;
        public Exception? PhotoTextThrows, PhotoImageThrows;
        public readonly FakeSession PhotoText = new() { Outputs = ValidTextOutputs() };
        public readonly FakeSession PhotoImage = new() { Outputs = ValidImageOutputs() };

        public OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec) => OnnxSessionReadiness.Ready;

        public IOnnxSessionLease Acquire(OnnxModelSpec spec)
        {
            switch (spec.Model)
            {
                case OnnxModel.PhotoText:
                    Interlocked.Increment(ref PhotoTextAcquireCount);
                    if (PhotoTextThrows is not null) throw PhotoTextThrows;
                    return new Lease(PhotoText);
                case OnnxModel.PhotoImage:
                    Interlocked.Increment(ref PhotoImageAcquireCount);
                    if (PhotoImageThrows is not null) throw PhotoImageThrows;
                    return new Lease(PhotoImage);
                default:
                    Interlocked.Increment(ref OtherAcquireCount);
                    throw new InvalidOperationException("unexpected face model in photo preload tests");
            }
        }

        public void EnsureNativeProviderInitialized() => Interlocked.Increment(ref InitCount);
        public OnnxNativeCoreState NativeCoreState => OnnxNativeCoreState.OpenVinoCore;
        public void Dispose() { }

        private sealed class Lease(FakeSession s) : IOnnxSessionLease
        {
            public IOnnxSession Session => s;
            public void Dispose() { }
        }
    }

    private sealed class FakeSession : IOnnxSession
    {
        public IReadOnlyList<OnnxOutputTensor> Outputs = Array.Empty<OnnxOutputTensor>();
        public IReadOnlyList<string> InputNames => new[] { "input_ids", "attention_mask" };
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs) => Outputs;
        public void Dispose() { }
    }

    // ---- API preload: photo TEXT stage ----

    [Fact]
    public async Task PhotoText_Compiles_And_Validates_Then_Ready()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(PhotoDirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.True(state.Current.IsReady);
        Assert.Equal(1, factory.PhotoTextAcquireCount); // text tower compiled once
        Assert.Equal(0, factory.OtherAcquireCount);     // no face model configured → none touched
        Assert.True(factory.InitCount >= 1);
    }

    [Fact]
    public async Task PhotoText_Model_Missing_Fails_With_Distinct_Code()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(PhotoDirectOptions(TempModelDir(textModel: false)), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadFailureCodes.PhotoTextModelMissing, state.Current.FailureCode);
        Assert.Equal(0, factory.PhotoTextAcquireCount);
    }

    [Fact]
    public async Task PhotoText_Tokenizer_Missing_Fails_With_Distinct_Code()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var svc = Service(PhotoDirectOptions(TempModelDir(tokenizer: false)), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadFailureCodes.PhotoTextTokenizerMissing, state.Current.FailureCode);
        Assert.Equal(0, factory.PhotoTextAcquireCount);
    }

    [Fact]
    public async Task PhotoText_Compile_Failure_Fails_Closed()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory
        {
            PhotoTextThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonCompileFailed),
        };
        var svc = Service(PhotoDirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.False(state.Current.IsReady);
        Assert.Equal(FacePreloadFailureCodes.PhotoTextCompileFailed, state.Current.FailureCode);
    }

    [Fact]
    public async Task PhotoText_Device_Unavailable_Maps_To_Shared_Code()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory
        {
            PhotoTextThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var svc = Service(PhotoDirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.Equal(FacePreloadFailureCodes.OpenVinoDeviceUnavailable, state.Current.FailureCode);
    }

    [Fact]
    public async Task PhotoText_Validation_Failure_On_Wrong_Dimension()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        factory.PhotoText.Outputs = new[] { new OnnxOutputTensor("text_embeds", new float[768], new[] { 1, 768 }) };
        var svc = Service(PhotoDirectOptions(TempModelDir()), factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.Equal(FacePreloadFailureCodes.PhotoTextValidationFailed, state.Current.FailureCode);
    }

    [Fact]
    public async Task PhotoText_Skipped_When_Image_Embeddings_Disabled()
    {
        var state = new OnnxFacePreloadState();
        var factory = new FakeFactory();
        var options = PhotoDirectOptions(TempModelDir());
        options.ImageEmbeddingsEnabled = false;
        var svc = Service(options, factory, state);

        await svc.RunPreloadAsync(CancellationToken.None);

        Assert.True(state.Current.IsReady); // nothing required by this process
        Assert.Equal(0, factory.PhotoTextAcquireCount);
    }

    [Fact]
    public void ResolvePhotoTextConfig_Requires_Enabled_Flags_And_Text_Tower()
    {
        Assert.Null(OnnxFacePreloadService.ResolvePhotoTextConfig(new AiOptions()));
        Assert.Null(OnnxFacePreloadService.ResolvePhotoTextConfig(new AiOptions
        {
            Enabled = true,
            ImageEmbeddingsEnabled = true,
            PhotoSimilarityProfileKey = "unknown-profile",
        }));
        Assert.NotNull(OnnxFacePreloadService.ResolvePhotoTextConfig(new AiOptions
        {
            Enabled = true,
            ImageEmbeddingsEnabled = true,
            PhotoSimilarityProfileKey = OnnxImageModels.SiglipSo400mProfileKey,
        }));
    }

    [Fact]
    public void MapCompileReason_Maps_PhotoText_Stage()
    {
        Assert.Equal(FacePreloadFailureCodes.PhotoTextModelMissing,
            OnnxFacePreloadService.MapCompileReason(
                OnnxInferenceSessionFactory.ReasonModelNotFound, FacePreloadStates.PhotoTextCompiling));
        Assert.Equal(FacePreloadFailureCodes.PhotoTextCompileFailed,
            OnnxFacePreloadService.MapCompileReason(
                OnnxInferenceSessionFactory.ReasonCompileFailed, FacePreloadStates.PhotoTextCompiling));
        Assert.Equal(FacePreloadFailureCodes.OrtAbiMismatch,
            OnnxFacePreloadService.MapCompileReason(
                OnnxInferenceSessionFactory.ReasonAbiMismatch, FacePreloadStates.PhotoTextCompiling));
    }

    // ---- validators ----

    [Fact]
    public void ValidatePhotoText_Rejects_Missing_Output_And_ZeroNorm()
    {
        var config = OnnxImageModels.Catalog[OnnxImageModels.SiglipSo400mKey];

        Assert.False(OnnxFacePreloadService.ValidatePhotoText(
            new[] { new OnnxOutputTensor("wrong_name", new float[Dim], new[] { 1, Dim }) }, config, out var missing));
        Assert.Equal("output-tensor-missing", missing);

        Assert.False(OnnxFacePreloadService.ValidatePhotoText(
            new[] { new OnnxOutputTensor("text_embeds", new float[Dim], new[] { 1, Dim }) }, config, out var zero));
        Assert.Equal("zero-norm", zero);

        var nan = new float[Dim]; nan[0] = float.NaN;
        Assert.False(OnnxFacePreloadService.ValidatePhotoText(
            new[] { new OnnxOutputTensor("text_embeds", nan, new[] { 1, Dim }) }, config, out var reason));
        Assert.Equal("non-finite-output", reason);
    }

    [Fact]
    public void ValidatePhotoImage_Selects_Image_Output()
    {
        var config = OnnxImageModels.Catalog[OnnxImageModels.SiglipSo400mKey];
        var ok = new float[Dim]; ok[0] = 3f; ok[1] = 4f;

        Assert.True(OnnxFacePreloadService.ValidatePhotoImage(
            new[] { new OnnxOutputTensor("image_embeds", ok, new[] { 1, Dim }) }, config, out _));
        Assert.False(OnnxFacePreloadService.ValidatePhotoImage(
            new[] { new OnnxOutputTensor("image_embeds", new float[768], new[] { 1, 768 }) }, config, out var reason));
        Assert.Equal("dimension-mismatch", reason);
    }

    // ---- worker inline preload (photo IMAGE) ----

    private static IServiceProvider WorkerServices(AiOptions options, IOnnxInferenceSessionFactory factory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(factory);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Worker_Preload_Compiles_Validates_And_Reports_Ready()
    {
        var factory = new FakeFactory();
        var options = PhotoDirectOptions(TempModelDir());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliEntryPoint.PreloadDirectPhotoImage(WorkerServices(options, factory), stdout, stderr);

        Assert.Contains("photo-image preload READY", stdout.ToString());
        Assert.Equal("", stderr.ToString());
        Assert.Equal(1, factory.PhotoImageAcquireCount);
        Assert.True(factory.InitCount >= 1);
    }

    [Fact]
    public void Worker_Preload_Skips_NonDirect_Provider()
    {
        var factory = new FakeFactory();
        var options = PhotoDirectOptions(TempModelDir());
        options.Onnx.ExecutionProvider = OnnxExecutionProviders.OnnxRuntime;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliEntryPoint.PreloadDirectPhotoImage(WorkerServices(options, factory), stdout, stderr);

        Assert.Equal("", stdout.ToString());
        Assert.Equal(0, factory.PhotoImageAcquireCount);
    }

    [Fact]
    public void Worker_Preload_Reports_Missing_Model_Without_Throwing()
    {
        var factory = new FakeFactory();
        var options = PhotoDirectOptions(TempModelDir(imageModel: false));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliEntryPoint.PreloadDirectPhotoImage(WorkerServices(options, factory), stdout, stderr);

        Assert.Contains(FacePreloadFailureCodes.PhotoImageModelMissing, stderr.ToString());
        Assert.Equal(0, factory.PhotoImageAcquireCount);
    }

    [Fact]
    public void Worker_Preload_Reports_Compile_Failure_Without_Throwing()
    {
        var factory = new FakeFactory
        {
            PhotoImageThrows = new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable),
        };
        var options = PhotoDirectOptions(TempModelDir());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliEntryPoint.PreloadDirectPhotoImage(WorkerServices(options, factory), stdout, stderr);

        Assert.Contains(FacePreloadFailureCodes.PhotoImageCompileFailed, stderr.ToString());
        Assert.Contains(OnnxInferenceSessionFactory.ReasonDeviceUnavailable, stderr.ToString());
    }

    [Fact]
    public void Worker_Preload_Reports_Validation_Failure_Without_Throwing()
    {
        var factory = new FakeFactory();
        factory.PhotoImage.Outputs = new[] { new OnnxOutputTensor("image_embeds", new float[768], new[] { 1, 768 }) };
        var options = PhotoDirectOptions(TempModelDir());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliEntryPoint.PreloadDirectPhotoImage(WorkerServices(options, factory), stdout, stderr);

        Assert.Contains(FacePreloadFailureCodes.PhotoImageValidationFailed, stderr.ToString());
    }
}

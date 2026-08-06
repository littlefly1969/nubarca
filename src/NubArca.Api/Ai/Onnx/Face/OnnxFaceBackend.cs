using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Ai.Onnx.Face;

// Evaluation-only local ONNX face-recognition backend (provider "onnx"): a SCRFD/
// RetinaFace detector (bbox + 5-point landmarks) + an ArcFace recognition
// embedder (512-d, cosine). It implements BOTH IFaceDetector and IFaceEmbedder so
// the resolver can hand it out for either capability, and it stays UNAVAILABLE
// (CheckReadiness) until both model files are present under Ai__Onnx__ModelDir —
// a missing model is an environment/config state, never a content failure.
//
// This branch is evaluation-only: NOTHING here persists FaceDetection/
// FaceEmbedding rows, creates clusters/identities, or writes crops to disk.
// Aligned crops exist only in memory. Raw vectors never leave the process.
public sealed class OnnxFaceBackend : IFaceDetector, IAlignedFaceEmbedder, IDisposable
{
    public enum EmbedMode { None, First, Specific }

    private readonly IOptions<AiOptions> _options;
    private readonly OnnxFacePreprocessor _preprocessor;
    private readonly ILogger<OnnxFaceBackend> _logger;
    private readonly IOnnxInferenceSessionFactory _factory;
    // Face AI milestone: BOTH the detector and the recognizer are now owned by
    // _factory (one cache, one lifecycle, per-model device selection). The backend
    // keeps NO second local InferenceSession cache — sessions are never created or
    // disposed per request.
    private readonly SemaphoreSlim _gate;

    public OnnxFaceBackend(
        IOptions<AiOptions> options,
        OnnxFacePreprocessor preprocessor,
        ILogger<OnnxFaceBackend> logger,
        IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _preprocessor = preprocessor;
        _logger = logger;
        _factory = factory;
        var max = Math.Max(1, options.Value.MaxConcurrency);
        _gate = new SemaphoreSlim(max, max);
    }

    public string Provider => AiProviders.Onnx;

    public bool Supports(string capability) =>
        capability == AiCapabilities.FaceDetection || capability == AiCapabilities.FaceEmbedding;

    public AiBackendReadiness CheckReadiness(AiProfile profile)
    {
        var config = OnnxFaceModels.ResolveConfig(profile.ConfigHash, profile.Key);
        if (config is null)
        {
            return AiBackendReadiness.NotReady("onnx-unknown-face-model");
        }

        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            return AiBackendReadiness.NotReady("onnx-modeldir-not-configured");
        }

        if (!File.Exists(DetectorPath(modelDir, config)))
        {
            return AiBackendReadiness.NotReady("onnx-face-detector-not-found");
        }

        var detectorPath = DetectorPath(modelDir, config);
        var recognitionPath = RecognitionPath(modelDir, config);
        if (!File.Exists(recognitionPath))
        {
            return AiBackendReadiness.NotReady("onnx-face-recognition-not-found");
        }

        // Face AI milestone: compile-backed readiness for BOTH migrated models in
        // direct mode. "Ready" is authoritative — it means each OpenVINO session
        // actually COMPILED for its configured device, never merely that /dev/dri or
        // the native library exists. The factory returns a sanitized reason on
        // failure; compilation is warm/cached so the first real request reuses it.
        if (OnnxExecutionProviders.Normalize(_options.Value.Onnx.ExecutionProvider)
            == OnnxExecutionProviders.OpenVinoDirect)
        {
            _factory.EnsureNativeProviderInitialized();
            try
            {
                using (var detLease = _factory.Acquire(new OnnxModelSpec(OnnxModel.FaceDetector, detectorPath)))
                {
                    _ = detLease.Session; // compiled + cached (warm)
                }

                using var recLease = _factory.Acquire(new OnnxModelSpec(OnnxModel.FaceRecognizer, recognitionPath));
                _ = recLease.Session; // compiled + cached (warm)
            }
            catch (OnnxSessionUnavailableException ex)
            {
                return AiBackendReadiness.NotReady(ex.ReasonCode);
            }
        }

        return AiBackendReadiness.Ready;
    }

    // ---- IFaceDetector: full image → normalized faces (+ landmarks) ----------
    public async Task<AiFaceDetectionResult> DetectFacesAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(imageBytes, profile, EmbedMode.None, 0, cancellationToken);
        var faces = result.Faces
            .Select(f => new DetectedFace(f.X, f.Y, f.Width, f.Height, f.Score, f.Landmarks))
            .ToList();
        return new AiFaceDetectionResult(faces);
    }

    // ---- IFaceEmbedder: an ALREADY-ALIGNED crop → recognition embedding ------
    public async Task<AiEmbeddingResult> EmbedFaceAsync(
        ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default)
    {
        var config = ResolveConfigOrThrow(profile);
        var (modelDir, _, recognitionPath) = ResolvePathsOrThrow(config);
        var dimension = DimensionFor(profile, config);
        var bytes = faceCropBytes.ToArray();
        var timeoutSeconds = _options.Value.TimeoutSeconds;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var work = Task.Run(() =>
            {
                var tensor = _preprocessor.PrepareAlignedCrop(bytes, config);
                var raw = RunRecognition(recognitionPath, config, tensor);
                return OnnxImageEmbeddings.Finalize(raw, dimension);
            }, cancellationToken);

            var vector = timeoutSeconds > 0
                ? await work.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                : await work.WaitAsync(cancellationToken);

            return new AiEmbeddingResult(vector, dimension, MetricFor(profile, config));
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- IAlignedFaceEmbedder: FULL image + per-face landmarks → embeddings ---
    // Decodes/orients the image ONCE, then aligns+recognizes each face from its
    // normalized 5-point landmarks. Used by the face-embedding backfill so stored
    // detections get properly-aligned ArcFace embeddings without re-detecting.
    public async Task<IReadOnlyList<FaceEmbedAttempt>> EmbedAlignedFacesAsync(
        ReadOnlyMemory<byte> imageBytes,
        IReadOnlyList<IReadOnlyList<FaceLandmark>> normalizedLandmarksPerFace,
        AiProfile profile,
        CancellationToken cancellationToken = default)
    {
        var config = ResolveConfigOrThrow(profile);
        var (_, _, recognitionPath) = ResolvePathsOrThrow(config);
        var dimension = DimensionFor(profile, config);
        var metric = MetricFor(profile, config);
        var bytes = imageBytes.ToArray();
        var timeoutSeconds = _options.Value.TimeoutSeconds;
        var faceCount = normalizedLandmarksPerFace.Count;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var work = Task.Run(() =>
            {
                // Decode ONCE for the whole image. A decode failure throws out of
                // here → the caller marks every pending face with a shared reason.
                using var image = Image.Load<Rgb24>(bytes);
                image.Mutate(ctx => ctx.AutoOrient());
                var width = image.Width;
                var height = image.Height;

                var results = new FaceEmbedAttempt[faceCount];
                for (var i = 0; i < faceCount; i++)
                {
                    // PER-FACE ISOLATION: one bad face never aborts the others.
                    try
                    {
                        var lm = normalizedLandmarksPerFace[i];
                        if (lm is null || lm.Count < config.LandmarkCount)
                        {
                            results[i] = FaceEmbedAttempt.AlignmentInvalid;
                            continue;
                        }

                        // Normalized [0..1] → oriented-image pixel coords (x0,y0,x1,y1,…).
                        var src = new float[config.LandmarkCount * 2];
                        for (var k = 0; k < config.LandmarkCount; k++)
                        {
                            src[k * 2] = (float)(lm[k].X * width);
                            src[k * 2 + 1] = (float)(lm[k].Y * height);
                        }

                        if (!_preprocessor.TryPrepareRecognition(image, src, config, out var recTensor))
                        {
                            results[i] = FaceEmbedAttempt.AlignmentInvalid;
                            continue;
                        }

                        var raw = RunRecognition(recognitionPath, config, recTensor);
                        var vector = OnnxImageEmbeddings.Finalize(raw, dimension);
                        results[i] = FaceEmbedAttempt.Ok(new AiEmbeddingResult(vector, dimension, metric));
                    }
                    catch
                    {
                        // Recognition/crop failed for THIS face only → transient.
                        results[i] = FaceEmbedAttempt.RecognitionFailed;
                    }
                }

                return (IReadOnlyList<FaceEmbedAttempt>)results;
            }, cancellationToken);

            // The timeout is a CEILING scaled by face count so a crowded image is
            // not killed by a per-image budget sized for one face (the pre-fix bug
            // that produced 0 embeddings on 44-face photos). It never slows the
            // normal case — only guards against a genuine hang.
            if (timeoutSeconds <= 0)
            {
                return await work.WaitAsync(cancellationToken);
            }

            var ceiling = TimeSpan.FromSeconds((long)timeoutSeconds * Math.Max(1, faceCount));
            return await work.WaitAsync(ceiling, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Combined detect (+ optional embed) pipeline used by the eval harness -
    // One decode, one detection, and (optionally) one alignment+recognition, so
    // the evaluation service never re-decodes or re-detects.
    public async Task<FacePipelineResult> RunAsync(
        ReadOnlyMemory<byte> imageBytes,
        AiProfile profile,
        EmbedMode embedMode,
        int specificFaceIndex,
        CancellationToken cancellationToken = default)
    {
        var config = ResolveConfigOrThrow(profile);
        var (modelDir, detectorPath, recognitionPath) = ResolvePathsOrThrow(config);
        var dimension = DimensionFor(profile, config);
        var bytes = imageBytes.ToArray();
        var timeoutSeconds = _options.Value.TimeoutSeconds;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var work = Task.Run(
                () => RunPipeline(bytes, config, detectorPath, recognitionPath, dimension, embedMode, specificFaceIndex),
                cancellationToken);

            return timeoutSeconds > 0
                ? await work.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                : await work.WaitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private FacePipelineResult RunPipeline(
        byte[] bytes,
        OnnxFaceModelConfig config,
        string detectorPath,
        string recognitionPath,
        int dimension,
        EmbedMode embedMode,
        int specificFaceIndex)
    {
        // Direct mode: ensure the OpenVINO-enabled ORT core is the one this process
        // binds BEFORE the first session is created — the core is process-global.
        _factory.EnsureNativeProviderInitialized();

        // Image.Load throws on corrupt/unsupported input — the caller treats that
        // as a per-image failure, never a crash.
        using var image = Image.Load<Rgb24>(bytes);
        image.Mutate(ctx => ctx.AutoOrient());
        var width = image.Width;
        var height = image.Height;

        var prep = _preprocessor.PrepareDetection(image, config);
        var sw = Stopwatch.StartNew();
        var raws = RunDetection(detectorPath, prep);

        var decoded = ScrfdDecoder.Decode(
            raws, prep.Size, prep.Size, config.DetectorScoreThreshold, config.DetectorNmsThreshold,
            out var diagnostic);
        sw.Stop();
        var detectMs = sw.Elapsed.TotalMilliseconds;

        // Map detector-input coords back to oriented-image space and normalize.
        var faces = new List<FacePipelineFace>(decoded.Count);
        var pixelLandmarks = new List<float[]?>(decoded.Count);
        foreach (var d in decoded)
        {
            var x1 = d.X1 / prep.Scale;
            var y1 = d.Y1 / prep.Scale;
            var x2 = d.X2 / prep.Scale;
            var y2 = d.Y2 / prep.Scale;

            float[]? lmPixels = null;
            IReadOnlyList<FaceLandmark>? lmNorm = null;
            if (d.Landmarks is { Length: >= 10 })
            {
                lmPixels = new float[10];
                var norm = new List<FaceLandmark>(5);
                for (var k = 0; k < 5; k++)
                {
                    var px = d.Landmarks[k * 2] / prep.Scale;
                    var py = d.Landmarks[k * 2 + 1] / prep.Scale;
                    lmPixels[k * 2] = px;
                    lmPixels[k * 2 + 1] = py;
                    norm.Add(new FaceLandmark(Clamp01(px / width), Clamp01(py / height)));
                }

                lmNorm = norm;
            }

            faces.Add(new FacePipelineFace(
                Clamp01(x1 / width), Clamp01(y1 / height),
                Clamp01((x2 - x1) / width), Clamp01((y2 - y1) / height),
                d.Score, lmNorm));
            pixelLandmarks.Add(lmPixels);
        }

        if (embedMode == EmbedMode.None || faces.Count == 0)
        {
            return new FacePipelineResult(width, height, faces, detectMs, diagnostic, null, null, null, null);
        }

        var index = embedMode == EmbedMode.First ? 0 : specificFaceIndex;
        if (index < 0 || index >= faces.Count)
        {
            return new FacePipelineResult(width, height, faces, detectMs, diagnostic, null, null, null, null);
        }

        var landmarks = pixelLandmarks[index];
        if (landmarks is null)
        {
            // Detector produced no keypoints → cannot align for recognition.
            return new FacePipelineResult(
                width, height, faces, detectMs, diagnostic ?? "detector-has-no-landmarks", null, null, null, null);
        }

        if (!_preprocessor.TryPrepareRecognition(image, landmarks, config, out var recTensor))
        {
            return new FacePipelineResult(
                width, height, faces, detectMs, diagnostic ?? "face-alignment-degenerate", null, null, null, null);
        }

        var esw = Stopwatch.StartNew();
        var rawEmbedding = RunRecognition(recognitionPath, config, recTensor);
        var vector = OnnxImageEmbeddings.Finalize(rawEmbedding, dimension);
        esw.Stop();

        return new FacePipelineResult(
            width, height, faces, detectMs, diagnostic, vector, dimension, esw.Elapsed.TotalMilliseconds, index);
    }

    // Face AI milestone: the face DETECTOR (SCRFD/RetinaFace) routes exactly like
    // the recognizer — explicit, never silently falling back:
    //   onnxruntime      → factory-owned CPU session
    //   openvino-direct  → factory-owned OpenVINO session (FP32; GPU/CPU per config)
    // The tensor shape/dtype, input name, multi-output collection, output ordering
    // and shapes are unchanged; SCRFD decode + NMS stay in ScrfdDecoder. The lease
    // is acquired per call and released immediately (the shared session is cached in
    // the factory, never created or disposed here), keeping the call site stable for
    // a future exclusive-lease scheduler.
    private List<ScrfdDecoder.RawOutput> RunDetection(
        string detectorPath, OnnxFacePreprocessor.DetectionInput prep)
    {
        _factory.EnsureNativeProviderInitialized();
        using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.FaceDetector, detectorPath));
        var session = lease.Session;
        var inputName = session.InputNames.First();
        var tensor = new DenseTensor<float>(prep.Data, new[] { 1, 3, prep.Size, prep.Size });
        var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var raws = new List<ScrfdDecoder.RawOutput>(outputs.Count);
        foreach (var o in outputs)
        {
            raws.Add(new ScrfdDecoder.RawOutput(o.Data, o.Shape));
        }

        return raws;
    }

    // The face RECOGNIZER (glintr100) routes identically. The tensor shape/dtype,
    // input name, single-output selection and 512-d vector are unchanged; L2
    // normalization stays in the caller (OnnxImageEmbeddings.Finalize).
    private float[] RunRecognition(string recognitionPath, OnnxFaceModelConfig config, float[] chw)
    {
        var size = config.RecognitionInputSize;
        _factory.EnsureNativeProviderInitialized();
        using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.FaceRecognizer, recognitionPath));
        var session = lease.Session;
        var inputName = session.InputNames.First();
        var tensor = new DenseTensor<float>(chw, new[] { 1, 3, size, size });
        var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        return outputs[0].Data;
    }

    private static OnnxFaceModelConfig ResolveConfigOrThrow(AiProfile profile) =>
        OnnxFaceModels.ResolveConfig(profile.ConfigHash, profile.Key)
        ?? throw new InvalidOperationException($"No ONNX face config for profile '{profile.Key}'.");

    private (string ModelDir, string DetectorPath, string RecognitionPath) ResolvePathsOrThrow(
        OnnxFaceModelConfig config)
    {
        var modelDir = _options.Value.Onnx.ModelDir;
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            throw new InvalidOperationException("Ai:Onnx:ModelDir is not configured.");
        }

        var detectorPath = DetectorPath(modelDir, config);
        var recognitionPath = RecognitionPath(modelDir, config);
        if (!File.Exists(detectorPath))
        {
            throw new FileNotFoundException("ONNX face detector model file not found.");
        }

        if (!File.Exists(recognitionPath))
        {
            throw new FileNotFoundException("ONNX face recognition model file not found.");
        }

        return (modelDir!, detectorPath, recognitionPath);
    }

    private static int DimensionFor(AiProfile profile, OnnxFaceModelConfig config) =>
        profile.Dimension is > 0 ? profile.Dimension.Value : config.Dimension;

    private static string MetricFor(AiProfile profile, OnnxFaceModelConfig config) =>
        string.IsNullOrWhiteSpace(profile.DistanceMetric) ? config.DistanceMetric : profile.DistanceMetric!;

    private static string DetectorPath(string modelDir, OnnxFaceModelConfig config) =>
        Path.Combine(modelDir, config.PackageSubdir, config.DetectorFile);

    private static string RecognitionPath(string modelDir, OnnxFaceModelConfig config) =>
        Path.Combine(modelDir, config.PackageSubdir, config.RecognitionFile);

    private static double Clamp01(double v) => Math.Clamp(v, 0d, 1d);

    public void Dispose()
    {
        // Sessions are owned and disposed by IOnnxInferenceSessionFactory; the
        // backend only owns the concurrency gate.
        _gate.Dispose();
    }
}

// A face in normalized [0..1] oriented-image coordinates (+ optional landmarks).
public sealed record FacePipelineFace(
    double X, double Y, double Width, double Height, double? Score, IReadOnlyList<FaceLandmark>? Landmarks);

// Result of the combined detect (+ optional recognition-embed) pipeline. Vector /
// EmbeddingDimension / EmbedMs / EmbeddedFaceIndex are set only when an embed was
// requested and succeeded. The raw Vector never leaves the service layer.
public sealed record FacePipelineResult(
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<FacePipelineFace> Faces,
    double DetectMs,
    string? Diagnostic,
    float[]? Embedding,
    int? EmbeddingDimension,
    double? EmbedMs,
    int? EmbeddedFaceIndex);

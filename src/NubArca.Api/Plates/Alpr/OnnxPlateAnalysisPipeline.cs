using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Plates.Alpr;

// In-process (.NET, CPU) ONNX ALPR pipeline: a YOLO-family plate detector + a
// CTC plate-OCR reader. It is production PLUMBING — the inference path is real
// and works against a CONFORMING model, but no weights are committed and the
// exact tensor contracts are documented in docs/model-deployment/plates.md. The
// pure output interpretation lives in YoloDetectionOutputParser / PlateCtcDecoder
// (fully unit-tested on synthetic tensors); here we only preprocess, run the
// session, and map results. Missing/incompatible models surface as safe error
// codes (never a raw tensor dump, path, or stack trace). Shares NO model/profile
// with the AI face substrate.
public sealed class OnnxPlateAnalysisPipeline : IPlateAnalysisPipeline
{
    private readonly OnnxRuntimeSessionCache _sessions;
    private readonly IOptions<PlatesAlprOptions> _options;
    private readonly ILogger<OnnxPlateAnalysisPipeline> _logger;
    private readonly SemaphoreSlim _gate;

    public OnnxPlateAnalysisPipeline(
        OnnxRuntimeSessionCache sessions,
        IOptions<PlatesAlprOptions> options,
        ILogger<OnnxPlateAnalysisPipeline> logger)
    {
        _sessions = sessions;
        _options = options;
        _logger = logger;
        var max = Math.Max(1, options.Value.WorkerConcurrency);
        _gate = new SemaphoreSlim(max, max);
    }

    public bool IsAvailable => AvailabilityReason() is null;

    public string? UnavailableReason => AvailabilityReason();

    // Sanitized reason the ONNX provider is not usable, or null when ready.
    private string? AvailabilityReason()
    {
        var o = _options.Value;
        if (o.ResolveProvider() != PlateAlprProvider.Onnx)
        {
            return PlateAnalysisErrorCodes.ModelNotConfigured;
        }
        if (string.IsNullOrWhiteSpace(o.DetectorModelPath) || !File.Exists(o.DetectorModelPath))
        {
            return PlateAnalysisErrorCodes.DetectorModelMissing;
        }
        if (string.IsNullOrWhiteSpace(o.OcrModelPath) || !File.Exists(o.OcrModelPath))
        {
            return PlateAnalysisErrorCodes.OcrModelMissing;
        }
        return null;
    }

    public async Task<PlateAnalysisResult> AnalyzeAsync(
        PlateImageInput image, CancellationToken cancellationToken)
    {
        var o = _options.Value;
        var started = Stopwatch.GetTimestamp();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Run(image, o), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        PlateAnalysisResult Run(PlateImageInput input, PlatesAlprOptions opts)
        {
            using var img = LoadImage(input.Bytes);
            var detector = new OnnxPlateDetector(_sessions, opts);
            var ocr = new OnnxPlateOcrReader(_sessions, opts);

            var candidates = detector.Detect(img);
            var accepted = new List<PlateAnalysisDetection>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Score < opts.MinPlateConfidence)
                {
                    continue;
                }

                var (text, ocrConf) = ocr.Read(img, candidate);
                var normalized = PlateTextNormalizer.Normalize(text);
                if (normalized is null || ocrConf < opts.MinOcrConfidence)
                {
                    continue;
                }

                var combined = Math.Round((candidate.Score + ocrConf) / 2.0, 4);
                accepted.Add(new PlateAnalysisDetection(
                    new PlateBox(candidate.X, candidate.Y, candidate.Width, candidate.Height),
                    text.Trim(), normalized,
                    candidate.Score, ocrConf, combined,
                    CountryHint: null, RegionHint: null, Polygon: null));

                if (accepted.Count >= opts.MaxDetectionsPerImage)
                {
                    break;
                }
            }

            var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new PlateAnalysisResult(
                accepted, durationMs, opts.ProfileKey,
                "onnx-plate-detector", opts.DetectorModelKind,
                "onnx-plate-ocr", opts.OcrModelKind);
        }
    }

    private static Image<Rgb24> LoadImage(byte[] bytes)
    {
        try
        {
            var img = Image.Load<Rgb24>(bytes);
            img.Mutate(ctx => ctx.AutoOrient());
            return img;
        }
        catch (Exception ex)
        {
            throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.UnsupportedImage, ex);
        }
    }
}

// Thrown by the ONNX pipeline for a model load / output / inference failure.
// Carries only a stable safe code (see PlateAnalysisErrorCodes); the inner
// exception is for internal logging only and never surfaced to a client.
public sealed class PlateAnalysisModelException : Exception
{
    public string SafeCode { get; }

    public PlateAnalysisModelException(string safeCode, Exception? inner = null)
        : base(safeCode, inner)
        => SafeCode = safeCode;
}

// Cache of reusable ONNX InferenceSessions keyed by absolute model path. CPU,
// in-process. Sessions are heavy: created once and reused. Thread-safe.
public sealed class OnnxRuntimeSessionCache : IDisposable
{
    private readonly ConcurrentDictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);

    public InferenceSession Get(string modelPath, string loadFailureCode)
    {
        try
        {
            return _sessions.GetOrAdd(modelPath, p => new InferenceSession(p));
        }
        catch (Exception ex)
        {
            throw new PlateAnalysisModelException(loadFailureCode, ex);
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}

// YOLO-family ONNX plate detector step. Preprocess (letterbox → NCHW RGB/255),
// run the session, and interpret via YoloDetectionOutputParser.
internal sealed class OnnxPlateDetector
{
    private readonly OnnxRuntimeSessionCache _sessions;
    private readonly PlatesAlprOptions _o;

    public OnnxPlateDetector(OnnxRuntimeSessionCache sessions, PlatesAlprOptions options)
    {
        _sessions = sessions;
        _o = options;
    }

    public IReadOnlyList<YoloDetectionOutputParser.ParsedBox> Detect(Image<Rgb24> image)
    {
        var session = _sessions.Get(_o.DetectorModelPath, PlateAnalysisErrorCodes.DetectorModelLoadFailed);
        var (tensor, letterbox) = Preprocess(image);
        var inputName = session.InputMetadata.Keys.First();

        try
        {
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            var first = results.First().AsTensor<float>();
            return YoloDetectionOutputParser.Parse(
                first.ToArray(), first.Dimensions.ToArray(),
                numClasses: 1,
                _o.DetectorConfidenceThreshold, _o.DetectorNmsThreshold,
                letterbox);
        }
        catch (PlateModelOutputException ex)
        {
            throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.DetectorOutputUnsupported, ex);
        }
        catch (PlateAnalysisModelException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.InferenceFailed, ex);
        }
    }

    private (DenseTensor<float> Tensor, YoloDetectionOutputParser.Letterbox Letterbox) Preprocess(Image<Rgb24> image)
    {
        int inW = _o.DetectorInputWidth, inH = _o.DetectorInputHeight;
        var scale = Math.Min((double)inW / image.Width, (double)inH / image.Height);
        var newW = (int)Math.Round(image.Width * scale);
        var newH = (int)Math.Round(image.Height * scale);
        var padX = (inW - newW) / 2.0;
        var padY = (inH - newH) / 2.0;

        using var canvas = new Image<Rgb24>(inW, inH, new Rgb24(114, 114, 114));
        using (var resized = image.Clone(ctx => ctx.Resize(newW, newH)))
        {
            canvas.Mutate(ctx => ctx.DrawImage(resized, new Point((int)Math.Round(padX), (int)Math.Round(padY)), 1f));
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, inH, inW });
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    tensor[0, 0, y, x] = p.R / 255f;
                    tensor[0, 1, y, x] = p.G / 255f;
                    tensor[0, 2, y, x] = p.B / 255f;
                }
            }
        });

        var lb = new YoloDetectionOutputParser.Letterbox(
            scale, padX, padY, inW, inH, image.Width, image.Height);
        return (tensor, lb);
    }
}

// CTC ONNX plate-OCR step. Crop the detected box, resize to the OCR input,
// run the session, and decode via PlateCtcDecoder.
internal sealed class OnnxPlateOcrReader
{
    private readonly OnnxRuntimeSessionCache _sessions;
    private readonly PlatesAlprOptions _o;

    public OnnxPlateOcrReader(OnnxRuntimeSessionCache sessions, PlatesAlprOptions options)
    {
        _sessions = sessions;
        _o = options;
    }

    public (string Text, double Confidence) Read(Image<Rgb24> image, YoloDetectionOutputParser.ParsedBox box)
    {
        var session = _sessions.Get(_o.OcrModelPath, PlateAnalysisErrorCodes.OcrModelLoadFailed);

        // Denormalize the box to original pixels and clamp inside the image.
        var x = (int)Math.Floor(box.X * image.Width);
        var y = (int)Math.Floor(box.Y * image.Height);
        var w = (int)Math.Ceiling(box.Width * image.Width);
        var h = (int)Math.Ceiling(box.Height * image.Height);
        x = Math.Clamp(x, 0, Math.Max(0, image.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, image.Height - 1));
        w = Math.Clamp(w, 1, image.Width - x);
        h = Math.Clamp(h, 1, image.Height - y);

        using var crop = image.Clone(ctx => ctx
            .Crop(new Rectangle(x, y, w, h))
            .Resize(_o.OcrInputWidth, _o.OcrInputHeight));

        var tensor = new DenseTensor<float>(new[] { 1, 3, _o.OcrInputHeight, _o.OcrInputWidth });
        crop.ProcessPixelRows(accessor =>
        {
            for (var ry = 0; ry < accessor.Height; ry++)
            {
                var row = accessor.GetRowSpan(ry);
                for (var rx = 0; rx < row.Length; rx++)
                {
                    var p = row[rx];
                    tensor[0, 0, ry, rx] = p.R / 255f;
                    tensor[0, 1, ry, rx] = p.G / 255f;
                    tensor[0, 2, ry, rx] = p.B / 255f;
                }
            }
        });

        var inputName = session.InputMetadata.Keys.First();
        try
        {
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            var first = results.First().AsTensor<float>();
            var decoded = PlateCtcDecoder.Decode(first.ToArray(), first.Dimensions.ToArray(), _o.OcrAlphabet);
            return (decoded.Text, decoded.Confidence);
        }
        catch (PlateModelOutputException ex)
        {
            throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.OcrOutputUnsupported, ex);
        }
        catch (PlateAnalysisModelException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlateAnalysisModelException(PlateAnalysisErrorCodes.InferenceFailed, ex);
        }
    }
}

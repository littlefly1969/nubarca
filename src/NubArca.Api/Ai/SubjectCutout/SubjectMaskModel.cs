using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Ai.SubjectCutout;

// The segmentation model behind "copy subject": a square NCHW image in, one
// square plane of per-pixel subject LOGITS out. Pixels are the caller's
// business (SubjectCutoutImaging); this seam is what tests replace.
public interface ISubjectMaskModel
{
    // Sanitized reason the model cannot run, or null when it can. Cheap: never
    // compiles a session (a request does that, once, on first use).
    string? UnavailableReason();

    // Runs the model on a normalized 1x3xSxS tensor and returns S*S logits.
    float[] PredictLogits(float[] input, int size);
}

// BiRefNet ONNX on the shared in-process session factory, so it follows the
// installation's execution provider (CPU onnxruntime or OpenVINO) and its own
// device setting (Ai:Onnx:OpenVino:SubjectCutoutDevice).
public sealed class OnnxSubjectMaskModel : ISubjectMaskModel
{
    internal const string ReasonDisabled = "subject-cutout-disabled";
    internal const string ReasonModelDirMissing = "onnx-modeldir-not-configured";
    internal const string ReasonOutputShape = "subject-cutout-output-shape";

    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;

    public OnnxSubjectMaskModel(IOptions<AiOptions> options, IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _factory = factory;
    }

    public string? UnavailableReason()
    {
        var ai = _options.Value;
        if (!ai.Enabled || !ai.SubjectCutout.Enabled) return ReasonDisabled;
        var path = ModelPath(ai);
        if (path is null) return ReasonModelDirMissing;
        var readiness = _factory.CheckReadiness(new OnnxModelSpec(OnnxModel.SubjectCutout, path));
        return readiness.IsReady ? null : readiness.Reason;
    }

    public float[] PredictLogits(float[] input, int size)
    {
        var path = ModelPath(_options.Value)
            ?? throw new OnnxSessionUnavailableException(ReasonModelDirMissing);
        _factory.EnsureNativeProviderInitialized();
        using var lease = _factory.Acquire(new OnnxModelSpec(OnnxModel.SubjectCutout, path));
        var session = lease.Session;
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, size, size });
        var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor) });

        // BiRefNet exports that keep the intermediate side outputs put the final
        // prediction last; the stock export has exactly one.
        var logits = outputs[^1].Data;
        if (logits.Length != size * size)
        {
            throw new OnnxSessionUnavailableException(ReasonOutputShape);
        }
        return logits;
    }

    private static string? ModelPath(AiOptions ai)
    {
        var file = ai.SubjectCutout.ModelFile;
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (Path.IsPathRooted(file)) return file;
        var dir = ai.Onnx.ModelDir;
        return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, file);
    }
}

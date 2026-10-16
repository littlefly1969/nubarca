using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Ai.SubjectCutout;

// "Tap a subject": a promptable segmenter that, given one touched point,
// proposes a few candidate masks around it (a shirt, the whole person, …).
// Which candidate is the subject is decided against the cutout model's
// foreground (SubjectCutoutImaging.SelectSubject); this seam is what tests
// replace.
public interface ISubjectPointModel
{
    // Sanitized reason point selection cannot run, or null when it can.
    string? UnavailableReason();

    // `input` is the photo stretched to InputEdge², ImageNet-normalized, NCHW —
    // the same tensor the cutout model takes at 1024. The point is normalized to
    // the photo (0..1). Returns `count` square candidate planes of LOGITS, each
    // CandidateEdge² over the same stretched square.
    float[] PredictCandidates(float[] input, float x, float y, out int count);
}

public static class SubjectPointModelShape
{
    public const int InputEdge = 1024;
    public const int CandidateEdge = 256;
}

// SAM 2.1 exported as two ONNX files (docs/model-deployment/subject-cutout.md):
// the image encoder (image → image_embed, high_res_feats_0, high_res_feats_1)
// and the prompt decoder (those + one point → 3 low-res candidate masks + IoU).
public sealed class OnnxSubjectPointModel : ISubjectPointModel
{
    internal const string ReasonDisabled = "subject-point-disabled";
    internal const string ReasonOutputShape = "subject-point-output-shape";

    private static readonly string[] Features = ["image_embed", "high_res_feats_0", "high_res_feats_1"];

    private readonly IOptions<AiOptions> _options;
    private readonly IOnnxInferenceSessionFactory _factory;

    public OnnxSubjectPointModel(IOptions<AiOptions> options, IOnnxInferenceSessionFactory factory)
    {
        _options = options;
        _factory = factory;
    }

    public string? UnavailableReason()
    {
        var ai = _options.Value;
        if (!ai.Enabled || !ai.SubjectCutout.Enabled || !ai.SubjectCutout.PointSelection.Enabled)
        {
            return ReasonDisabled;
        }
        var (encoder, decoder) = (EncoderPath(ai), DecoderPath(ai));
        if (encoder is null || decoder is null) return OnnxSubjectMaskModel.ReasonModelDirMissing;
        foreach (var spec in new[]
        {
            new OnnxModelSpec(OnnxModel.SubjectPointEncoder, encoder),
            new OnnxModelSpec(OnnxModel.SubjectPointDecoder, decoder),
        })
        {
            var readiness = _factory.CheckReadiness(spec);
            if (!readiness.IsReady) return readiness.Reason;
        }
        return null;
    }

    public float[] PredictCandidates(float[] input, float x, float y, out int count)
    {
        var ai = _options.Value;
        var encoderPath = EncoderPath(ai) ?? throw new OnnxSessionUnavailableException(OnnxSubjectMaskModel.ReasonModelDirMissing);
        var decoderPath = DecoderPath(ai) ?? throw new OnnxSessionUnavailableException(OnnxSubjectMaskModel.ReasonModelDirMissing);
        _factory.EnsureNativeProviderInitialized();

        const int edge = SubjectPointModelShape.InputEdge;
        using var encoderLease = _factory.Acquire(new OnnxModelSpec(OnnxModel.SubjectPointEncoder, encoderPath));
        var encoder = encoderLease.Session;
        var features = encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(encoder.InputNames[0], new DenseTensor<float>(input, new[] { 1, 3, edge, edge })),
        });

        var prompt = new List<NamedOnnxValue>();
        foreach (var name in Features)
        {
            var feature = Output(features, name);
            prompt.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(feature.Data, feature.Shape.ToArray())));
        }
        prompt.Add(NamedOnnxValue.CreateFromTensor(
            "point_coords", new DenseTensor<float>(new[] { x * edge, y * edge }, new[] { 1, 1, 2 })));
        prompt.Add(NamedOnnxValue.CreateFromTensor(
            "point_labels", new DenseTensor<int>(new[] { 1 }, new[] { 1, 1 })));

        using var decoderLease = _factory.Acquire(new OnnxModelSpec(OnnxModel.SubjectPointDecoder, decoderPath));
        var low = Output(decoderLease.Session.Run(prompt), "low_res_masks");
        const int plane = SubjectPointModelShape.CandidateEdge * SubjectPointModelShape.CandidateEdge;
        var dims = low.Shape;
        if (dims.Count != 4 || dims[2] != SubjectPointModelShape.CandidateEdge
            || dims[3] != SubjectPointModelShape.CandidateEdge || dims[1] < 1)
        {
            throw new OnnxSessionUnavailableException(ReasonOutputShape);
        }
        count = dims[1];
        var data = low.Data;
        return data.Length == count * plane ? data : throw new OnnxSessionUnavailableException(ReasonOutputShape);
    }

    private static OnnxOutputTensor Output(IReadOnlyList<OnnxOutputTensor> outputs, string name)
    {
        foreach (var output in outputs)
        {
            if (output.Name == name) return output;
        }
        throw new OnnxSessionUnavailableException(ReasonOutputShape);
    }

    private static string? EncoderPath(AiOptions ai) => Resolve(ai, ai.SubjectCutout.PointSelection.EncoderFile);

    private static string? DecoderPath(AiOptions ai) => Resolve(ai, ai.SubjectCutout.PointSelection.DecoderFile);

    private static string? Resolve(AiOptions ai, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (Path.IsPathRooted(file)) return file;
        var dir = ai.Onnx.ModelDir;
        return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, file);
    }
}

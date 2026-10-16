using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.SubjectCutout;

namespace NubArca.Api.Tests.Ai.SubjectCutout;

public sealed class SubjectCutoutOptionsTests
{
    private static bool Ok(AiOptions o) => new AiOnnxOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Defaults_are_off_and_valid()
    {
        var o = new AiOptions();

        Assert.False(o.SubjectCutout.Enabled);
        Assert.False(o.SubjectCutout.PointSelection.Enabled);
        Assert.Equal("CPU", o.Onnx.OpenVino.DeviceFor(OnnxModel.SubjectPointEncoder));
        Assert.Equal(1024, o.SubjectCutout.InputSize);
        // Room for the first request's model compilation, cold SAM included.
        Assert.Equal(240, o.SubjectCutout.TimeoutSeconds);
        Assert.Equal("CPU", o.Onnx.OpenVino.DeviceFor(OnnxModel.SubjectCutout));
        Assert.True(Ok(o));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(4096)]
    public void An_input_size_the_model_cannot_take_is_rejected(int size)
    {
        var o = new AiOptions();
        o.SubjectCutout.InputSize = size;

        Assert.False(Ok(o));
    }

    [Fact]
    public void The_cutout_device_is_validated_with_the_others()
    {
        var o = new AiOptions();
        o.Onnx.ExecutionProvider = "openvino-direct";
        o.Onnx.OpenVino.NativeDir = "/opt/nubarca/ort-openvino";
        o.Onnx.OpenVino.SubjectCutoutDevice = "DUAL:CPU,GPU";

        Assert.False(Ok(o));

        o.Onnx.OpenVino.SubjectCutoutDevice = "GPU";
        Assert.True(Ok(o));
        Assert.Equal("GPU", o.Onnx.OpenVino.DeviceFor(OnnxModel.SubjectCutout));
        // Tapping a subject runs on the same device as the cutout.
        Assert.Equal("GPU", o.Onnx.OpenVino.DeviceFor(OnnxModel.SubjectPointEncoder));
        Assert.Equal("GPU", o.Onnx.OpenVino.DeviceFor(OnnxModel.SubjectPointDecoder));
    }

    [Fact]
    public void The_model_is_unavailable_until_ai_the_feature_and_the_file_are_all_there()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nubarca-cutout-{Guid.NewGuid():N}");
        var ai = new AiOptions();
        var model = new OnnxSubjectMaskModel(
            Options.Create(ai),
            new OnnxInferenceSessionFactory(Options.Create(ai), NullLogger<OnnxInferenceSessionFactory>.Instance));

        Assert.Equal(OnnxSubjectMaskModel.ReasonDisabled, model.UnavailableReason());

        ai.Enabled = true;
        Assert.Equal(OnnxSubjectMaskModel.ReasonDisabled, model.UnavailableReason());

        ai.SubjectCutout.Enabled = true;
        Assert.Equal(OnnxSubjectMaskModel.ReasonModelDirMissing, model.UnavailableReason());

        ai.Onnx.ModelDir = dir;
        Assert.Equal(OnnxInferenceSessionFactory.ReasonModelNotFound, model.UnavailableReason());
    }

    [Fact]
    public void Point_selection_is_unavailable_until_the_cutout_the_option_and_both_files_are_there()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nubarca-point-{Guid.NewGuid():N}");
        var ai = new AiOptions { Enabled = true };
        var model = new OnnxSubjectPointModel(
            Options.Create(ai),
            new OnnxInferenceSessionFactory(Options.Create(ai), NullLogger<OnnxInferenceSessionFactory>.Instance));

        ai.SubjectCutout.PointSelection.Enabled = true;
        Assert.Equal(OnnxSubjectPointModel.ReasonDisabled, model.UnavailableReason());

        ai.SubjectCutout.Enabled = true;
        Assert.Equal(OnnxSubjectMaskModel.ReasonModelDirMissing, model.UnavailableReason());

        ai.Onnx.ModelDir = dir;
        Assert.Equal(OnnxInferenceSessionFactory.ReasonModelNotFound, model.UnavailableReason());

        ai.SubjectCutout.PointSelection.Enabled = false;
        Assert.Equal(OnnxSubjectPointModel.ReasonDisabled, model.UnavailableReason());
    }
}

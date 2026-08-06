using NubArca.Api.Ai;

namespace NubArca.Api.Tests.Ai;

// Gate 3B: strict execution-provider configuration. Unknown providers and
// incomplete provider-specific config are rejected at startup.
public sealed class AiOnnxOptionsValidatorTests
{
    private static AiOptions Base() => new();

    private static AiOptions WithDirect(Action<AiOnnxOpenVinoOptions>? tweak = null)
    {
        var o = Base();
        o.Onnx.ExecutionProvider = "openvino-direct";
        o.Onnx.OpenVino.NativeDir = "/opt/nubarca/ort-openvino";
        tweak?.Invoke(o.Onnx.OpenVino);
        return o;
    }

    private static bool Ok(AiOptions o) => new AiOnnxOptionsValidator().Validate(null, o).Succeeded;
    private static string Why(AiOptions o) => new AiOnnxOptionsValidator().Validate(null, o).FailureMessage ?? "";

    [Fact]
    public void Default_OnnxRuntime_Is_Valid()
    {
        Assert.True(Ok(Base())); // default provider = onnxruntime
    }

    [Fact]
    public void OpenVinoDirect_Complete_Is_Valid()
    {
        Assert.True(Ok(WithDirect(ov =>
        {
            ov.PhotoImageDevice = "CPU";
            ov.PhotoTextDevice = "GPU";
        })));
    }

    [Fact]
    public void Removed_Sidecar_Providers_Are_Rejected()
    {
        // SigLIP direct milestone: the legacy Python sidecar providers are gone;
        // a stale deployment config must fail startup, never silently run CPU.
        foreach (var provider in new[] { "openvino-sidecar", "openvino" })
        {
            var o = Base();
            o.Onnx.ExecutionProvider = provider;
            Assert.False(Ok(o));
            Assert.Contains("ExecutionProvider", Why(o));
        }
    }

    [Fact]
    public void Unknown_Provider_Is_Rejected()
    {
        var o = Base();
        o.Onnx.ExecutionProvider = "cuda";
        Assert.False(Ok(o));
        Assert.Contains("ExecutionProvider", Why(o));
    }

    [Fact]
    public void Direct_Without_NativeDir_Is_Rejected()
    {
        var o = WithDirect(ov => ov.NativeDir = "  ");
        Assert.False(Ok(o));
        Assert.Contains("NativeDir", Why(o));
    }

    [Fact]
    public void Direct_With_Invalid_Device_Is_Rejected()
    {
        var o = WithDirect(ov => ov.FaceDetectorDevice = "NPU");
        Assert.False(Ok(o));
        Assert.Contains("FaceDetectorDevice", Why(o));
    }

    [Fact]
    public void Direct_With_Non_Fp32_Gpu_Precision_Is_Rejected()
    {
        var o = WithDirect(ov => ov.GpuPrecision = "FP16");
        Assert.False(Ok(o));
        Assert.Contains("FP32", Why(o));
    }
}

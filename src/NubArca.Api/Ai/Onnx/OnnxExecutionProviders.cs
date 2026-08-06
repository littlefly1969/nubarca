namespace NubArca.Api.Ai.Onnx;

// Execution-provider identity for the ONNX substrate. Two explicit values are
// supported; unknown values are rejected at startup (see AiOnnxOptionsValidator)
// — never silently treated as CPU. The legacy Python HTTP sidecar providers
// ("openvino-sidecar" / "openvino") were removed with the SigLIP direct
// milestone: every ONNX model now runs in-process.
public static class OnnxExecutionProviders
{
    // In-process ONNX Runtime CPU (the safe fallback / default).
    public const string OnnxRuntime = "onnxruntime";
    // In-process OpenVINO Execution Provider (native stack from
    // scripts/openvino-direct).
    public const string OpenVinoDirect = "openvino-direct";

    // Normalizes user input to a canonical provider value. Unknown input is
    // returned lower-cased so the validator can reject it with the original
    // intent visible.
    public static string Normalize(string? provider) =>
        provider?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool IsKnown(string? provider) =>
        Normalize(provider) is OnnxRuntime or OpenVinoDirect;
}

// Device placement tokens for the openvino-direct path. A SINGLE device ("CPU" or
// "GPU") maps 1:1 to an OpenVINO device_type. "DUAL:CPU,GPU" is a NubArca-level
// placement — two exclusive single-device sessions behind a bounded dispatcher (the
// benchmarked CPU+GPU tandem, see docs/model-deployment/openvino-siglip2-benchmark-2026-07.md)
// — and is NEVER passed to OpenVINO as a device_type; the factory splits it into a
// CPU leg and a GPU leg. Both legs run FP32 (output-equivalent, honors the
// persisted-embedding invariant).
public static class OnnxDevice
{
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string DualCpuGpu = "DUAL:CPU,GPU";

    // True only for the exact DUAL:CPU,GPU placement (case/space-insensitive).
    public static bool IsDual(string? device) =>
        string.Equals(device?.Trim(), DualCpuGpu, StringComparison.OrdinalIgnoreCase);
}

// Logical models the factory can place on a device. These map to the four ONNX
// model files NubArca runs.
public enum OnnxModel
{
    PhotoImage,
    PhotoText,
    FaceDetector,
    FaceRecognizer,
}

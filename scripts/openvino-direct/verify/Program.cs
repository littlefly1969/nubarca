using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Gate 3A native verifier — fail-closed. Proves, inside the target Linux image,
// that the OpenVINO-enabled ORT core loads via a SUPPORTED NativeLibrary resolver
// (no Windows-PE overwrite) and that the OpenVINO EP is actually present and runs.
//
// Exit codes: 0 = OK; 10 = core missing; 11 = OpenVINO EP absent; 12 = inference
// failed; 13 = numeric check failed. Non-zero fails the Docker build.
//
// Env: NUBARCA_ORT_NATIVE_DIR (dir with libonnxruntime.so.<ver>),
//      NUBARCA_ORT_ABI (default 1.24.1), arg[0]=model.onnx, arg[1]=device (CPU|GPU).

string nativeDir = Environment.GetEnvironmentVariable("NUBARCA_ORT_NATIVE_DIR") ?? "/opt/nubarca/ort-openvino";
string abi = Environment.GetEnvironmentVariable("NUBARCA_ORT_ABI") ?? "1.24.1";
string core = Path.Combine(nativeDir, $"libonnxruntime.so.{abi}");
string model = args.Length > 0 ? args[0] : "model.onnx";
string device = args.Length > 1 ? args[1] : "CPU";

Console.WriteLine($"[verify] core={core} device={device}");
if (!File.Exists(core)) { Console.Error.WriteLine($"FATAL: OV-enabled ORT core missing: {core}"); return 10; }

// Supported resolver: map the ORT P/Invoke (literal "onnxruntime.dll") to our
// Linux OpenVINO core, registered before any native call touches NativeMethods.
NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, (name, asm, path) =>
    name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryLoad(core, out var h)
        ? h : IntPtr.Zero);

var providers = OrtEnv.Instance().GetAvailableProviders();
Console.WriteLine("[verify] providers: " + string.Join(", ", providers));
if (!providers.Contains("OpenVINOExecutionProvider"))
{
    Console.Error.WriteLine("FATAL: OpenVINOExecutionProvider absent — this is NOT an OpenVINO-enabled ORT build.");
    return 11;
}

try
{
    using var so = new SessionOptions();
    var opts = new Dictionary<string, string> { ["device_type"] = device };
    if (device.Equals("GPU", StringComparison.OrdinalIgnoreCase)) opts["precision"] = "FP32";
    so.AppendExecutionProvider("OpenVINO", opts);
    using var s = new InferenceSession(model, so);
    var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("X", new DenseTensor<float>(new float[] { 1, 2, 3, 4 }, new[] { 1, 4 })),
    };
    using var r = s.Run(inputs);
    var o = r.First().AsTensor<float>().ToArray();
    Console.WriteLine($"[verify] OpenVINO-{device} output=[{string.Join(", ", o)}]");
    // model is Y = 2*X, so expect [2,4,6,8]; guards against a silently-wrong build.
    var expected = new float[] { 2, 4, 6, 8 };
    for (int i = 0; i < expected.Length; i++)
        if (float.IsNaN(o[i]) || Math.Abs(o[i] - expected[i]) > 1e-4f)
        { Console.Error.WriteLine("FATAL: unexpected inference output"); return 13; }
}
catch (Exception e) { Console.Error.WriteLine($"FATAL: inference failed: {e.GetType().Name}: {e.Message}"); return 12; }

Console.WriteLine($"[verify] OK — OpenVINO EP present and OpenVINO-{device} inference verified.");
return 0;

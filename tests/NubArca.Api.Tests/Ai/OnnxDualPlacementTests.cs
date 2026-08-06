using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx;

namespace NubArca.Api.Tests.Ai;

// DUAL:CPU,GPU image-tower placement: the bounded CPU+GPU tandem from
// docs/model-deployment/openvino-siglip2-benchmark-2026-07.md. Covers the exclusive
// dispatcher (distinct devices, blocking, idempotent release), leg compilation with
// graceful GPU degrade, routing, the embedder concurrency floor, device parsing, and
// validation. The native session creator and resolver are faked — no OpenVINO stack.
public sealed class OnnxDualPlacementTests
{
    private sealed class DeviceFake(string device) : IOnnxSession
    {
        public string Device { get; } = device;
        public int DisposeCount;
        public IReadOnlyList<string> InputNames => Array.Empty<string>();
        public IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
            => throw new NotSupportedException();
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static OnnxInferenceSessionFactory DirectFactory(
        Func<OnnxSessionCreateSpec, IOnnxSession> creator, string photoImageDevice = "DUAL:CPU,GPU")
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = "/opt/ort-openvino";
        o.Onnx.OpenVino.PhotoImageDevice = photoImageDevice;
        return new OnnxInferenceSessionFactory(
            Options.Create(o), NullLogger<OnnxInferenceSessionFactory>.Instance, creator, installResolver: _ => { });
    }

    private static OnnxInferenceSessionFactory.DualExecutor Exec(string device) =>
        new(new DeviceFake(device), device);

    private static string DeviceOf(IOnnxSessionLease lease) => ((DeviceFake)lease.Session).Device;

    // ---- exclusive dispatcher ----

    [Fact]
    public void Pool_Hands_Out_Distinct_Devices_And_Blocks_When_Exhausted()
    {
        using var pool = new OnnxInferenceSessionFactory.DualSessionPool(new[] { Exec("CPU"), Exec("GPU") });
        Assert.Equal(2, pool.Count);

        var a = pool.Acquire();
        var b = pool.Acquire();
        Assert.Equal(new[] { "CPU", "GPU" }, new[] { DeviceOf(a), DeviceOf(b) }.OrderBy(x => x).ToArray());

        // A third acquire blocks until a device is released, then reuses the freed one.
        var third = Task.Run(() => pool.Acquire());
        Assert.False(third.Wait(TimeSpan.FromMilliseconds(200)));
        var freed = DeviceOf(a);
        a.Dispose();
        Assert.True(third.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(freed, DeviceOf(third.Result));

        b.Dispose();
        third.Result.Dispose();
    }

    [Fact]
    public void Pool_Lease_Dispose_Is_Idempotent()
    {
        using var pool = new OnnxInferenceSessionFactory.DualSessionPool(new[] { Exec("CPU") });
        var lease = pool.Acquire();
        lease.Dispose();
        lease.Dispose(); // must not release the semaphore twice / inflate capacity

        var a = pool.Acquire();
        var second = Task.Run(() => pool.Acquire());
        Assert.False(second.Wait(TimeSpan.FromMilliseconds(200))); // still a 1-slot pool
        a.Dispose();
        Assert.True(second.Wait(TimeSpan.FromSeconds(2)));
        second.Result.Dispose();
    }

    [Fact]
    public void Pool_Rejects_Empty_Executor_Set()
        => Assert.Throws<ArgumentException>(() =>
            new OnnxInferenceSessionFactory.DualSessionPool(Array.Empty<OnnxInferenceSessionFactory.DualExecutor>()));

    // ---- leg compilation ----

    [Fact]
    public void CreateDualPool_Compiles_Cpu_And_Gpu_Legs()
    {
        using var factory = DirectFactory(spec => new DeviceFake(spec.Device));
        using var pool = factory.CreateDualPool(new OnnxModelSpec(OnnxModel.PhotoImage, "/m/photo.onnx"));

        Assert.Equal(2, pool.Count);
        var a = pool.Acquire();
        var b = pool.Acquire();
        Assert.Equal(new[] { "CPU", "GPU" }, new[] { DeviceOf(a), DeviceOf(b) }.OrderBy(x => x).ToArray());
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void CreateDualPool_Degrades_To_Cpu_Only_When_Gpu_Unavailable()
    {
        using var factory = DirectFactory(spec => spec.Device == "GPU"
            ? throw new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonDeviceUnavailable)
            : new DeviceFake(spec.Device));
        using var pool = factory.CreateDualPool(new OnnxModelSpec(OnnxModel.PhotoImage, "/m/photo.onnx"));

        Assert.Equal(1, pool.Count); // graceful CPU-only
        var a = pool.Acquire();
        Assert.Equal("CPU", DeviceOf(a));
        a.Dispose();
    }

    [Fact]
    public void CreateDualPool_Fails_Closed_When_Cpu_Leg_Cannot_Compile()
    {
        using var factory = DirectFactory(spec => spec.Device == "CPU"
            ? throw new OnnxSessionUnavailableException(OnnxInferenceSessionFactory.ReasonCompileFailed)
            : new DeviceFake(spec.Device));

        Assert.Throws<OnnxSessionUnavailableException>(
            () => factory.CreateDualPool(new OnnxModelSpec(OnnxModel.PhotoImage, "/m/photo.onnx")));
    }

    // ---- routing ----

    [Theory]
    [InlineData("DUAL:CPU,GPU", true)]
    [InlineData("dual:cpu,gpu", true)]
    [InlineData("CPU", false)]
    [InlineData("GPU", false)]
    public void IsDualPlacement_Tracks_Configured_PhotoImage_Device(string device, bool expected)
    {
        using var factory = DirectFactory(_ => new DeviceFake("CPU"), photoImageDevice: device);
        Assert.Equal(expected, factory.IsDualPlacement(new OnnxModelSpec(OnnxModel.PhotoImage, "/m/photo.onnx")));
        // Never DUAL for other models regardless of the image-tower setting.
        Assert.False(factory.IsDualPlacement(new OnnxModelSpec(OnnxModel.PhotoText, "/m/text.onnx")));
    }

    [Fact]
    public void IsDualPlacement_Is_False_For_OnnxRuntime_Provider()
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "onnxruntime" } };
        o.Onnx.OpenVino.PhotoImageDevice = "DUAL:CPU,GPU"; // ignored: not openvino-direct
        using var factory = new OnnxInferenceSessionFactory(
            Options.Create(o), NullLogger<OnnxInferenceSessionFactory>.Instance,
            _ => new DeviceFake("CPU"), installResolver: _ => { });

        Assert.False(factory.IsDualPlacement(new OnnxModelSpec(OnnxModel.PhotoImage, "/m/photo.onnx")));
    }

    // ---- embedder concurrency floor ----

    [Fact]
    public void Dual_Image_Tower_Raises_Concurrency_Floor_To_Two()
    {
        var ai = new AiOptions { MaxConcurrency = 1, Onnx = { ExecutionProvider = "openvino-direct" } };
        ai.Onnx.OpenVino.PhotoImageDevice = "DUAL:CPU,GPU";
        Assert.Equal(2, OnnxImageEmbedder.ResolveImageConcurrency(ai));

        ai.MaxConcurrency = 5; // a higher configured value still wins
        Assert.Equal(5, OnnxImageEmbedder.ResolveImageConcurrency(ai));
    }

    [Fact]
    public void Single_Device_Image_Tower_Uses_Configured_Concurrency()
    {
        var gpu = new AiOptions { MaxConcurrency = 1, Onnx = { ExecutionProvider = "openvino-direct" } };
        gpu.Onnx.OpenVino.PhotoImageDevice = "GPU";
        Assert.Equal(1, OnnxImageEmbedder.ResolveImageConcurrency(gpu));

        var cpu = new AiOptions { MaxConcurrency = 1, Onnx = { ExecutionProvider = "onnxruntime" } };
        Assert.Equal(1, OnnxImageEmbedder.ResolveImageConcurrency(cpu));
    }

    // ---- device parsing + validation ----

    [Theory]
    [InlineData("DUAL:CPU,GPU", true)]
    [InlineData("  dual:cpu,gpu  ", true)]
    [InlineData("CPU", false)]
    [InlineData("GPU", false)]
    [InlineData("DUAL:GPU,CPU", false)]
    [InlineData(null, false)]
    public void OnnxDevice_IsDual_Matches_Only_The_Canonical_Token(string? device, bool expected)
        => Assert.Equal(expected, OnnxDevice.IsDual(device));

    [Fact]
    public void Validator_Accepts_Dual_For_Image_Tower_Only()
    {
        Assert.True(Validate(ov => ov.PhotoImageDevice = "DUAL:CPU,GPU").Succeeded);
        Assert.False(Validate(ov => ov.PhotoTextDevice = "DUAL:CPU,GPU").Succeeded);
        Assert.False(Validate(ov => ov.FaceDetectorDevice = "DUAL:CPU,GPU").Succeeded);
        Assert.False(Validate(ov => ov.FaceRecognizerDevice = "DUAL:CPU,GPU").Succeeded);
    }

    private static ValidateOptionsResult Validate(Action<AiOnnxOpenVinoOptions> configure)
    {
        var o = new AiOptions { Onnx = { ExecutionProvider = "openvino-direct" } };
        o.Onnx.OpenVino.NativeDir = "/opt/ort-openvino";
        configure(o.Onnx.OpenVino);
        return new AiOnnxOptionsValidator().Validate(null, o);
    }
}

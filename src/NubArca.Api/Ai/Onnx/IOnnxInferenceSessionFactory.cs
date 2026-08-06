using Microsoft.ML.OnnxRuntime;

namespace NubArca.Api.Ai.Onnx;

// Centralized construction + caching of in-process ONNX Runtime sessions for the
// ONNX substrate. It owns execution-provider selection, per-model device
// selection, OpenVINO native-provider initialization, CPU thread limits, OpenVINO
// cache configuration, session caching, deterministic disposal and sanitized
// readiness — and NOTHING about users, DB state, files, blobs, albums, parties or
// HTTP requests. Embedders keep preprocessing, tensor construction, output
// selection, decoding and normalization.
//
// Gate 3B introduces the abstraction; DUAL executors (one exclusive CPU + one
// exclusive GPU session behind a bounded dispatcher) arrive in a later gate — the
// lease type exists now so call sites stay stable when exclusivity is added.
public interface IOnnxInferenceSessionFactory : IDisposable
{
    // Non-throwing readiness probe. Returns a sanitized reason token when a model
    // cannot be served in-process (missing model, provider unavailable, native
    // library missing, requested device unavailable, …).
    OnnxSessionReadiness CheckReadiness(OnnxModelSpec spec);

    // Borrows a session for the model. For openvino-direct a failed initialization
    // throws OnnxSessionUnavailableException with a sanitized reason — it NEVER
    // silently falls back to CPU. Dispose the lease when done.
    IOnnxSessionLease Acquire(OnnxModelSpec spec);

    // Idempotently installs the OpenVINO-enabled native core resolver for the
    // process when the configured provider is openvino-direct, so that EVERY
    // subsequent ORT session in this process — including non-migrated CPU sessions
    // built directly elsewhere — binds the same OpenVINO-enabled core. No-op for
    // onnxruntime. Must be called before the first ORT session
    // is created in a host that mixes migrated and non-migrated models.
    void EnsureNativeProviderInitialized();

    // The process-global native-core selection (diagnostics). Set once on first ORT
    // use and immutable thereafter; provider changes require a process restart.
    OnnxNativeCoreState NativeCoreState { get; }
}

// Identifies a concrete model to serve: its logical role (→ device) and the
// resolved on-disk model path (the caller owns path resolution).
public readonly record struct OnnxModelSpec(OnnxModel Model, string ModelPath);

// Readiness of the in-process path for a model. `Reason` is a short sanitized
// token (never a path, device string, native message or secret).
public readonly record struct OnnxSessionReadiness(bool IsReady, string? Reason)
{
    public static readonly OnnxSessionReadiness Ready = new(true, null);
    public static OnnxSessionReadiness NotReady(string reason) => new(false, reason);
}

// Thrown by Acquire when a session cannot be served in-process. ReasonCode is the
// same sanitized token surfaced by CheckReadiness.
public sealed class OnnxSessionUnavailableException(string reasonCode)
    : Exception($"ONNX in-process session unavailable ({reasonCode}).")
{
    public string ReasonCode { get; } = reasonCode;
}

// A borrowed session. In Gate 3B every lease shares the cached session and Dispose
// is a no-op; a later gate turns this into an exclusive lease for DUAL executors.
public interface IOnnxSessionLease : IDisposable
{
    IOnnxSession Session { get; }
}

// Minimal seam over Microsoft.ML.OnnxRuntime.InferenceSession so the factory's
// caching/lifecycle is testable without the OpenVINO native stack, and so the
// factory — not each embedder — owns session construction/disposal. Run returns
// already-materialized float outputs (the adapter disposes the native result), so
// the interface exposes no un-fakeable ORT result types.
public interface IOnnxSession : IDisposable
{
    IReadOnlyList<string> InputNames { get; }
    IReadOnlyList<OnnxOutputTensor> Run(IReadOnlyCollection<NamedOnnxValue> inputs);
}

// A single materialized model output: name, row-major float data, and shape.
public readonly record struct OnnxOutputTensor(string Name, float[] Data, IReadOnlyList<int> Shape);

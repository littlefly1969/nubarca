namespace NubArca.Api.Ai.Onnx.Face;

// Face AI milestone: stable, sanitized preload/readiness state for the direct
// (in-process OpenVINO) face pipeline. Liveness and readiness are DISTINCT: the
// process is live while models compile (so /health stays green and the container
// is not killed mid-compile), but it is NOT ready — and must not accept HTTP AI
// face traffic — until BOTH configured direct sessions compile AND pass a bounded
// synthetic inference. All tokens here are stable and safe to log / surface to a
// readiness probe: never a path, device string, native message, tensor content,
// embedding or user data.

// The lifecycle states, in order. STARTING → NATIVE_RUNTIME_INITIALIZING →
// FACE_DETECTOR_COMPILING → FACE_DETECTOR_VALIDATING → FACE_RECOGNIZER_COMPILING →
// FACE_RECOGNIZER_VALIDATING → PHOTO_TEXT_COMPILING → PHOTO_TEXT_VALIDATING →
// READY, or FAILED at any step. Stages for models this process does not serve
// (no profile configured / no text tower) are skipped.
public static class FacePreloadStates
{
    public const string Starting = "STARTING";
    public const string NativeRuntimeInitializing = "NATIVE_RUNTIME_INITIALIZING";
    public const string FaceDetectorCompiling = "FACE_DETECTOR_COMPILING";
    public const string FaceDetectorValidating = "FACE_DETECTOR_VALIDATING";
    public const string FaceRecognizerCompiling = "FACE_RECOGNIZER_COMPILING";
    public const string FaceRecognizerValidating = "FACE_RECOGNIZER_VALIDATING";
    // SigLIP direct milestone: the API hosts the SigLIP2 TEXT tower (semantic
    // query embedding), so its readiness is compile-backed like the face models.
    public const string PhotoTextCompiling = "PHOTO_TEXT_COMPILING";
    public const string PhotoTextValidating = "PHOTO_TEXT_VALIDATING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

// Sanitized failure codes surfaced when preload cannot complete. These are the
// stable operator-facing tokens; the underlying factory reason (also sanitized) is
// mapped onto the code for the stage that failed.
public static class FacePreloadFailureCodes
{
    public const string OrtNativeCoreMissing = "ORT_NATIVE_CORE_MISSING";
    public const string OrtNativeLoadFailed = "ORT_NATIVE_LOAD_FAILED";
    public const string OrtAbiMismatch = "ORT_ABI_MISMATCH";
    public const string OpenVinoEpMissing = "OPENVINO_EP_MISSING";
    public const string OpenVinoDeviceUnavailable = "OPENVINO_DEVICE_UNAVAILABLE";
    public const string FaceDetectorModelMissing = "FACE_DETECTOR_MODEL_MISSING";
    public const string FaceDetectorCompileFailed = "FACE_DETECTOR_COMPILE_FAILED";
    public const string FaceDetectorValidationFailed = "FACE_DETECTOR_VALIDATION_FAILED";
    public const string FaceRecognizerModelMissing = "FACE_RECOGNIZER_MODEL_MISSING";
    public const string FaceRecognizerCompileFailed = "FACE_RECOGNIZER_COMPILE_FAILED";
    public const string FaceRecognizerValidationFailed = "FACE_RECOGNIZER_VALIDATION_FAILED";
    // SigLIP direct milestone: distinct codes for the photo towers, so an
    // operator can tell WHICH engine (and which missing asset) failed readiness.
    public const string PhotoTextModelMissing = "PHOTO_TEXT_MODEL_MISSING";
    public const string PhotoTextTokenizerMissing = "PHOTO_TEXT_TOKENIZER_MISSING";
    public const string PhotoTextCompileFailed = "PHOTO_TEXT_COMPILE_FAILED";
    public const string PhotoTextValidationFailed = "PHOTO_TEXT_VALIDATION_FAILED";
    public const string PhotoImageModelMissing = "PHOTO_IMAGE_MODEL_MISSING";
    public const string PhotoImageCompileFailed = "PHOTO_IMAGE_COMPILE_FAILED";
    public const string PhotoImageValidationFailed = "PHOTO_IMAGE_VALIDATION_FAILED";
    public const string PreloadTimeout = "PRELOAD_TIMEOUT";
}

// A sanitized snapshot of preload progress. Detail is a short stable token (e.g.
// "provider=onnxruntime", "no-face-model-configured") — never sensitive.
public readonly record struct FacePreloadStatus(string State, string? FailureCode, string? Detail)
{
    public bool IsReady => State == FacePreloadStates.Ready;
    public bool IsFailed => State == FacePreloadStates.Failed;
}

// Read side of the preload state, consumed by the readiness endpoint / container
// health check. The write side (OnnxFacePreloadState) is internal to the preloader.
public interface IOnnxFacePreloadState
{
    FacePreloadStatus Current { get; }
}

// Thread-safe, single-writer preload state. The hosted preloader advances it
// through the lifecycle; readiness probes read Current concurrently.
public sealed class OnnxFacePreloadState : IOnnxFacePreloadState
{
    private volatile FacePreloadStatusBox _box =
        new(new FacePreloadStatus(FacePreloadStates.Starting, null, null));

    public FacePreloadStatus Current => _box.Status;

    internal void Advance(string state, string? detail = null) =>
        _box = new FacePreloadStatusBox(new FacePreloadStatus(state, null, detail));

    internal void Ready(string? detail = null) =>
        _box = new FacePreloadStatusBox(new FacePreloadStatus(FacePreloadStates.Ready, null, detail));

    internal void Fail(string failureCode, string? detail = null) =>
        _box = new FacePreloadStatusBox(new FacePreloadStatus(FacePreloadStates.Failed, failureCode, detail));

    // A reference box makes the volatile publish atomic (a struct field cannot be
    // volatile), so a concurrent reader always sees a fully-written status.
    private sealed record FacePreloadStatusBox(FacePreloadStatus Status);
}

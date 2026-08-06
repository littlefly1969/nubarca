namespace NubArca.Api.Aesthetics;

// Terminal + in-flight statuses for an AestheticAnalysisRun. Constrained strings
// (not an enum) to match the repository convention (see PlateAnalysisJobStatuses,
// JobStatuses) and to keep migrations provider-agnostic.
public static class AestheticRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) => status is
        Queued or Running or Succeeded or Failed or Cancelled;

    public static bool IsTerminal(string? status) => status is
        Succeeded or Failed or Cancelled;

    public static bool IsLive(string? status) => status is Queued or Running;
}

// Stable, client-safe error codes for a failed run. NEVER a stack trace, model
// path, storage key, or raw model/exception message.
public static class AestheticErrorCodes
{
    // The model feature (or the requested capability) is disabled by config.
    public const string FeatureDisabled = "feature_disabled";
    public const string CapabilityDisabled = "capability_disabled";
    // The sidecar is unreachable / not ready — an environment state, not a
    // content failure.
    public const string ModelUnavailable = "model_unavailable";
    // The sidecar accepted the request but exceeded the deadline.
    public const string Timeout = "timeout";
    // The sidecar returned a malformed / partial / non-finite / out-of-range
    // response that failed strict validation.
    public const string InvalidModelOutput = "invalid_model_output";
    // The source image could not be decoded / preprocessed (bad bytes,
    // decompression bomb, unsupported format).
    public const string UnsupportedImage = "unsupported_image";
    // The lab item / blob backing the run disappeared.
    public const string ItemNotFound = "item_not_found";
    // Any other content-related failure (sanitized).
    public const string AnalysisFailed = "analysis_failed";
}

// Preprocessing profiles. `official-v1` MUST preserve the checkpoint's own
// preprocessing (448x448 dynamic tiling, use_thumbnail, max_num=12, ImageNet
// normalization); any reduction MUST use a DIFFERENT key so a run is never
// silently compared to the official paper/model behavior.
public static class AestheticPreprocessingProfiles
{
    public const string OfficialV1 = "human-aesexpert-official-v1";
    public const string ControlledV1 = "human-aesexpert-controlled-v1";

    public static bool IsKnown(string? key) => key is OfficialV1 or ControlledV1;
}

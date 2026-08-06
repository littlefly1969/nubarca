namespace NubArca.Api.Ai.Resolution;

// Sanitized, content-free reason codes for why a capability is unavailable.
// Safe to surface to admins/logs/diagnostics: short tokens, never secrets,
// paths, ids, or messages with sensitive content.
public static class AiUnavailableReasons
{
    public const string Disabled = "ai-disabled";
    public const string NoDefaultProfile = "no-default-profile";
    public const string ProfileNotFound = "profile-not-found";
    public const string ProfileDisabled = "profile-disabled";
    public const string ModelUnavailable = "model-unavailable";
    public const string ProviderNone = "provider-none";
    public const string ProviderUnavailable = "provider-unavailable";
    public const string CapabilityUnsupported = "capability-unsupported";

    // Phase 2A: backend matched but its environment isn't ready (generic
    // fallback). A backend may return a more specific token from CheckReadiness
    // (e.g. the ONNX backend reports model-dir/model-file absence).
    public const string BackendNotReady = "backend-not-ready";

    // Photo-profile lifecycle: an explicitly named profile exists but is not
    // usable for image-embedding similarity.
    //  - capability-mismatch: the profile's capability is not image-embedding.
    //  - profile-dimension-invalid: an embedding profile with no positive
    //    dimension (cannot host comparable vectors).
    public const string CapabilityMismatch = "capability-mismatch";
    public const string ProfileDimensionInvalid = "profile-dimension-invalid";
}

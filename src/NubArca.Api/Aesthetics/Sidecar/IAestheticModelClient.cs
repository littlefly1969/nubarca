namespace NubArca.Api.Aesthetics.Sidecar;

// The controlled boundary to the local HumanAesExpert sidecar. The worker passes
// bounded, preprocessed image bytes + the request envelope; the client streams
// them over the internal network and returns the parsed, PRE-validation
// response. NO blob-store access, NO paths — bytes in, response out.
//
// Implementations MUST NOT throw raw HTTP/model detail to the caller. They throw
// AestheticSidecarException with a stable, sanitized code (AestheticErrorCodes)
// so the analysis service records a client-safe failure.
public interface IAestheticModelClient
{
    // True only when a sidecar base URL is configured. When false, the feature is
    // effectively unavailable and the analysis service fails runs with
    // model_unavailable WITHOUT marking the content skipped/failed permanently.
    bool IsConfigured { get; }

    Task<AestheticSidecarResponse> AnalyzeAsync(
        AestheticSidecarRequest request,
        byte[] imageBytes,
        string imageContentType,
        CancellationToken cancellationToken);
}

// Sanitized model/transport failure. `Code` is one of AestheticErrorCodes; the
// message is safe (no path/host/stack). The inner exception (if any) is for
// server-side logging only and is NEVER surfaced.
public sealed class AestheticSidecarException : Exception
{
    public string Code { get; }

    public AestheticSidecarException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

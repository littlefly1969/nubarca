namespace NubArca.Api.Aesthetics.Sidecar;

// Versioned request/response contract between the API/worker and the internal
// HumanAesExpert Python sidecar. ONE contract version; a breaking change bumps
// ContractVersion and is validated strictly on the response.
//
// The IMAGE is streamed as a bounded multipart body (see the client); it is NOT
// part of this JSON. These records are the metadata/response envelope only.
public static class AestheticSidecarContract
{
    public const int Version = 1;

    // Hard caps enforced by the strict response validator (defense in depth; the
    // sidecar also caps these).
    public const int MaxMetrics = 64;
    public const int MaxTexts = 16;
    public const int MaxTextLength = 8000;
    public const int MaxWarnings = 16;
    public const int MaxWarningLength = 256;
    // Reject an over-large raw response body before persisting it as provenance.
    public const int MaxRawResponseBytes = 64 * 1024;
}

// The request envelope (sent as multipart fields alongside the image part).
public sealed record AestheticSidecarRequest(
    int ContractVersion,
    string ProfileKey,
    IReadOnlyList<string> Capabilities,
    string Language,
    string PreprocessingProfileKey);

// The parsed, PRE-validation sidecar response (mirrors the JSON shape). All
// validation happens in AestheticSidecarResponseValidator before any value is
// trusted or persisted.
public sealed record AestheticSidecarResponse(
    int ContractVersion,
    string ProfileKey,
    string? ModelName,
    string? ModelRevision,
    string? RuntimeName,
    string? RuntimeVersion,
    string? PreprocessingProfileKey,
    IReadOnlyList<string> CompletedCapabilities,
    IReadOnlyList<AestheticSidecarMetric> Metrics,
    IReadOnlyList<AestheticSidecarText> Texts,
    IReadOnlyList<string> Warnings,
    long DurationMs);

public sealed record AestheticSidecarMetric(
    string Key,
    double Value,
    double ScaleMin,
    double ScaleMax,
    double? Confidence,
    int Version);

public sealed record AestheticSidecarText(
    string Kind,
    string Language,
    string Text,
    int? PromptTemplateVersion);

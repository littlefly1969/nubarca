namespace NubArca.Api.Domain;

// Owner-private audit/debug record of one ALPR model run (linked to a
// PlateAnalysisJob). Captures sanitized model identity + timing + counts so an
// operator can reason about a run WITHOUT exposing model paths, weights, or blob
// internals. Never surfaced in a normal user DTO.
public class PlateAnalysisModelRun
{
    public Guid Id { get; set; }
    public Guid PlateAnalysisJobId { get; set; }

    public string ProfileKey { get; set; } = string.Empty;

    // Sanitized model identity (names/versions only — never a filesystem path).
    public string? DetectorName { get; set; }
    public string? DetectorVersion { get; set; }
    public string? OcrName { get; set; }
    public string? OcrVersion { get; set; }

    public int InputWidth { get; set; }
    public int InputHeight { get; set; }
    public long DurationMs { get; set; }
    public int DetectionsCount { get; set; }

    public DateTime CreatedAt { get; set; }
}

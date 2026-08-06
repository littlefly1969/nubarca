namespace NubArca.Api.Domain;

// One immutable analysis of an AestheticLabItem, retained as history. Created
// (queued) when the owner requests analysis; the durable background job
// (JobTypes.AestheticsAnalyze) drives it running → succeeded/failed on the
// worker. A completed run is never mutated; a re-request creates a NEW run.
//
// Model/runtime identity + timing are captured so an operator can reason about a
// run WITHOUT exposing model paths, weights, or blob internals. RawOutputJson is
// the bounded, validated sidecar response kept as internal provenance only — it
// NEVER replaces the normalized AestheticMetric rows and is never surfaced in an
// ordinary API response.
public class AestheticAnalysisRun
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }
    public Guid AestheticLabItemId { get; set; }

    // Profile + sanitized model identity (names/versions only — never a path).
    public string ProfileKey { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public string? ModelRevision { get; set; }
    public string? RuntimeName { get; set; }
    public string? RuntimeVersion { get; set; }
    public string PreprocessingProfileKey { get; set; } = string.Empty;

    // CSV of stable capability keys requested / completed (small, bounded set).
    public string RequestedCapabilities { get; set; } = string.Empty;
    public string CompletedCapabilities { get; set; } = string.Empty;

    // AestheticRunStatuses (queued/running/succeeded/failed/cancelled).
    public string Status { get; set; } = Aesthetics.AestheticRunStatuses.Queued;

    // The durable background job that drives this run (null until enqueued).
    public Guid? BackgroundJobId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }

    // Bounded, validated sidecar response, internal provenance only. jsonb on
    // PostgreSQL. NEVER returned by an ordinary API response.
    public string? RawOutputJson { get; set; }

    // Sanitized model warnings surfaced to the owner (jsonb array of safe
    // strings). Never raw model text / paths.
    public string? WarningsJson { get; set; }

    // Stable, client-safe failure code (AestheticErrorCodes). Never a stack
    // trace / model path / storage internal.
    public string? ErrorCode { get; set; }
}

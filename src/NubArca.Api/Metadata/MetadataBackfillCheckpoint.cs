using System.Text.Json;

namespace NubArca.Api.Metadata;

// Versioned, INTERNAL-ONLY resume state for the sliceable metadata backfill.
// The job engine persists this to BackgroundJob.CheckpointJson between slices;
// it is never returned by any admin DTO. It holds only COUNTS plus the set of
// blob ids that failed to resolve this run — never extracted/raw metadata,
// storage keys, sha, or paths.
//
// No positional cursor is needed: a blob that reaches the current extractor
// version with a non-failed status drops out of the candidate query on its own.
// Only blobs that STAY candidates after processing (extraction Failed, or the
// metadata row vanished) are recorded here and skipped on later slices, so a
// permanent failure can never block forward progress. A fresh enqueue (no
// checkpoint), or `--failed-only`, re-attempts them.
public sealed record MetadataBackfillCheckpoint
{
    public int V { get; init; } = 1;

    // Cumulative across ALL slices of this one logical job.
    public int ProcessedTotal { get; init; }
    public int CompletedTotal { get; init; }
    public int SkippedTotal { get; init; }
    public int FailedTotal { get; init; }

    // Blob ids excluded from subsequent slices (see class remarks). Bounded.
    public IReadOnlyList<Guid> FailedIds { get; init; } = Array.Empty<Guid>();

    public string Serialize() => JsonSerializer.Serialize(this);

    public static MetadataBackfillCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var checkpoint = JsonSerializer.Deserialize<MetadataBackfillCheckpoint>(json);
            // Unknown / older version → start fresh rather than fail, so an
            // older queued job resumes safely after a code change.
            return checkpoint is { V: 1 } ? checkpoint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

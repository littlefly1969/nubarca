using System.Text.Json;

namespace NubArca.Api.Files;

// Versioned, INTERNAL-ONLY resume state for the sliceable media-derivatives
// backfill. The job engine persists this to BackgroundJob.CheckpointJson
// between slices; it is never returned by any admin DTO. It holds only counts
// plus the set of FileItem ids that failed to fully resolve this run — never
// storage keys, paths, raw metadata, or tokens.
//
// No positional cursor is needed: a successfully-processed item gains its
// FileThumbnail row(s) and therefore drops out of the "missing derivatives"
// query on its own. Only items that FAILED to resolve (decode error,
// ineligible, partial) stay "missing"; recording their ids lets the next slice
// skip them so a permanent failure can never block forward progress
// (guardrail #5). A fresh backfill enqueue starts with no checkpoint and so
// retries them.
public sealed record MediaDerivativesCheckpoint
{
    public int V { get; init; } = 1;

    // images → posters → done. Images first (the gallery grid is the priority).
    public string Phase { get; init; } = MediaDerivativesPhases.Images;

    // Cumulative across ALL slices of this one logical job.
    public int ProcessedTotal { get; init; }
    public int FailedTotal { get; init; }

    // FileItem ids excluded from subsequent slices (see class remarks). Bounded.
    public IReadOnlyList<Guid> FailedIds { get; init; } = Array.Empty<Guid>();

    public string Serialize() => JsonSerializer.Serialize(this);

    public static MediaDerivativesCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var checkpoint = JsonSerializer.Deserialize<MediaDerivativesCheckpoint>(json);
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

public static class MediaDerivativesPhases
{
    public const string Images = "images";
    public const string Posters = "posters";
    public const string Done = "done";
}

using System.Text.Json;

namespace NubArca.Api.Organizer;

// Versioned, INTERNAL-ONLY resume state for the sliceable organizer job. The
// job engine persists this to BackgroundJob.CheckpointJson between slices; it is
// never returned by any DTO. Holds only cumulative COUNTS plus a keyset cursor
// (the last processed FileItem id) so a later slice resumes after it. No names,
// paths, storage keys, sha, or metadata.
//
// Candidates are processed in a stable Id order; LastFileId lets a slice resume
// strictly after the last committed file. Reprocessing is harmless anyway —
// an already-organized file is detected and counted, not re-moved.
public sealed record PhotoOrganizerCheckpoint
{
    public int V { get; init; } = 1;

    public int ProcessedTotal { get; init; }
    public int MovedTotal { get; init; }
    public int AlreadyTotal { get; init; }
    public int SkippedMissingTotal { get; init; }
    public int SkippedConflictTotal { get; init; }
    public int ExactDuplicateRemovedTotal { get; init; }
    public int FailedTotal { get; init; }
    public int FoldersCreatedTotal { get; init; }

    // Keyset cursor: the highest FileItem id fully processed so far (null = none).
    public Guid? LastFileId { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static PhotoOrganizerCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var checkpoint = JsonSerializer.Deserialize<PhotoOrganizerCheckpoint>(json);
            return checkpoint is { V: 1 } ? checkpoint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

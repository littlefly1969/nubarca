using System.Text.Json;

namespace NubArca.Api.PhotoExport;

// Versioned, INTERNAL-ONLY resume state for the sliceable snapshot-build job.
// Persisted to BackgroundJob.CheckpointJson between slices; never returned by a
// DTO. Holds only cumulative counts + a keyset cursor (last FileItem id) so a
// later slice resumes strictly after it. No names, paths, storage keys, or sha.
public sealed record PhotoExportCheckpoint
{
    public int V { get; init; } = 1;

    public int EntriesBuiltTotal { get; init; }
    public long BytesTotal { get; init; }

    // Keyset cursor: the highest FileItem id snapshotted so far (null = none).
    public Guid? LastFileId { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static PhotoExportCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var cp = JsonSerializer.Deserialize<PhotoExportCheckpoint>(json);
            return cp is { V: 1 } ? cp : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

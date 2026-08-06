using System.Text.Json;

namespace NubArca.Api.Ai.Faces;

// Versioned, internal-only resume state for the face backfills. Keyset cursor
// (last processed BlobObjectId) + cumulative counts. Counts/cursor only — never
// storage keys, paths, vectors, or tokens. TryParse tolerates unknown/older
// versions by returning null (start fresh).
public sealed record FaceBackfillCheckpoint(
    int V,
    Guid? CursorBlobId,
    int ProcessedTotal,
    int ProducedTotal,
    int SkippedTotal,
    int FailedTotal)
{
    public const int CurrentVersion = 1;

    public static FaceBackfillCheckpoint Initial => new(CurrentVersion, null, 0, 0, 0, 0);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static FaceBackfillCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FaceBackfillCheckpoint>(json);
            return parsed is null || parsed.V != CurrentVersion ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

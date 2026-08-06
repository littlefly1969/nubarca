using System.Text.Json;

namespace NubArca.Api.Ai.Photos;

// Versioned, internal-only resume state for ai.photos.embeddings.backfill.
// Keyset cursor (last processed BlobObjectId) + cumulative counts. Counts/cursor
// only — never storage keys, paths, vectors, or tokens. TryParse tolerates
// unknown/older versions by returning null (start fresh).
public sealed record PhotoEmbeddingBackfillCheckpoint(
    int V,
    Guid? CursorBlobId,
    int IndexedTotal,
    int SkippedTotal,
    int FailedTotal)
{
    public const int CurrentVersion = 1;

    public static PhotoEmbeddingBackfillCheckpoint Initial => new(CurrentVersion, null, 0, 0, 0);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static PhotoEmbeddingBackfillCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PhotoEmbeddingBackfillCheckpoint>(json);
            return parsed is null || parsed.V != CurrentVersion ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

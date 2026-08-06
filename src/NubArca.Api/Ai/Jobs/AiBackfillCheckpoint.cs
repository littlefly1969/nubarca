using System.Text.Json;

namespace NubArca.Api.Ai.Jobs;

// Versioned, internal-only resume state for AI backfill jobs. Phase 0C handlers
// NO-OP and never continue, so this is not yet written — it establishes the
// Phase 1 structure (cursor by blob id + cumulative count) so the real
// backfills can slice/checkpoint exactly like MetadataBackfillService.
//
// Counts/cursor ids only — never storage keys, paths, raw metadata, or tokens.
// TryParse tolerates unknown/older versions by returning null (start fresh), so
// a queued job resumes safely across code updates.
public sealed record AiBackfillCheckpoint(
    int V,
    Guid? CursorBlobId,
    int Processed)
{
    public const int CurrentVersion = 1;

    public static AiBackfillCheckpoint Initial => new(CurrentVersion, null, 0);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static AiBackfillCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AiBackfillCheckpoint>(json);
            if (parsed is null || parsed.V != CurrentVersion)
            {
                // Unknown/older version: start fresh rather than misread state.
                return null;
            }

            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

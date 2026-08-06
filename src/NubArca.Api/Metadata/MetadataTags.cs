using System.Text.Json;

namespace NubArca.Api.Metadata;

// Serialization + normalization for the curated user tag list. Tags are stored
// as a JSON array string on FileItemUserMetadata.TagsJson and exposed as a
// plain string[] — never a raw metadata bag.
internal static class MetadataTags
{
    public const int MaxTags = 32;
    public const int MaxTagLength = 64;

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    public static IReadOnlyList<string> Deserialize(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(tagsJson);
            return parsed is null ? Empty : parsed;
        }
        catch (JsonException)
        {
            // Defensive: a malformed value never crashes a read.
            return Empty;
        }
    }

    // Trims, drops blanks, dedupes case-insensitively (first wins), caps count
    // and per-tag length. Returns null when nothing remains (so the column is
    // cleared rather than storing "[]"). Throws when a tag is too long.
    public static string? NormalizeToJson(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var tag = raw.Trim();
            if (tag.Length > MaxTagLength)
            {
                throw new ArgumentException(
                    $"Each tag must be {MaxTagLength} characters or fewer.", nameof(tags));
            }
            if (seen.Add(tag))
            {
                normalized.Add(tag);
            }
            if (normalized.Count > MaxTags)
            {
                throw new ArgumentException(
                    $"A file may have at most {MaxTags} tags.", nameof(tags));
            }
        }

        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }
}

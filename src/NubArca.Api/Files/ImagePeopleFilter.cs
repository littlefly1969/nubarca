namespace NubArca.Api.Files;

// Parses the gallery People filter query params (CSV of person ids). Person ids
// are safe owner-private identifiers already used in authenticated People
// routes; a foreign id simply matches nothing in the owner-scoped query, so no
// existence check is needed here. Returns false only on a genuinely malformed
// token or when the count exceeds the cap.
public static class ImagePeopleFilter
{
    public static bool TryParseIds(string? csv, int maxIds, out IReadOnlyList<Guid>? ids)
    {
        ids = null;
        if (string.IsNullOrWhiteSpace(csv))
        {
            return true;
        }

        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > maxIds)
        {
            return false;
        }

        var parsed = new List<Guid>(parts.Length);
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, out var id))
            {
                return false;
            }
            if (!parsed.Contains(id))
            {
                parsed.Add(id);
            }
        }

        ids = parsed.Count == 0 ? null : parsed;
        return true;
    }
}

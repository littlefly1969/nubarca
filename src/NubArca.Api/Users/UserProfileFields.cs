namespace NubArca.Api.Users;

// Validation and normalisation for the optional profile fields, shared by the
// self-service endpoint and the admin editor so both accept exactly the same
// values. Neither surface may invent its own idea of a valid time zone.
public static class UserProfileFields
{
    public const int MaxNameLength = 100;
    public const int MaxDisplayNameLength = 200;

    // Trims, collapses an empty string to null, and rejects anything longer than
    // the column. A caller clearing a name sends "" (or "   "); null means
    // "leave it alone", which is why the two are not the same value here.
    public static bool TryNormalizeOptionalName(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (raw is null)
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxNameLength)
        {
            error = $"Must be {MaxNameLength} characters or fewer.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static bool TryNormalizeDisplayName(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (raw is null)
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            error = "Display name cannot be empty.";
            return false;
        }

        if (trimmed.Length > MaxDisplayNameLength)
        {
            error = $"Must be {MaxDisplayNameLength} characters or fewer.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    // Accepts an IANA identifier the RUNTIME can actually resolve, so the column
    // never holds a zone this server cannot convert with. `TimeZoneInfo` on
    // Linux reads the tz database directly, which is where "Europe/Rome" comes
    // from; the lookup also accepts a Windows id on Windows hosts, and
    // FindSystemTimeZoneById's own canonical id is what gets stored.
    public static bool TryNormalizeTimeZone(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (raw is null)
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > 64)
        {
            error = "Unknown time zone.";
            return false;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            normalized = zone.Id;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = "Unknown time zone.";
            return false;
        }
    }
}

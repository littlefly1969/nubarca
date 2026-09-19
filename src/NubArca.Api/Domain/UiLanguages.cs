namespace NubArca.Api.Domain;

// The closed set of UI languages NubArca ships. Italian is the canonical
// default; the others are what the web interface is fully translated into.
// Arbitrary browser locale strings are never persisted — only these exact codes
// pass Normalize.
//
// This list and `frontend/src/i18n/types.ts` are one decision in two places: the
// server persists an authenticated user's choice, so a code the browser offers
// and the server refuses is a preference that silently reverts on the next
// sign-in.
public static class UiLanguages
{
    public const string Italian = "it";
    public const string English = "en";
    public const string Spanish = "es";
    public const string German = "de";

    // Canonical default when a user has no explicit preference.
    public const string Default = Italian;

    public static readonly IReadOnlyList<string> All =
        new[] { Italian, English, Spanish, German };

    // True when the code is exactly a supported language (case-insensitive,
    // trimmed). Rejects null/empty and any unsupported/locale-extended string
    // (e.g. "en-US", "fr").
    public static bool IsSupported(string? code) => TryNormalize(code, out _);

    // Normalizes a candidate to a supported code, or null when unsupported.
    // Accepts only the bare code (trimmed, lower-cased); does NOT infer from
    // region-tagged locales.
    public static bool TryNormalize(string? code, out string normalized)
    {
        normalized = Default;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var candidate = code.Trim().ToLowerInvariant();
        foreach (var lang in All)
        {
            if (candidate == lang)
            {
                normalized = lang;
                return true;
            }
        }

        return false;
    }
}

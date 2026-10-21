using System.Text;

namespace NubArca.Api.MailArchive;

// Turns names found inside a PST (folder names, attachment file names) into
// names NubArca accepts for its own folders and files. PST names are data
// from a foreign file: they may hold separators, control characters, "..",
// or be absurdly long. Nothing here is a security boundary on its own —
// FileItemService still validates — but a valid name never fails an import.
public static class MailNames
{
    private const int MaxNameLength = 255;

    public static string Segment(string? raw, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var ch in (raw ?? string.Empty).Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(ch) || ch is '/' or '\\')
            {
                builder.Append(ch is '/' or '\\' ? '-' : ' ');
            }
            else
            {
                builder.Append(ch);
            }
        }
        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0 || name is "." or "..")
        {
            name = fallback;
        }
        return Truncate(name);
    }

    public static string AttachmentFileName(string? fileName, string? displayName, string key)
    {
        var candidate = !string.IsNullOrWhiteSpace(fileName) ? fileName : displayName;
        return Segment(candidate, $"allegato-{key}");
    }

    // Shortens the stem, never the extension.
    private static string Truncate(string name)
    {
        if (name.Length <= MaxNameLength)
        {
            return name;
        }
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : string.Empty;
        var stem = name[..(dot > 0 && extension.Length > 0 ? dot : name.Length)];
        var cut = MaxNameLength - extension.Length;
        if (char.IsHighSurrogate(stem[cut - 1]))
        {
            cut--; // never split a surrogate pair
        }
        return stem[..cut] + extension;
    }
}

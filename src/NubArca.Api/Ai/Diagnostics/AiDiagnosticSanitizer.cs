namespace NubArca.Api.Ai.Diagnostics;

// Defensive sanitizer for any free-text that might reach a diagnostic row.
// Collapses whitespace/newlines (so a stack trace can't survive as multiple
// lines), trims, and truncates. The diagnostics writer structurally avoids
// accepting exceptions/payloads in the first place; this is the second line of
// defence for the optional short message.
public static class AiDiagnosticSanitizer
{
    public const int MaxMessageLength = 200;

    public static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        // Collapse all runs of whitespace (incl. newlines/tabs) to single spaces.
        var collapsed = string.Join(' ', message.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        collapsed = collapsed.Trim();
        if (collapsed.Length > MaxMessageLength)
        {
            collapsed = collapsed[..MaxMessageLength];
        }

        return collapsed.Length == 0 ? null : collapsed;
    }
}

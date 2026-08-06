using System.Text;

namespace NubArca.Api.Plates.Alpr;

// Conservative license-plate text normalization. Deliberately does NOT do
// country-specific ambiguous-character substitution (no unconditional O→0 or
// I→1) — that belongs to a future country-aware step. The raw OCR text is kept
// separately on the detection; this only produces the canonical match form.
public static class PlateTextNormalizer
{
    // Trim, uppercase, and keep only alphanumeric characters (dropping spaces and
    // common separators). Returns null for text that normalizes to empty (caller
    // rejects it). Example: " ab 123 cd " → "AB123CD".
    public static string? Normalize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var sb = new StringBuilder(rawText.Length);
        foreach (var ch in rawText)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}

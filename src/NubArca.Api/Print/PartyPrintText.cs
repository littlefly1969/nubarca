namespace NubArca.Api.Print;

using NubArca.Api.Domain.Print;

/// <summary>
/// The bounds on the one line of text a HOST may put on a party print.
///
/// Paper is permanent and public: everyone at the party sees this line on every
/// sheet, and no one can moderate it after the fact. So it is deliberately the
/// host's line and no one else's — guests write nothing on paper — and it is
/// held to a length the renderer's footer band can actually show, with no
/// control characters and no line breaks that would push type out of the band.
/// </summary>
public static class PartyPrintText
{
    /// <summary>
    /// Normalises a host's footer line, or explains why it cannot be used.
    ///
    /// Empty means "no line", which is a valid choice rather than an error, and
    /// comes back as null so the profile stores absence rather than a blank.
    /// </summary>
    public static bool TryNormaliseFooter(string? value, out string? normalised, out string? error)
    {
        normalised = null;
        error = null;
        if (value is null)
        {
            return true;
        }

        // Collapse the whitespace a paste can bring with it: the band is a
        // single line, so a newline is not a layout the renderer can honour.
        var flat = new string(value
            .Select(c => char.IsControl(c) || c == ' ' ? ' ' : c)
            .ToArray());
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }
        flat = flat.Trim();

        if (flat.Length == 0)
        {
            return true;
        }

        if (flat.Length > PartyPrintLimits.FooterMaxLength)
        {
            // Refused, never silently truncated: a host who typed a long line
            // must see what will actually be printed rather than discover the
            // cut on paper.
            error = "footer_too_long";
            return false;
        }

        normalised = flat;
        return true;
    }
}

using System.Globalization;
using System.Text;

namespace NubArca.Api.Domain;

/// <summary>
/// How the host's guest search compares text, as one pure rule the database
/// columns, the writers and the query all share.
///
/// <para>A host typing "nicolo" is looking for Nicolò, and one typing "333 444"
/// or "333444" is looking for +39 333 444. So every searchable row stores a
/// FOLDED copy of what it may be found by — accents stripped, case folded, and a
/// phone also as its digits alone — and the needle is folded the same way. The
/// comparison is then a plain substring test that PostgreSQL and SQLite execute
/// identically, with no extension and no collation doing the folding.</para>
///
/// <para>The folded copy is a CACHE of owner-private text, never shown and never
/// returned. Every write that changes a name, a label, an address or a phone
/// writes it too, and <c>PartySearchTextReconciler</c> re-derives it at startup,
/// so a row written by an application that did not know the column is found
/// again after the next start.</para>
/// </summary>
public static class PartySearchText
{
    /// <summary>What a search may be, in Unicode code points.</summary>
    public const int MaxQueryLength = 120;

    public static string ForGroup(string label, string? email, string? phone) =>
        Join(Fold(label), Fold(email), Fold(phone), Digits(phone));

    public static string ForGuest(string name, string? email, string? phone) =>
        Join(Fold(name), Fold(email), Fold(phone), Digits(phone));

    public static string ForName(string name) => Fold(name);

    /// <summary>
    /// Accents stripped (canonical decomposition, combining marks dropped), case
    /// folded invariantly, control characters read as spaces.
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return folded.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// The needle a query becomes, or null when there is nothing to search for.
    /// <see cref="SearchNeedle.Digits"/> is set only for a query that looks like
    /// a phone number — digits and the usual separators, three digits at least —
    /// so "333 444" also finds a number stored as "333444" and the other way round.
    /// </summary>
    public static SearchNeedle? Needle(string? query)
    {
        var folded = Fold(query?.Trim()).Trim();
        if (folded.Length == 0) return null;
        var phoneLike = folded.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '.' or '(' or ')' or '/');
        var digits = Digits(folded);
        return new SearchNeedle(
            folded,
            phoneLike && digits.Length >= 3 && digits != folded ? digits : null);
    }

    private static string Digits(string? value) =>
        value is null ? string.Empty : new string(value.Where(char.IsAsciiDigit).ToArray());

    // A newline between the parts, so no needle a search field can produce
    // matches ACROSS two of them.
    private static string Join(params string[] parts) =>
        string.Join('\n', parts.Where(p => p.Length > 0));
}

/// <summary>A folded query, and its digits-only form when it looks like a phone number.</summary>
public sealed record SearchNeedle(string Text, string? Digits);

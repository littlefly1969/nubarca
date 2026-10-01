using System.Globalization;
using System.Text;

namespace NubArca.Api.Domain;

// The ONE definition of what a party message's text is, and of how long it is.
//
// Both halves are a CONTRACT shared with the browser, not an implementation
// detail, because the guest sees a live character counter and the server is the
// authority: if the two disagree, a guest watches the counter say 118 and the
// submit fail. `frontend/src/lib/partyMessageText.ts` is the mirror of this
// file, expressed in the JavaScript constructs that match these rules exactly,
// and PartyMessageTextTests pins the shared cases.
//
// LENGTH IS COUNTED IN UNICODE CODE POINTS. Not UTF-16 units, which would
// charge two for every emoji and vary with an implementation detail of the
// storage encoding; and not grapheme clusters, which are the friendliest count
// but are defined by an ICU table that .NET and a browser upgrade on their own
// schedules — the day they disagree, the counter and the validator disagree
// too. Code points are exactly `EnumerateRunes().Count()` here and exactly
// `[...text].length` there, on every runtime, forever. The visible consequence
// is that a heart with a variation selector costs 2 and a family emoji costs 7;
// with a 120 budget for a party greeting that is a price worth paying for two
// numbers that can never drift apart.
public static class PartyMessageText
{
    // A message is plain text on one line. Normalisation is applied BEFORE
    // validation, so the limit is measured against what will actually be
    // stored and displayed — never against whitespace the guest cannot see.
    //
    //   1. Drop format characters (Cf). This is what removes the bidi overrides
    //      (U+202A-U+202E, U+2066-U+2069) that can make stored text render as
    //      something other than what it says, plus zero-width spaces and soft
    //      hyphens used to pad past a limit. ZWJ and ZWNJ are KEPT: they are
    //      what holds an emoji sequence and much Indic/Persian text together,
    //      and dropping them would corrupt real messages.
    //   2. Turn every control character and every whitespace character —
    //      including the line endings, which is how CRLF/CR/LF all become the
    //      same thing — into a plain space.
    //   3. Collapse runs of spaces, then trim.
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (category == UnicodeCategory.Format && !IsJoiner(rune))
            {
                continue;
            }

            if (category == UnicodeCategory.Control || Rune.IsWhiteSpace(rune))
            {
                // Never emitted at the start, and only emitted once a further
                // non-space rune arrives — which collapses runs and trims both
                // ends in a single pass.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    // The same rules for text that keeps its LINE BREAKS — a guest book
    // dedication. It is not a second normaliser: every line goes through
    // Normalize above, so what is security-relevant (the bidi overrides, the
    // zero-width padding, the control characters) still has exactly one
    // implementation. What this adds is only which line endings survive:
    //
    //   1. CRLF and a lone CR become LF, so the stored text has one ending.
    //   2. Each line is normalised on its own: trimmed, its runs collapsed.
    //   3. Empty lines at either END are dropped — the outside of the value is
    //      trimmed — and every line break INSIDE it is kept exactly as written,
    //      blank lines included. How an author spaces their dedication is part
    //      of the dedication; the length limit, not this, bounds it.
    public static string NormalizeMultiline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = Normalize(lines[i]);
        }

        var first = 0;
        while (first < lines.Length && lines[first].Length == 0)
        {
            first++;
        }

        var last = lines.Length - 1;
        while (last >= first && lines[last].Length == 0)
        {
            last--;
        }

        return first > last ? string.Empty : string.Join('\n', lines, first, last - first + 1);
    }

    // Length in Unicode code points — the counted unit for BOTH limits.
    public static int Length(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    // The optional guest signature: normalised, then absent rather than empty.
    // "" and "   " and null all become null, so the TV has exactly one shape to
    // render for "this guest did not sign it".
    //
    // Returns false when the name is present but too long — a name over the
    // limit is a rejected submission, never a silent truncation of what
    // somebody chose to call themselves.
    public static bool TryNormalizeDisplayName(string? value, out string? normalized)
    {
        var text = Normalize(value);
        if (text.Length == 0)
        {
            normalized = null;
            return true;
        }

        if (Length(text) > PartyMessageLimits.MaxDisplayNameLength)
        {
            normalized = null;
            return false;
        }

        normalized = text;
        return true;
    }

    // The message body: normalised, non-empty, within the limit. A body that is
    // empty or whitespace-only after normalisation is rejected — there is no
    // such thing as a blank greeting on a television.
    public static bool TryNormalizeBody(string? value, out string normalized)
    {
        normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        return Length(normalized) <= PartyMessageLimits.MaxBodyLength;
    }

    // U+200C ZERO WIDTH NON-JOINER and U+200D ZERO WIDTH JOINER.
    private static bool IsJoiner(Rune rune) => rune.Value is 0x200C or 0x200D;
}

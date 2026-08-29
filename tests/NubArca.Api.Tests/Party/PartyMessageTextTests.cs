using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Party;

// The text contract a party message is measured by. These are not incidental
// unit tests: the browser renders a live character counter from the SAME rules
// (frontend/src/lib/partyMessageText.ts), so every case here is also a case
// there, and the two agreeing is what stops a guest watching the counter say
// "2 left" and the submit fail.
public sealed class PartyMessageTextTests
{
    // --- counting ---

    [Fact]
    public void Length_Counts_Unicode_Code_Points_Not_Utf16_Units()
    {
        // Plain ASCII: nothing interesting.
        Assert.Equal(5, PartyMessageText.Length("hello"));

        // A single astral code point is ONE, though C# stores it as a surrogate
        // pair. Counting `string.Length` here would say 2 and the browser's
        // `[...s].length` would say 1 — the exact drift this contract removes.
        Assert.Equal(2, "🎉".Length);
        Assert.Equal(1, PartyMessageText.Length("🎉"));

        // A heart with a variation selector is deliberately TWO: the selector is
        // a code point of its own and both runtimes count it.
        Assert.Equal(2, PartyMessageText.Length("❤️"));

        // A ZWJ family sequence is SEVEN — four people and three joiners. The
        // grapheme count would be 1; we do not use grapheme counts, on purpose.
        Assert.Equal(7, PartyMessageText.Length("👨‍👩‍👧‍👦"));

        // Combining marks count separately, for the same reason. Written as
        // escapes so no editor or tool can silently precompose it into the
        // single code point U+00E9 and make the test agree for the wrong reason.
        Assert.Equal(2, PartyMessageText.Length("e\u0301"));
        Assert.Equal(1, PartyMessageText.Length("\u00e9"));
    }

    [Fact]
    public void Body_Of_Exactly_120_Code_Points_Is_Accepted_And_121_Is_Not()
    {
        Assert.True(PartyMessageText.TryNormalizeBody(new string('a', 120), out var ok));
        Assert.Equal(120, PartyMessageText.Length(ok));

        Assert.False(PartyMessageText.TryNormalizeBody(new string('a', 121), out _));
    }

    [Fact]
    public void The_Limit_Is_Measured_In_The_Same_Code_Points_For_Emoji()
    {
        // 120 astral emoji = 120 code points = accepted, though the stored UTF-16
        // string is 240 units long (which is why the column is bounded at 240).
        var sixty = string.Concat(Enumerable.Repeat("🎉", 120));
        Assert.True(PartyMessageText.TryNormalizeBody(sixty, out var accepted));
        Assert.Equal(120, PartyMessageText.Length(accepted));
        Assert.Equal(240, accepted.Length);

        Assert.False(PartyMessageText.TryNormalizeBody(sixty + "🎉", out _));
    }

    [Fact]
    public void The_Limit_Applies_To_The_Normalised_Text_Not_The_Raw_Input()
    {
        // Forty "ab" pairs separated by DOUBLE spaces, wrapped in leading and
        // trailing whitespace: 164 raw characters that normalise to exactly 120.
        // It fits, because a guest is not charged for whitespace they cannot see.
        var padded = "   " + string.Join("  ", Enumerable.Repeat("ab", 40)) + "x\n\n";
        Assert.Equal(164, padded.Length);
        Assert.True(PartyMessageText.TryNormalizeBody(padded, out var normalized));
        Assert.Equal(120, PartyMessageText.Length(normalized));
        Assert.DoesNotContain("  ", normalized);
        Assert.Equal("ab", normalized[..2]);
        Assert.EndsWith("abx", normalized);

        // One more real character and it no longer fits, however much whitespace
        // is around it: the limit is on the normalised text, not on the input.
        Assert.False(PartyMessageText.TryNormalizeBody("  " + padded + " y  ", out _));
    }

    // --- emptiness ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r\n  ")]
    [InlineData("  ")] // non-breaking and em space: still whitespace
    [InlineData("​​")] // zero-width spaces: dropped, so nothing is left
    public void Empty_And_Whitespace_Only_Bodies_Are_Rejected(string? input)
    {
        Assert.False(PartyMessageText.TryNormalizeBody(input, out _));
    }

    // --- normalisation ---

    [Fact]
    public void Line_Endings_All_Become_One_Space_And_Runs_Collapse()
    {
        Assert.True(PartyMessageText.TryNormalizeBody("a\r\nb\rc\nd", out var normalized));
        Assert.Equal("a b c d", normalized);

        Assert.True(PartyMessageText.TryNormalizeBody("  a \t\t b  ", out var collapsed));
        Assert.Equal("a b", collapsed);
    }

    [Fact]
    public void Bidi_Overrides_And_Zero_Width_Padding_Are_Removed()
    {
        // A right-to-left override can make stored text render as something other
        // than what it says. It never reaches the television.
        Assert.True(PartyMessageText.TryNormalizeBody("auguri‮gnorw", out var normalized));
        Assert.Equal("augurignorw", normalized);
        Assert.DoesNotContain('‮', normalized);

        // Isolates, zero-width space and soft hyphen go the same way — all three
        // are ways to pad a message past a limit while looking short.
        Assert.True(PartyMessageText.TryNormalizeBody("a⁦b⁩c​d­e", out var stripped));
        Assert.Equal("abcde", stripped);
    }

    [Fact]
    public void Joiners_Survive_Because_Dropping_Them_Would_Corrupt_Real_Text()
    {
        // ZWJ holds an emoji family together; ZWNJ is meaningful in Persian and
        // several Indic scripts. Both are Format characters, and both are kept.
        Assert.True(PartyMessageText.TryNormalizeBody("👨‍👩‍👧‍👦", out var family));
        Assert.Equal("👨‍👩‍👧‍👦", family);

        Assert.True(PartyMessageText.TryNormalizeBody("می‌روم", out var persian));
        Assert.Equal("می‌روم", persian);
    }

    [Fact]
    public void Emoji_Punctuation_And_Accents_Pass_Through_Untouched()
    {
        const string greeting = "Serata fantastica! Auguri ragazzi ❤️🎉 — «davvero»";
        Assert.True(PartyMessageText.TryNormalizeBody(greeting, out var normalized));
        Assert.Equal(greeting, normalized);
    }

    // --- display name ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("​")]
    public void Absent_Blank_And_Zero_Width_Names_All_Become_Null(string? input)
    {
        Assert.True(PartyMessageText.TryNormalizeDisplayName(input, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void A_Name_Of_40_Is_Accepted_And_41_Is_Refused_Rather_Than_Truncated()
    {
        Assert.True(PartyMessageText.TryNormalizeDisplayName(new string('n', 40), out var ok));
        Assert.Equal(40, PartyMessageText.Length(ok));

        // Refused, not silently shortened: nobody's name gets cut in half by us.
        Assert.False(PartyMessageText.TryNormalizeDisplayName(new string('n', 41), out var overflow));
        Assert.Null(overflow);
    }

    [Fact]
    public void A_Name_Is_Normalised_Like_A_Body()
    {
        Assert.True(PartyMessageText.TryNormalizeDisplayName("  Giulia\tRossi \n", out var name));
        Assert.Equal("Giulia Rossi", name);
    }
}

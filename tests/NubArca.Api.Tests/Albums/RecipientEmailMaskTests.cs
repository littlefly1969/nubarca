using NubArca.Api.Albums.Sharing;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// The masked hint that lets an album owner tell two members with the SAME
// display name apart. Its whole value is being enough to disambiguate and no
// more, so both halves of that are pinned here.
public class RecipientEmailMaskTests
{
    [Theory]
    // Ordinary addresses: first and last character of the local part survive.
    [InlineData("mario.rossi@nubarca.local", "m•••i@nubarca.local")]
    [InlineData("bruno@example.com", "b•••o@example.com")]
    [InlineData("anna@example.com", "a•••a@example.com")]
    // Short local parts show LESS, not more: keeping first AND last of a
    // three-character local would effectively spell it out.
    [InlineData("bob@example.com", "b••@example.com")]
    [InlineData("jo@example.com", "••@example.com")]
    [InlineData("a@example.com", "••@example.com")]
    // Sub-addressing and dotted domains are not special-cased; only the local
    // part is masked, and the LAST '@' separates them.
    [InlineData("mario+albums@nubarca.local", "m•••s@nubarca.local")]
    [InlineData("a.b.c@mail.example.co.uk", "a•••c@mail.example.co.uk")]
    public void Masks_The_Local_Part_And_Keeps_The_Domain(string email, string expected)
    {
        Assert.Equal(expected, RecipientEmailMask.Mask(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("trailing@")]
    public void Unusable_Input_Degrades_To_No_Hint_Rather_Than_Leaking_Itself(string? email)
    {
        Assert.Equal(string.Empty, RecipientEmailMask.Mask(email));
    }

    [Fact]
    public void The_Mask_Never_Contains_The_Full_Local_Part()
    {
        // The property that matters: whatever the input, the output must not be
        // the address itself.
        foreach (var email in new[]
                 {
                     "mario.rossi@nubarca.local", "bob@example.com", "jo@example.com",
                     "averyverylonglocalpart@example.com",
                 })
        {
            var masked = RecipientEmailMask.Mask(email);
            Assert.NotEqual(email, masked);
            Assert.Contains('•', masked);
            Assert.EndsWith(email[email.LastIndexOf('@')..], masked, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Two_Distinct_Members_Of_A_Realistic_Album_Stay_Distinguishable()
    {
        // The exact scenario the hint exists for: same display name, different
        // accounts. If these collided the feature would not do its job.
        var first = RecipientEmailMask.Mask("mario.rossi@nubarca.local");
        var second = RecipientEmailMask.Mask("m.rossi@example.com");
        Assert.NotEqual(first, second);
    }
}

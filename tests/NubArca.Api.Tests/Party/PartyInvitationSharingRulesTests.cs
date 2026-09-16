using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The pure rules the share and the guest search are made of: which numbers
/// WhatsApp may open a chat with directly, how the click-to-chat link and the
/// message are written, and how text is folded for the host's search.
/// </summary>
public sealed class PartyInvitationSharingRulesTests
{
    [Theory]
    [InlineData("+39 333 123 4567", "393331234567")]
    [InlineData("+393331234567", "393331234567")]
    [InlineData("  +39 333 123 4567  ", "393331234567")]
    [InlineData("0039 333 123 4567", "393331234567")]
    [InlineData("+39.333.123.4567", "393331234567")]
    [InlineData("+39-333-123-4567", "393331234567")]
    [InlineData("+39/333/1234567", "393331234567")]
    [InlineData("+1 (415) 555-2671", "14155552671")]
    [InlineData("+44 20 7946 0958", "442079460958")]
    public void An_international_number_opens_its_chat_directly(string phone, string expected)
    {
        Assert.Equal(expected, PartyWhatsAppLink.InternationalNumber(phone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // National: which country is a guess, and a wrong guess is a stranger's chat.
    [InlineData("333 123 4567")]
    [InlineData("06 1234 5678")]
    // "(0)" means "drop this 0 abroad" in some countries and not in others.
    [InlineData("+44 (0)20 7946 0958")]
    [InlineData("+39 333 12a 4567")]
    [InlineData("+39 333 1234567 int. 12")]
    [InlineData("++39 333 1234567")]
    [InlineData("39+333 1234567")]
    [InlineData("+0 123 456 789")]
    [InlineData("+39 12")]
    [InlineData("+1234567890123456")]
    public void Anything_uncertain_is_left_to_WhatsApp_to_ask(string? phone)
    {
        Assert.Null(PartyWhatsAppLink.InternationalNumber(phone));
    }

    [Fact]
    public void The_click_to_chat_link_carries_the_message_encoded()
    {
        const string text = "Sei invitato a \"A & B\" 🎉\n\nhttps://cloud.example.com/party/invite/abc?x=1";

        var direct = PartyWhatsAppLink.Build("+39 333 123 4567", text);
        var chooser = PartyWhatsAppLink.Build(null, text);

        Assert.Equal("https://wa.me/393331234567?text=" + Uri.EscapeDataString(text), direct);
        Assert.Equal("https://wa.me/?text=" + Uri.EscapeDataString(text), chooser);
        Assert.Equal(text, Uri.UnescapeDataString(chooser["https://wa.me/?text=".Length..]));
        Assert.DoesNotContain(" ", direct);
        Assert.DoesNotContain("\n", direct);
    }

    [Fact]
    public void The_message_is_short_names_the_party_and_carries_one_link()
    {
        const string url = "https://cloud.example.com/party/invite/TOKEN";

        var italian = PartyInvitationShareText.Compose("it", "Compleanno\ndi Anna", url);
        var english = PartyInvitationShareText.Compose("en", "Anna's birthday", url);

        Assert.Equal(
            "Sei invitato a \"Compleanno di Anna\" 🎉\n\nApri il tuo invito personale:\n" + url
            + "\n\nDa qui puoi confermare la partecipazione.",
            italian);
        Assert.StartsWith("You're invited to \"Anna's birthday\"", english);
        Assert.Contains(url, english);
        // An unknown or empty language is the product's own language.
        Assert.Equal(italian, PartyInvitationShareText.Compose(string.Empty, "Compleanno di Anna", url));
    }

    [Fact]
    public void Search_text_folds_accents_and_case_and_keeps_a_phone_as_digits()
    {
        Assert.Equal("nicolo", PartySearchText.Fold("Nicolò"));
        Assert.Equal("zoe muller", PartySearchText.Fold("ZOË MÜLLER"));
        Assert.Equal(
            "famiglia rossi\nrossi@example.com\n+39 333 444 5555\n393334445555",
            PartySearchText.ForGroup("Famiglia Rossi", "Rossi@Example.com", "+39 333 444 5555"));
        Assert.Equal("mario", PartySearchText.ForGuest("Mario", null, null));
        Assert.Equal("anna", PartySearchText.ForName("  Anna  ".Trim()));
    }

    [Fact]
    public void A_needle_is_folded_and_a_phone_like_one_also_searches_its_digits()
    {
        Assert.Null(PartySearchText.Needle(null));
        Assert.Null(PartySearchText.Needle("   "));
        Assert.Equal(new SearchNeedle("nicolo", null), PartySearchText.Needle("  NICOLÒ "));
        Assert.Equal(new SearchNeedle("333 444", "333444"), PartySearchText.Needle("333 444"));
        Assert.Equal(new SearchNeedle("+39 (333)", "39333"), PartySearchText.Needle("+39 (333)"));
        // Already digits: nothing to add. Too few digits: not a phone.
        Assert.Equal(new SearchNeedle("333444", null), PartySearchText.Needle("333444"));
        Assert.Equal(new SearchNeedle("1 2", null), PartySearchText.Needle("1 2"));
        Assert.Equal(new SearchNeedle("rossi 3", null), PartySearchText.Needle("Rossi 3"));
    }

    [Fact]
    public void A_delivery_invites_only_when_it_was_sent_or_shared_and_was_not_a_reminder()
    {
        Assert.True(PartyInvitationDeliveryStatuses.IsInvitation("initial", "sent"));
        Assert.True(PartyInvitationDeliveryStatuses.IsInvitation("resend", "shared"));
        Assert.False(PartyInvitationDeliveryStatuses.IsInvitation("reminder", "sent"));
        Assert.False(PartyInvitationDeliveryStatuses.IsInvitation("initial", "pending"));
        Assert.False(PartyInvitationDeliveryStatuses.IsInvitation("initial", "failed"));
    }

    [Fact]
    public void The_state_of_a_link_says_email_sent_before_shared_and_describes_its_latest_delivery()
    {
        var capability = Guid.NewGuid();
        var at = new DateTime(2027, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        PartyInvitationDelivery Row(string channel, string kind, string status, int minutes, Guid? generation = null) => new()
        {
            Id = Guid.NewGuid(), CapabilityId = generation ?? capability, Channel = channel, Kind = kind,
            Status = status, CreatedAt = at.AddMinutes(minutes),
            CompletedAt = status == PartyInvitationDeliveryStatuses.Pending ? null : at.AddMinutes(minutes),
        };

        var none = PartyInvitationService.DeliveryState([], capability, out var invitedByNone);
        Assert.Equal("not_sent", none.State);
        Assert.False(invitedByNone);

        var shared = PartyInvitationService.DeliveryState(
            [Row("whatsapp", "initial", "shared", 1)], capability, out var invitedByShare);
        Assert.Equal("shared", shared.State);
        Assert.True(invitedByShare);
        Assert.Equal("whatsapp", shared.LastAttemptChannel);
        Assert.Null(shared.LastSentAt);

        var both = PartyInvitationService.DeliveryState(
            [Row("email", "initial", "sent", 1), Row("copy", "resend", "shared", 2), Row("email", "resend", "failed", 3)],
            capability, out _);
        Assert.Equal("sent", both.State);
        Assert.Equal("email", both.LastAttemptChannel);
        Assert.Equal("failed", both.LastAttemptStatus);

        // A rotated link's deliveries invited nobody.
        var rotated = PartyInvitationService.DeliveryState(
            [Row("whatsapp", "initial", "shared", 1, Guid.NewGuid())], capability, out var invitedBeforeRotation);
        Assert.Equal("not_sent", rotated.State);
        Assert.False(invitedBeforeRotation);
        Assert.Null(rotated.LastAttemptChannel);
    }
}

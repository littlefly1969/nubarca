using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The guest list's pure rules: what an address is, what a question may be,
/// what an answer to it may be, how the personal link is made, and what the
/// email says. No database and no HTTP — these are the functions the services
/// and the endpoints all ask.
/// </summary>
public sealed class PartyInvitationDomainTests
{
    [Theory]
    [InlineData("mario@example.com", true)]
    [InlineData("mario.rossi+festa@posta.example.it", true)]
    [InlineData("mario", false)]
    [InlineData("mario@localhost", false)]
    [InlineData("Mario <mario@example.com>", false)]
    [InlineData("mario @example.com", false)]
    [InlineData("mario@@example.com", false)]
    [InlineData("", false)]
    public void An_address_is_one_mailbox_in_plain_form(string value, bool valid) =>
        Assert.Equal(valid, PartyInvitationLimits.IsValidEmail(PartyInvitationLimits.Normalize(value)));

    [Fact]
    public void Limits_are_counted_in_code_points()
    {
        var hundredTwentyEmoji = string.Concat(Enumerable.Repeat("🎉", 120));
        Assert.True(PartyInvitationLimits.Fits(hundredTwentyEmoji, PartyInvitationLimits.MaxLabelLength));
        Assert.False(PartyInvitationLimits.Fits(hundredTwentyEmoji + "!", PartyInvitationLimits.MaxLabelLength));
    }

    // --- Questions --------------------------------------------------------------

    [Fact]
    public void A_single_choice_keeps_its_options_trimmed_and_in_order()
    {
        var options = PartyRsvpQuestionRules.NormalizeOptions(
            PartyRsvpQuestionKinds.SingleChoice, [" Carne ", "Pesce", "Vegetariano"]);
        Assert.Equal(["Carne", "Pesce", "Vegetariano"], options);
    }

    [Fact]
    public void A_single_choice_refuses_options_nobody_could_tell_apart_or_answer()
    {
        string?[][] refused =
        [
            ["Carne"],                                         // not a choice
            ["Carne", "carne"],                                // the same option twice
            ["Carne", "  "],                                   // an empty one
            ["Carne", new string('x', 121)],                   // too long
            Enumerable.Range(0, 21).Select(i => (string?)$"Opzione {i}").ToArray(),
        ];
        foreach (var options in refused)
        {
            Assert.Null(PartyRsvpQuestionRules.NormalizeOptions(PartyRsvpQuestionKinds.SingleChoice, options));
        }
        Assert.Null(PartyRsvpQuestionRules.NormalizeOptions(PartyRsvpQuestionKinds.SingleChoice, null));
    }

    [Fact]
    public void Only_a_single_choice_carries_options()
    {
        Assert.Null(PartyRsvpQuestionRules.NormalizeOptions(PartyRsvpQuestionKinds.YesNo, ["Sì", "No"]));
        Assert.Empty(PartyRsvpQuestionRules.NormalizeOptions(PartyRsvpQuestionKinds.YesNo, null)!);
        Assert.Empty(PartyRsvpQuestionRules.NormalizeOptions(PartyRsvpQuestionKinds.ShortText, [])!);
        Assert.Null(PartyRsvpQuestionRules.SerializeOptions(PartyRsvpQuestionKinds.ShortText, []));
    }

    [Fact]
    public void The_vocabularies_are_closed()
    {
        Assert.False(PartyRsvpStatuses.IsKnown("maybe"));
        Assert.False(PartyRsvpStatuses.IsKnown("Attending"));
        Assert.True(PartyRsvpStatuses.IsKnown("declined"));
        Assert.False(PartyRsvpQuestionKinds.IsKnown("multiple_choice"));
        Assert.False(PartyRsvpQuestionKinds.IsKnown(null));
    }

    // --- Answers ----------------------------------------------------------------

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static readonly IReadOnlyList<string> Menu = ["Carne", "Pesce"];

    [Fact]
    public void A_short_text_is_trimmed_bounded_and_blank_means_unanswered()
    {
        Assert.True(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.ShortText, [], Json("\"  in treno  \""), out var text));
        Assert.Equal("\"in treno\"", text);

        Assert.True(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.ShortText, [], Json("\"   \""), out var blank));
        Assert.Null(blank);

        Assert.False(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.ShortText, [], Json($"\"{new string('x', 501)}\""), out _));
    }

    [Fact]
    public void A_single_choice_answer_is_exactly_one_of_its_options()
    {
        Assert.True(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.SingleChoice, Menu, Json("\"Pesce\""), out var choice));
        Assert.Equal("\"Pesce\"", choice);
        Assert.False(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.SingleChoice, Menu, Json("\"pesce\""), out _));
        Assert.False(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.SingleChoice, Menu, Json("\"Pollo\""), out _));
    }

    [Fact]
    public void A_yes_no_answer_is_a_boolean()
    {
        Assert.True(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.YesNo, [], Json("false"), out var no));
        Assert.Equal("false", no);
        Assert.False(PartyRsvpQuestionRules.TryCanonicalAnswer(
            PartyRsvpQuestionKinds.YesNo, [], Json("\"true\""), out _));
    }

    [Theory]
    [InlineData(PartyRsvpQuestionKinds.ShortText, "42")]
    [InlineData(PartyRsvpQuestionKinds.ShortText, "true")]
    [InlineData(PartyRsvpQuestionKinds.SingleChoice, "[\"Carne\"]")]
    [InlineData(PartyRsvpQuestionKinds.SingleChoice, "{\"value\":\"Carne\"}")]
    [InlineData(PartyRsvpQuestionKinds.YesNo, "1")]
    [InlineData(PartyRsvpQuestionKinds.YesNo, "null")]
    public void Any_other_json_is_refused(string kind, string raw) =>
        Assert.False(PartyRsvpQuestionRules.TryCanonicalAnswer(kind, Menu, Json(raw), out _));

    // --- The personal link ------------------------------------------------------

    private static PartyInvitationTokens Tokens(string? secret = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(secret is null
                ? []
                : new Dictionary<string, string?> { ["Party:TokenSecret"] = secret })
            .Build());

    [Fact]
    public void The_token_is_derived_from_the_generation_and_bound_to_its_purpose()
    {
        var tokens = Tokens("test-secret");
        var generation = Guid.NewGuid();
        var raw = tokens.Derive(generation);

        // Deterministic, so the owner surface can put it in an email; 256 bits,
        // URL-safe, and a different generation is a different token.
        Assert.Equal(raw, tokens.Derive(generation));
        Assert.Equal(43, raw.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", raw);
        Assert.NotEqual(raw, tokens.Derive(Guid.NewGuid()));

        // The documented construction exactly — and therefore NOT the party
        // link's, which is the same HMAC without the purpose.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("test-secret"));
        string Url(byte[] mac) => Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(
            Url(hmac.ComputeHash([.. generation.ToByteArray(), .. Encoding.UTF8.GetBytes("invitation-rsvp")])),
            raw);
        Assert.NotEqual(Url(hmac.ComputeHash(generation.ToByteArray())), raw);

        // Keyed by the installation's secret.
        Assert.NotEqual(raw, Tokens("another-secret").Derive(generation));
    }

    [Fact]
    public void Only_the_hash_is_a_database_value()
    {
        var (generation, hash) = Tokens().Mint();
        var raw = Tokens().Derive(generation);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(PartyInvitationTokens.Hash(raw), hash);
        Assert.DoesNotContain(raw, hash);
        Assert.Equal($"/party/invite/{raw}", PartyInvitationTokens.InvitationPath(raw));
    }

    // --- The email --------------------------------------------------------------

    private const string Url = "https://cloud.example.com/party/invite/abc";

    [Fact]
    public void The_invitation_says_who_what_where_and_that_no_account_is_needed()
    {
        var message = PartyInvitationEmail.Compose(
            "it", "mario@example.com", "Mario e Laura", "Matrimonio di Marta",
            new DateTime(2027, 6, 12, 12, 0, 0, DateTimeKind.Utc), Url, reminder: false);

        Assert.Equal("mario@example.com", message.ToAddress);
        Assert.Equal("Sei invitato: Matrimonio di Marta", message.Subject);
        Assert.Contains("Ciao Mario e Laura", message.TextBody);
        Assert.Contains("Matrimonio di Marta", message.TextBody);
        Assert.Contains("giugno 2027", message.TextBody);
        Assert.Contains(Url, message.TextBody);
        Assert.Contains("Non serve un account NubArca", message.TextBody);
        // Plain text, one link, nothing that phones home.
        Assert.Null(message.HtmlBody);
        Assert.Single(message.TextBody.Split("http", StringSplitOptions.None).Skip(1));
        Assert.DoesNotContain("<img", message.TextBody);
    }

    [Fact]
    public void A_reminder_says_so_and_the_host_language_decides_the_words()
    {
        var reminder = PartyInvitationEmail.Compose(
            "it", "a@example.com", "Sara", "Festa", null, Url, reminder: true);
        Assert.StartsWith("Promemoria", reminder.Subject);
        Assert.Contains("non abbiamo ancora ricevuto la tua risposta", reminder.TextBody);

        var english = PartyInvitationEmail.Compose(
            "en", "a@example.com", "Sara", "Party", null, Url, reminder: false);
        Assert.Equal("You're invited: Party", english.Subject);
        Assert.Contains("No NubArca account is needed", english.TextBody);
    }

    [Fact]
    public void A_title_with_a_line_break_still_makes_a_one_line_subject()
    {
        var message = PartyInvitationEmail.Compose(
            "it", "a@example.com", "Sara\r\nBcc: x@example.com", "Festa\ndi Marta", null, Url, reminder: false);
        Assert.DoesNotContain('\n', message.Subject);
        Assert.DoesNotContain('\r', message.Subject);
        Assert.DoesNotContain('\n', message.ToName);
        Assert.Equal("Sei invitato: Festa di Marta", message.Subject);
    }
}

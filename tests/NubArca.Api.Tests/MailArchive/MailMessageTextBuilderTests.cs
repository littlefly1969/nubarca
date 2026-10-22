using NubArca.Api.Domain.Mail;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Tests.MailArchive;

public class MailMessageTextBuilderTests
{
    private static PstMessageDocument Document(string? text, string? html, IReadOnlyList<PstAttachmentDescriptor>? attachments = null) =>
        System.Text.Json.JsonSerializer.Deserialize<PstMessageDocument>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                format = MailDocumentFormats.Message, version = 1, subject = "Oggetto",
                body = new { text, html, rtf = (string?)null },
                attachments,
            }, PstProtocol.Json), PstProtocol.Json)!;

    [Fact]
    public void Plain_text_wins_and_is_lower_cased_and_collapsed()
    {
        var text = MailMessageTextBuilder.Build(Document("Ciao\r\n\tBOB,  a   presto", "<p>ignorato</p>"));
        Assert.Equal("ciao bob, a presto", text);
    }

    [Fact]
    public void An_html_only_body_is_indexed_by_its_visible_text()
    {
        var text = MailMessageTextBuilder.Build(Document(null,
            "<html><head><title>t</title><style>p{color:red}</style></head><body><script>rubare()</script>"
            + "<p>Fattura&nbsp;n.&#32;42</p><div>Scadenza<br>marzo</div></body></html>"));
        Assert.Contains("fattura", text);
        Assert.Contains("42", text);
        Assert.Contains("scadenza marzo", text);
        Assert.DoesNotContain("rubare", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void The_text_is_capped_without_splitting_a_surrogate_pair()
    {
        var text = MailMessageTextBuilder.Build(Document(new string('a', MailMessageText.MaxChars - 2) + "😀😀😀", null));
        Assert.True(text.Length < MailMessageText.MaxChars);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    [Theory]
    [InlineData("fattura 2024", new[] { "fattura", "2024" })]
    [InlineData("l'acqua & ( ) :* !", new[] { "acqua" })]
    [InlineData("perché PERCHÉ", new[] { "perché" })]
    [InlineData("a b", new string[0])]
    public void Search_words_are_letters_and_digits_only(string term, string[] expected)
    {
        Assert.Equal(expected, MailMessageQueryService.Words(term));
    }
}

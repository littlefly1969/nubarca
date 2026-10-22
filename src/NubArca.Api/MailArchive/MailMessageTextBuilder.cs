using System.Text;
using NubArca.Api.Domain.Mail;

namespace NubArca.Api.MailArchive;

// Builds a message's searchable text (MailMessageText) from its document:
// the body's plain text, or the text of its HTML body when it has none, then
// the subject and body of each embedded message, depth-first. RTF-only bodies
// are not converted (their subject and addresses stay searchable).
public static class MailMessageTextBuilder
{
    private const int MaxDepth = 8;

    public static string Build(PstMessageDocument document)
    {
        var builder = new StringBuilder();
        Append(builder, BodyText(document.Body));
        Walk(document, builder, depth: 0);
        return Normalize(builder.ToString());
    }

    private static void Walk(PstMessageDocument document, StringBuilder builder, int depth)
    {
        if (depth > MaxDepth || document.Attachments is null)
        {
            return;
        }
        foreach (var attachment in document.Attachments)
        {
            if (builder.Length >= MailMessageText.MaxChars)
            {
                return;
            }
            if (attachment.EmbeddedMessage is { } embedded)
            {
                Append(builder, embedded.Subject);
                Append(builder, BodyText(embedded.Body));
                Walk(embedded, builder, depth + 1);
            }
        }
    }

    private static string? BodyText(PstBody? body)
    {
        if (!string.IsNullOrWhiteSpace(body?.Text))
        {
            return body.Text;
        }
        if (string.IsNullOrWhiteSpace(body?.Html))
        {
            return null;
        }
        return MailHtmlSanitizer.ToText(body.Html);
    }

    private static void Append(StringBuilder builder, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && builder.Length < MailMessageText.MaxChars)
        {
            builder.Append(text).Append(' ');
        }
    }

    // Lower-cased (the SQLite fallback is a plain substring match), control
    // characters and runs of whitespace collapsed to one space, capped.
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MailMessageText.MaxChars));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
            {
                builder.Append(' ');
                space = false;
            }
            if (builder.Length >= MailMessageText.MaxChars - 1)
            {
                break;
            }
            builder.Append(char.ToLowerInvariant(ch));
        }
        // Never end on half of a surrogate pair.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }
        return builder.ToString();
    }
}

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace NubArca.Api.Auth.Recovery;

// Generic SMTP delivery. One connection per message: an installation sends a
// handful of recovery emails a month, so a pooled client would be complexity
// bought for nothing and a long-lived connection to somebody's relay is one
// more thing to keep alive.
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<MailOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptionsMonitor<MailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.Enabled
        && !string.IsNullOrWhiteSpace(_options.CurrentValue.Smtp.Host)
        && !string.IsNullOrWhiteSpace(_options.CurrentValue.FromAddress);

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!IsEnabled)
        {
            return false;
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = message.Subject;

        if (message.HtmlBody is null)
        {
            mime.Body = new TextPart(TextFormat.Plain) { Text = message.TextBody };
        }
        else
        {
            mime.Body = new MultipartAlternative
            {
                new TextPart(TextFormat.Plain) { Text = message.TextBody },
                new TextPart(TextFormat.Html) { Text = message.HtmlBody },
            };
        }

        try
        {
            using var client = new SmtpClient();
            // Implicit TLS is the 465 convention; 587 (and anything else)
            // negotiates STARTTLS. `SecureSocketOptions.StartTls` REQUIRES the
            // upgrade rather than accepting a silent downgrade to plaintext.
            var socketOptions = options.Smtp.UseTls
                ? (options.Smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
                : SecureSocketOptions.None;

            await client.ConnectAsync(options.Smtp.Host, options.Smtp.Port, socketOptions, cancellationToken);
            if (!string.IsNullOrEmpty(options.Smtp.Username))
            {
                await client.AuthenticateAsync(options.Smtp.Username, options.Smtp.Password, cancellationToken);
            }
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            // The exception carries the server's reply, which can name the
            // recipient — so this logs the FAILURE and the host, never the
            // message, the address or anything derived from the body.
            _logger.LogWarning(
                ex,
                "SMTP delivery to {SmtpHost}:{SmtpPort} failed.",
                options.Smtp.Host,
                options.Smtp.Port);
            return false;
        }
    }
}

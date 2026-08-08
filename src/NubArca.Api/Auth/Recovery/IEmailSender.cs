namespace NubArca.Api.Auth.Recovery;

// One outbound message. Plain text is mandatory and Html is optional, so a
// client that refuses HTML still gets a complete, readable message.
public sealed record EmailMessage(
    string ToAddress,
    string ToName,
    string Subject,
    string TextBody,
    string? HtmlBody = null);

// The product's whole outbound-mail surface. Deliberately generic: no provider
// SDK, no API keys, nothing that ties a self-hosted installation to somebody
// else's service. Tests substitute a recording implementation, so no automated
// test ever opens a socket.
public interface IEmailSender
{
    bool IsEnabled { get; }

    // Returns false when the message could not be handed to the SMTP server.
    // The password-recovery flow treats that exactly like every other outcome —
    // the public response never changes — but it is logged (without the token)
    // so an operator can see that delivery is failing.
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

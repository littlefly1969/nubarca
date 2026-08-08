using System.Collections.Concurrent;
using NubArca.Api.Auth.Recovery;

namespace NubArca.Api.Tests.Auth;

// The email sender used by every automated test. No socket is ever opened: an
// SMTP dependency in the test suite would make password-recovery coverage
// depend on a mail server being reachable, which is exactly the kind of test
// that passes locally and fails in CI for reasons unrelated to the product.
//
// It records what WOULD have been sent, so a test can pull the reset link out
// of the message body the same way a user pulls it out of their inbox.
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    // Mirrors the production sender's own gate. Tests that need recovery
    // disabled flip this instead of unsetting configuration piecemeal.
    public bool Enabled { get; set; } = true;

    // When true, SendAsync reports failure — the case where SMTP is configured
    // but the server refuses. The public request response must not change.
    public bool FailDelivery { get; set; }

    public bool IsEnabled => Enabled;

    public IReadOnlyList<EmailMessage> Messages => _messages.ToArray();

    public EmailMessage? Last => _messages.LastOrDefault();

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(message);
        return Task.FromResult(!FailDelivery);
    }

    public void Reset()
    {
        _messages.Clear();
        Enabled = true;
        FailDelivery = false;
    }

    // Extracts the raw token out of the recorded message exactly as a recipient
    // would: from the `#token=` fragment of the only link in the body.
    public static string ExtractToken(EmailMessage message)
    {
        const string marker = "#token=";
        var index = message.TextBody.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("The message contains no reset link.");
        }
        var start = index + marker.Length;
        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end]))
        {
            end++;
        }
        return Uri.UnescapeDataString(message.TextBody[start..end]);
    }
}

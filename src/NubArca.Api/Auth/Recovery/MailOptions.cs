namespace NubArca.Api.Auth.Recovery;

// Operator-owned SMTP configuration. Every value arrives from the environment
// (`Mail__*` in the compose stack, sourced from `.env`); nothing here has a
// credential baked in, and the repository never carries one.
public sealed class MailOptions
{
    public const string SectionName = "Mail";

    // The master switch. With it false, authentication is completely unaffected
    // — only the forgot-password flow reports itself unavailable, and the
    // administrator's manual password reset stays the recovery path.
    public bool Enabled { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "NubArca";

    // The externally reachable origin the reset link is built on, supplied by
    // the operator (NUBARCA_PUBLIC_ORIGIN). Deliberately NOT derived from the
    // request's Host header: an attacker who can set Host would otherwise mint
    // a reset link pointing at their own server and have the product mail it.
    public string PublicOrigin { get; set; } = string.Empty;

    // Short by design. A recovery link is a bearer credential sitting in a
    // mailbox; thirty minutes is long enough to read an email and short enough
    // that a leaked one is usually already dead.
    public int TokenLifetimeMinutes { get; set; } = 30;

    // Per normalized email address, on top of the per-IP limiter the endpoint
    // carries. Conservative values suited to a self-hosted installation: one
    // person asking twice is fine, a script walking an address list is not.
    public int PerEmailPermitLimit { get; set; } = 3;
    public int PerEmailWindowMinutes { get; set; } = 15;

    // True only when every value the flow actually needs is present. A half
    // configured mailer must present as "recovery unavailable", never as a
    // request that silently succeeds and delivers nothing.
    public bool IsRecoveryConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(Smtp.Host)
        && Smtp.Port > 0
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin)
        && (origin.Scheme == Uri.UriSchemeHttps || origin.Scheme == Uri.UriSchemeHttp);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // STARTTLS on the submission port (587), or implicit TLS on 465, which is
    // chosen from the port. False means "plaintext", which is only ever
    // reasonable for a relay on the same host.
    public bool UseTls { get; set; } = true;
}

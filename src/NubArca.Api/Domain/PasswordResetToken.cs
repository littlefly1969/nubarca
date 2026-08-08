namespace NubArca.Api.Domain;

// A single-use, short-lived capability to set a new password without knowing the
// current one.
//
// The RAW token exists only twice: in the bytes handed to the email, and in the
// body of the reset request that consumes it. What is stored here is a SHA-256
// digest of it, so a database copy (a backup, a dump, a stolen volume) grants
// nobody a password reset. The same reason keeps the raw value out of every log
// line and out of the reset URL's path and query — see PasswordResetLinkBuilder,
// which puts it in the fragment, where a reverse proxy never records it.
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // Lowercase hex SHA-256 of the raw token. Indexed, because the reset request
    // arrives with a token and nothing else to look the row up by.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // Set the moment the token is spent. A used row is kept rather than deleted
    // so a replay is answered by the same generic rejection as an expired one
    // instead of by a "no such token" that distinguishes the two.
    public DateTime? UsedAt { get; set; }
}

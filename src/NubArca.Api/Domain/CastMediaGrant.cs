namespace NubArca.Api.Domain;

// NUBARCA-GOOGLE-CAST-01: a short-lived, single-video playback capability
// delegated to an external receiver.
//
// A Google Cast receiver runs the Default Media Receiver on the television. It
// has no NubArca cookie and cannot get one, so the only way it can fetch bytes
// is a URL that carries its own authorization. This row IS that authorization,
// and it is deliberately the narrowest thing that can play one video: one user,
// one FileItem, one expiry, revocable at any moment.
//
// SECURITY: only the SHA-256 hex digest of the secret is stored. The raw secret
// is returned once, from the creating request, and lives only in the sender
// browser's memory — never in storage, never in a log, never in an audit
// payload. `Id` is what a request presents in the path, so the row is found by
// primary key and the digest is then compared in constant time; a token alone
// addresses nothing.
//
// A grant is NOT a standing authorization. Every media request re-establishes
// that the owner is still active, still holds `cast.access`, and still owns the
// file — so a revoked permission stops the next segment rather than the next
// session. Deliberately absent: any copy of the user's `SecurityVersion`. A
// password change signs other BROWSERS out; it does not retract a capability
// the user knowingly handed to a television in their own home. Disabling the
// account and removing the permission both do, immediately.
public class CastMediaGrant
{
    public Guid Id { get; set; }

    // The user on whose behalf the receiver plays. Every request re-reads this
    // account's current state; the grant never speaks for anybody else.
    public Guid UserId { get; set; }

    // The single video this grant can reach. A grant is never a media browser:
    // no other file id is addressable through it.
    public Guid FileItemId { get; set; }

    // SHA-256 hex of the raw secret. Unique. Never exposed, never logged.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // Set by an explicit "stop casting", by loading a replacement item, and
    // best-effort on an unexpected receiver disconnect. Expiry remains the
    // safety net for the case where revocation never runs.
    public DateTime? RevokedAt { get; set; }
}

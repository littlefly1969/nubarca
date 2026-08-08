namespace NubArca.Api.Auth.Recovery;

// Why a reset attempt ended the way it did. The REQUEST side deliberately has
// no such enum on the wire — every request outcome is the same 202 — but the
// service still distinguishes them so an operator's logs can say whether mail
// is failing without the browser ever learning it.
public enum PasswordResetResult
{
    Ok,
    // One rejection for expired, already used, unknown and malformed. The
    // endpoint must not turn four causes into four different messages: "this
    // token existed once" is itself information.
    InvalidToken,
    WeakPassword,
}

public interface IPasswordRecoveryService
{
    // True when the operator has configured everything the flow needs. Drives
    // the public status endpoint and the forgot-password page's copy.
    bool IsEnabled { get; }

    // The per-normalized-email limiter, applied by the endpoint before any work
    // is done. Counts the SUBMITTED address whether or not it names an account,
    // so being throttled is never a signal that the address is real.
    bool TryConsumeEmailQuota(string? email);

    // Accepts a recovery request. ALWAYS completes without telling the caller
    // anything: unknown address, disabled account and undeliverable mail are
    // indistinguishable from success by construction, because the method
    // returns nothing at all.
    Task RequestAsync(string? email, CancellationToken cancellationToken = default);

    // Consumes a token and sets the new password, transactionally: the token is
    // marked used, every other outstanding token for that user is invalidated,
    // PasswordChangedAt moves and SecurityVersion increments so pre-reset
    // browser sessions stop working. No sign-in happens — a reset returns the
    // user to the login form.
    Task<PasswordResetResult> ResetAsync(
        string? rawToken, string? newPassword, CancellationToken cancellationToken = default);
}

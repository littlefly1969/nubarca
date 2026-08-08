using NubArca.Api.Domain;

namespace NubArca.Api.Auth;

public interface IAuthService
{
    Task<User?> AuthenticateAsync(string? email, string? password, CancellationToken cancellationToken = default);

    // Sets a new password AS A CREDENTIAL-SECURITY EVENT, in one transaction:
    // the hash, PasswordChangedAt, an incremented SecurityVersion, and the
    // invalidation of every outstanding password-recovery token for the user.
    // The three are inseparable — a hash written without the version bump would
    // leave a pre-change cookie working, and a bump without the token sweep
    // would leave a mailed reset link live against the new password.
    //
    // Returns the new SecurityVersion, so a caller changing their OWN password
    // can re-issue their cookie at the new version instead of signing itself out.
    Task<int> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);
}

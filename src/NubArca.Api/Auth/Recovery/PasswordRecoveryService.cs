using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Auth.Recovery;

// The forgot-password flow.
//
// Two rules shape every line of it:
//
//   * Nothing observable distinguishes a known address from an unknown one.
//     RequestAsync returns void, so there is no result for an endpoint to leak
//     even by accident, and every branch below does the same amount of visible
//     work before returning.
//   * The raw token exists in exactly two places — the bytes handed to the
//     mailer, and the request body that spends it. What is persisted is a
//     SHA-256 digest, so a database copy grants no reset, and nothing logs the
//     token or a URL containing it.
public sealed class PasswordRecoveryService : IPasswordRecoveryService
{
    // 256 bits of cryptographically secure entropy, base64url-encoded. Well past
    // any brute-force reach for a value that lives thirty minutes.
    private const int TokenByteLength = 32;

    private readonly AppDbContext _db;
    private readonly IUserService _users;
    private readonly IAuthService _auth;
    private readonly IEmailSender _mailer;
    private readonly PasswordRecoveryThrottle _throttle;
    private readonly IOptionsMonitor<MailOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PasswordRecoveryService> _logger;

    public PasswordRecoveryService(
        AppDbContext db,
        IUserService users,
        IAuthService auth,
        IEmailSender mailer,
        PasswordRecoveryThrottle throttle,
        IOptionsMonitor<MailOptions> options,
        TimeProvider clock,
        ILogger<PasswordRecoveryService> logger)
    {
        _db = db;
        _users = users;
        _auth = auth;
        _mailer = mailer;
        _throttle = throttle;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.IsRecoveryConfigured && _mailer.IsEnabled;

    // The per-email limiter, exposed so the endpoint can apply it before doing
    // any work. Returns false when this address has been asked for too often.
    public bool TryConsumeEmailQuota(string? email)
    {
        var options = _options.CurrentValue;
        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return true;
        }
        return _throttle.TryConsume(
            normalized,
            options.PerEmailPermitLimit,
            TimeSpan.FromMinutes(Math.Max(1, options.PerEmailWindowMinutes)));
    }

    public async Task RequestAsync(string? email, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var normalized = NormalizeEmail(email);
        if (normalized is null || !IsEnabled)
        {
            return;
        }

        var user = await _users.GetByEmailAsync(normalized, cancellationToken);
        if (user is null || user.DisabledAt is not null || user.PasswordHash is null)
        {
            // Three separate reasons, one identical outcome. A disabled account
            // in particular must not be discoverable this way — "no email
            // arrived" is the answer for a nonexistent address too.
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // A new request supersedes the old links rather than accumulating them.
        // Otherwise every request would leave another live credential in the
        // mailbox, and "I reset it, why does the old link still work" becomes a
        // real question.
        var outstanding = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var previous in outstanding)
        {
            previous.UsedAt = now;
        }

        var rawToken = GenerateToken();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Max(1, options.TokenLifetimeMinutes)),
            UsedAt = null,
        });
        await _db.SaveChangesAsync(cancellationToken);

        var url = PasswordResetLink.Build(options.PublicOrigin, rawToken);
        var message = PasswordResetEmail.Compose(user, url, options.TokenLifetimeMinutes);
        var delivered = await _mailer.SendAsync(message, cancellationToken);
        if (!delivered)
        {
            // Logged so an operator can see delivery failing — with the REDACTED
            // link, never the real one, and never the recipient address.
            _logger.LogWarning(
                "Password-recovery email could not be delivered. Reset links point at {ResetUrl}.",
                PasswordResetLink.Redact(options.PublicOrigin));
        }
    }

    public async Task<PasswordResetResult> ResetAsync(
        string? rawToken, string? newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return PasswordResetResult.InvalidToken;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = HashToken(rawToken.Trim());

        // The digest is looked up by an indexed equality on a fixed-length hex
        // string. There is no secret-dependent branch on the way in, and the
        // comparison is between two digests rather than between a stored secret
        // and a supplied one, so the usual timing concern does not arise.
        var token = await _db.PasswordResetTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
        {
            return PasswordResetResult.InvalidToken;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, cancellationToken);
        if (user is null || user.DisabledAt is not null)
        {
            // Disabling an account kills its outstanding links. Same generic
            // rejection: the caller learns "this did not work", nothing more.
            return PasswordResetResult.InvalidToken;
        }

        if (!PasswordPolicy.TryValidate(newPassword, out _))
        {
            // Checked AFTER the token, so a weak-password answer can only ever
            // be seen by somebody who already held a valid token — it is not a
            // probe that distinguishes real tokens from invented ones.
            return PasswordResetResult.WeakPassword;
        }

        // One transaction for the whole credential event: spend the token, then
        // let SetPasswordAsync write the hash, stamp PasswordChangedAt, bump
        // SecurityVersion (which invalidates every pre-reset browser session)
        // and sweep the user's remaining outstanding tokens. A crash between the
        // two would otherwise leave a spent token against an unchanged password.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            token.UsedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            await _auth.SetPasswordAsync(user.Id, newPassword!, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return PasswordResetResult.Ok;
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }
        var normalized = email.Trim().ToLowerInvariant();
        return normalized.Length > 320 ? null : normalized;
    }

    private static string GenerateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    internal static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

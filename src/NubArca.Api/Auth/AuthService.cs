using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Auth;

public sealed class AuthService : IAuthService
{
    // A pre-computed valid-shape hash used solely to equalise the wall-clock
    // time of AuthenticateAsync when the email does not match a user. Without
    // this the "user not found" path is fast (DB miss) and the "user found,
    // wrong password" path is slow (PBKDF2), leaking the email's existence
    // through timing. Lazy + readonly so it's computed once per process and
    // reused for every dummy verification.
    private static readonly Lazy<string> DummyHash = new(() =>
    {
        var hasher = new PasswordHasher<User>();
        var user = new User { Id = Guid.NewGuid(), Email = "_", DisplayName = "_" };
        return hasher.HashPassword(user, "not-a-real-password");
    });

    private readonly AppDbContext _db;
    private readonly IUserService _users;
    private readonly IPasswordHasher<User> _hasher;
    private readonly TimeProvider _clock;

    public AuthService(AppDbContext db, IUserService users, IPasswordHasher<User> hasher, TimeProvider clock)
    {
        _db = db;
        _users = users;
        _hasher = hasher;
        _clock = clock;
    }

    public async Task<User?> AuthenticateAsync(string? email, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            // Equalise time even on missing input — defends against a tiny
            // amount of timing variance, and keeps the response identical.
            VerifyAgainstDummy(password ?? string.Empty);
            return null;
        }

        var user = await _users.GetByEmailAsync(email, cancellationToken);
        if (user is null || user.DisabledAt is not null || user.PasswordHash is null)
        {
            VerifyAgainstDummy(password);
            return null;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }

    public async Task<int> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            throw new InvalidOperationException($"User '{userId}' was not found.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.PasswordChangedAt = now;
        // Every session opened with the previous credential is now stale: the
        // cookie carries the old version and CookieSessionValidator rejects it
        // on the next request.
        user.SecurityVersion += 1;

        // Any recovery link still in a mailbox dies with the old password. This
        // covers the reset path's own token too — it is marked used first, and
        // marking an already-used row used again is a no-op.
        var outstanding = await _db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in outstanding)
        {
            token.UsedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return user.SecurityVersion;
    }

    private void VerifyAgainstDummy(string password)
    {
        var probe = new User { Id = Guid.Empty, Email = "_", DisplayName = "_" };
        _ = _hasher.VerifyHashedPassword(probe, DummyHash.Value, password);
    }
}

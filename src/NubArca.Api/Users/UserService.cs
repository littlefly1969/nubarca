using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Npgsql;

namespace NubArca.Api.Users;

public sealed class UserService : IUserService
{
    private const string EmailUniqueIndex = "IX_users_Email";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public UserService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<User> CreateAsync(string email, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var normalizedEmail = NormalizeEmail(email);
        var trimmedDisplayName = displayName.Trim();

        if (await _db.Users.AsNoTracking().AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            throw new UserAlreadyExistsException(normalizedEmail);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            DisplayName = trimmedDisplayName,
            PasswordHash = null,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            DisabledAt = null,
        };

        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException ex) when (IsEmailUniqueViolation(ex))
        {
            _db.Entry(user).State = EntityState.Detached;
            throw new UserAlreadyExistsException(normalizedEmail);
        }
    }

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalizedEmail = NormalizeEmail(email);
        return _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
    }

    public async Task<bool> SetAdminAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var affected = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.IsAdmin, _ => isAdmin),
                cancellationToken);
        return affected > 0;
    }

    public async Task<bool> SetLanguageAsync(
        Guid userId,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!UiLanguages.TryNormalize(language, out var normalized))
        {
            throw new ArgumentException($"Unsupported UI language '{language}'.", nameof(language));
        }

        var affected = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.UiLanguage, _ => normalized),
                cancellationToken);
        return affected > 0;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool IsEmailUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName == EmailUniqueIndex;
    }
}

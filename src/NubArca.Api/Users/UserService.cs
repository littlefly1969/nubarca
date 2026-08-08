using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
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

    public async Task<bool> SetRoleAsync(
        Guid userId,
        string roleKey,
        CancellationToken cancellationToken = default)
    {
        // Roles are rows now, so "is this a role" is a lookup rather than a
        // list in code. The column is a foreign key as well: this check turns
        // what would otherwise surface as a constraint violation into a clear
        // argument error at the call site.
        if (!await _db.AccessRoles.AsNoTracking()
                .AnyAsync(r => r.Key == roleKey, cancellationToken))
        {
            throw new ArgumentException($"Unknown role '{roleKey}'.", nameof(roleKey));
        }

        var affected = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.RoleKey, _ => roleKey),
                cancellationToken);
        return affected > 0;
    }

    public async Task<bool> RecordLoginAsync(
        Guid userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var affected = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.LastLoginAt, _ => utcNow),
                cancellationToken);
        return affected > 0;
    }

    public async Task<bool> UpdateProfileAsync(
        Guid userId,
        UserProfileUpdate update,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (update.DisplayName is not null)
        {
            user.DisplayName = update.DisplayName;
        }
        if (update.ClearFirstName)
        {
            user.FirstName = null;
        }
        else if (update.FirstName is not null)
        {
            user.FirstName = update.FirstName;
        }
        if (update.ClearLastName)
        {
            user.LastName = null;
        }
        else if (update.LastName is not null)
        {
            user.LastName = update.LastName;
        }
        if (update.Language is not null && UiLanguages.TryNormalize(update.Language, out var language))
        {
            user.UiLanguage = language;
        }
        if (update.ClearTimeZone)
        {
            user.TimeZone = null;
        }
        else if (update.TimeZone is not null)
        {
            user.TimeZone = update.TimeZone;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
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

using Microsoft.EntityFrameworkCore;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Users;

// Admin-facing user management: list/create/update/reset-password/grant-admin/
// disable. Reuses IUserService (creation + email normalization/uniqueness)
// and IAuthService (password hashing) rather than duplicating that logic —
// this service owns only the admin-specific projection, guard rules (last
// admin, self-demotion, self-disable), and the fields those two services
// don't cover (IsAdmin/DisabledAt/UiLanguage at creation, display/language
// edits).
public sealed class AdminUserService : IAdminUserService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AppDbContext _db;
    private readonly IUserService _users;
    private readonly IAuthService _auth;
    private readonly TimeProvider _clock;

    public AdminUserService(AppDbContext db, IUserService users, IAuthService auth, TimeProvider clock)
    {
        _db = db;
        _users = users;
        _auth = auth;
        _clock = clock;
    }

    public async Task<ListAdminUsersResponse> ListAsync(
        string? query,
        bool includeDisabled,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        var effectiveOffset = Math.Max(offset, 0);

        var users = _db.Users.AsNoTracking().AsQueryable();
        if (!includeDisabled)
        {
            users = users.Where(u => u.DisabledAt == null);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim().ToLowerInvariant();
            users = users.Where(u =>
                u.Email.ToLower().Contains(normalized)
                || u.DisplayName.ToLower().Contains(normalized));
        }

        var total = await users.CountAsync(cancellationToken);
        var items = await users
            .OrderBy(u => u.Email)
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToListAsync(cancellationToken);

        return new ListAdminUsersResponse(
            items.Select(ToDto).ToList(),
            total,
            effectiveLimit,
            effectiveOffset);
    }

    public async Task<AdminUserDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : ToDto(user);
    }

    public async Task<AdminUserDto> CreateAsync(
        CreateAdminUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);

        var created = await _users.CreateAsync(request.Email, request.DisplayName, cancellationToken);

        if (!string.IsNullOrEmpty(request.Password))
        {
            await _auth.SetPasswordAsync(created.Id, request.Password, cancellationToken);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == created.Id, cancellationToken);
        if (request.IsAdmin)
        {
            user.IsAdmin = true;
        }
        if (request.Disabled)
        {
            user.DisabledAt = _clock.GetUtcNow().UtcDateTime;
        }
        if (UiLanguages.TryNormalize(request.Language, out var language))
        {
            user.UiLanguage = language;
        }
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(user);
    }

    public async Task<AdminUserDto?> UpdateAsync(
        Guid userId,
        UpdateAdminUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        if (request.DisplayName is not null)
        {
            var trimmed = request.DisplayName.Trim();
            if (trimmed.Length > 0)
            {
                user.DisplayName = trimmed;
            }
        }
        if (request.Language is not null && UiLanguages.TryNormalize(request.Language, out var language))
        {
            user.UiLanguage = language;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    public async Task<bool> ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, cancellationToken);
        if (!exists)
        {
            return false;
        }

        await _auth.SetPasswordAsync(userId, password, cancellationToken);
        return true;
    }

    public async Task<(AdminSetAdminResult Result, AdminUserDto? User)> SetAdminAsync(
        Guid callerUserId,
        Guid targetUserId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetAdminResult.NotFound, null);
        }

        if (!isAdmin)
        {
            // Disallow self-demotion outright (preferred behavior — avoids
            // an admin locking themselves out mid-session, which would
            // otherwise require another admin or CLI access to undo).
            if (callerUserId == targetUserId)
            {
                return (AdminSetAdminResult.SelfDemotion, null);
            }

            if (user.IsAdmin && await ActiveAdminCountAsync(cancellationToken) <= 1)
            {
                return (AdminSetAdminResult.LastAdmin, null);
            }
        }

        user.IsAdmin = isAdmin;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetAdminResult.Ok, ToDto(user));
    }

    public async Task<(AdminSetDisabledResult Result, AdminUserDto? User)> SetDisabledAsync(
        Guid callerUserId,
        Guid targetUserId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetDisabledResult.NotFound, null);
        }

        if (disabled)
        {
            // Disallow self-disable outright — a disabled cookie is
            // rejected immediately by CookieSessionValidator, so an admin
            // disabling themselves would lose access with no recovery path
            // short of CLI/DB access.
            if (callerUserId == targetUserId)
            {
                return (AdminSetDisabledResult.SelfDisable, null);
            }

            if (user.IsAdmin && await ActiveAdminCountAsync(cancellationToken) <= 1)
            {
                return (AdminSetDisabledResult.LastAdmin, null);
            }
        }

        user.DisabledAt = disabled ? _clock.GetUtcNow().UtcDateTime : null;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetDisabledResult.Ok, ToDto(user));
    }

    private Task<int> ActiveAdminCountAsync(CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().CountAsync(u => u.IsAdmin && u.DisabledAt == null, cancellationToken);

    private static AdminUserDto ToDto(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsAdmin,
        user.DisabledAt,
        user.CreatedAt,
        user.PasswordHash is not null,
        user.UiLanguage);
}

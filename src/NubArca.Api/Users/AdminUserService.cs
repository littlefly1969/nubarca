using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Users;

// Admin-facing user management: list/create/update/reset-password/set-role/
// set-permission-override/disable. Reuses IUserService (creation + email
// normalization/uniqueness), IAuthService (password hashing, which is also the
// credential-security event) and IUserPermissionService (override rows) rather
// than duplicating any of it — this service owns only the admin-specific
// projection and the guard rules: last administrator, self-demotion,
// self-disable, and the administrator-protection rule on denies.
public sealed class AdminUserService : IAdminUserService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AppDbContext _db;
    private readonly IUserService _users;
    private readonly IAuthService _auth;
    private readonly IUserPermissionService _permissions;
    private readonly TimeProvider _clock;

    public AdminUserService(
        AppDbContext db,
        IUserService users,
        IAuthService auth,
        IUserPermissionService permissions,
        TimeProvider clock)
    {
        _db = db;
        _users = users;
        _auth = auth;
        _permissions = permissions;
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

    public Task<User?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task<AdminUserDetailDto?> GetDetailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : await BuildDetailAsync(user, cancellationToken);
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

        // An unrecognised role falls back to Member rather than failing: the
        // endpoint validates the value, and a new account defaulting to the
        // ordinary role is the safe reading if one ever slips past.
        RoleKeys.TryNormalize(request.Role, out var roleKey);
        user.RoleKey = roleKey;

        if (request.Disabled)
        {
            user.DisabledAt = _clock.GetUtcNow().UtcDateTime;
        }
        if (UiLanguages.TryNormalize(request.Language, out var language))
        {
            user.UiLanguage = language;
        }
        if (UserProfileFields.TryNormalizeOptionalName(request.FirstName, out var firstName, out _))
        {
            user.FirstName = firstName;
        }
        if (UserProfileFields.TryNormalizeOptionalName(request.LastName, out var lastName, out _))
        {
            user.LastName = lastName;
        }
        if (UserProfileFields.TryNormalizeTimeZone(request.TimeZone, out var timeZone, out _))
        {
            user.TimeZone = timeZone;
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

        if (UserProfileFields.TryNormalizeDisplayName(request.DisplayName, out var displayName, out _)
            && displayName is not null)
        {
            user.DisplayName = displayName;
        }
        if (request.FirstName is not null
            && UserProfileFields.TryNormalizeOptionalName(request.FirstName, out var firstName, out _))
        {
            user.FirstName = firstName;
        }
        if (request.LastName is not null
            && UserProfileFields.TryNormalizeOptionalName(request.LastName, out var lastName, out _))
        {
            user.LastName = lastName;
        }
        if (request.Language is not null && UiLanguages.TryNormalize(request.Language, out var language))
        {
            user.UiLanguage = language;
        }
        if (request.TimeZone is not null
            && UserProfileFields.TryNormalizeTimeZone(request.TimeZone, out var timeZone, out _))
        {
            user.TimeZone = timeZone;
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

        // A credential-security event like any other: the target's existing
        // sessions and outstanding recovery links die with the old password.
        await _auth.SetPasswordAsync(userId, password, cancellationToken);
        return true;
    }

    public async Task<(AdminSetRoleResult Result, AdminUserDto? User)> SetRoleAsync(
        Guid callerUserId,
        Guid targetUserId,
        string? roleKey,
        CancellationToken cancellationToken = default)
    {
        // TryNormalize returns false for anything the catalogue does not name, so
        // an unknown value never silently becomes Member here.
        if (!RoleKeys.TryNormalize(roleKey, out var normalizedRole))
        {
            return (AdminSetRoleResult.UnknownRole, null);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetRoleResult.NotFound, null);
        }

        var losingAdministrator =
            RolePermissionCatalog.IsAdministrator(user.RoleKey)
            && !RolePermissionCatalog.IsAdministrator(normalizedRole);

        if (losingAdministrator)
        {
            // Disallow self-demotion outright (avoids an administrator locking
            // themselves out mid-session, which would otherwise need another
            // administrator or CLI access to undo).
            if (callerUserId == targetUserId)
            {
                return (AdminSetRoleResult.SelfDemotion, null);
            }

            if (await ActiveAdministratorCountAsync(cancellationToken) <= 1)
            {
                return (AdminSetRoleResult.LastAdmin, null);
            }
        }

        user.RoleKey = normalizedRole;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetRoleResult.Ok, ToDto(user));
    }

    public async Task<(AdminSetPermissionResult Result, AdminUserDetailDto? User)> SetPermissionOverrideAsync(
        Guid targetUserId,
        string permissionKey,
        string? effect,
        CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalog.IsKnown(permissionKey))
        {
            return (AdminSetPermissionResult.UnknownPermission, null);
        }

        if (!TryParseEffect(effect, out var parsed))
        {
            return (AdminSetPermissionResult.UnknownEffect, null);
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetPermissionResult.NotFound, null);
        }

        // An Administrator's administrative permissions are not deniable. The
        // resolver already ignores overrides for administrators, so a stored
        // Deny would be inert — but refusing it outright keeps the admin UI
        // honest instead of accepting a setting that does nothing.
        if (RolePermissionCatalog.IsAdministrator(user.RoleKey)
            && parsed == PermissionEffect.Deny
            && PermissionCatalog.IsAdministrative(permissionKey))
        {
            return (AdminSetPermissionResult.AdministratorProtected, null);
        }

        await _permissions.SetOverrideAsync(targetUserId, permissionKey, parsed, cancellationToken);
        return (AdminSetPermissionResult.Ok, await BuildDetailAsync(user, cancellationToken));
    }

    public async Task<(AdminSetPermissionResult Result, AdminUserDetailDto? User)> ClearPermissionOverrideAsync(
        Guid targetUserId,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalog.IsKnown(permissionKey))
        {
            return (AdminSetPermissionResult.UnknownPermission, null);
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetPermissionResult.NotFound, null);
        }

        // Clearing an override that is not there is a success: the caller asked
        // for "no exception on this key" and that is the state they get.
        await _permissions.ClearOverrideAsync(targetUserId, permissionKey, cancellationToken);
        return (AdminSetPermissionResult.Ok, await BuildDetailAsync(user, cancellationToken));
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
            // Disallow self-disable outright — a disabled cookie is rejected
            // immediately by CookieSessionValidator, so an administrator
            // disabling themselves would lose access with no recovery path
            // short of CLI/DB access.
            if (callerUserId == targetUserId)
            {
                return (AdminSetDisabledResult.SelfDisable, null);
            }

            if (RolePermissionCatalog.IsAdministrator(user.RoleKey)
                && await ActiveAdministratorCountAsync(cancellationToken) <= 1)
            {
                return (AdminSetDisabledResult.LastAdmin, null);
            }
        }

        user.DisabledAt = disabled ? _clock.GetUtcNow().UtcDateTime : null;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetDisabledResult.Ok, ToDto(user));
    }

    private Task<int> ActiveAdministratorCountAsync(CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().CountAsync(
            u => u.RoleKey == RoleKeys.Administrator && u.DisabledAt == null,
            cancellationToken);

    private async Task<AdminUserDetailDto> BuildDetailAsync(User user, CancellationToken cancellationToken)
    {
        var baseline = RolePermissionCatalog.For(user.RoleKey);
        var overrides = (await _permissions.ListOverridesAsync(user.Id, cancellationToken))
            .Where(o => PermissionCatalog.IsKnown(o.PermissionKey))
            .ToDictionary(o => o.PermissionKey, o => o.Effect, StringComparer.Ordinal);
        var effective = await _permissions.GetEffectiveAsync(user, cancellationToken);

        var rows = PermissionCatalog.All
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(definition =>
            {
                var hasOverride = overrides.TryGetValue(definition.Key, out var overrideEffect);
                return new AdminUserPermissionDto(
                    definition.Key,
                    definition.Group,
                    definition.Administrative,
                    baseline.Contains(definition.Key),
                    hasOverride ? (overrideEffect == PermissionEffect.Grant ? "grant" : "deny") : null,
                    effective.Has(definition.Key));
            })
            .ToList();

        return new AdminUserDetailDto(ToDto(user), rows);
    }

    private static bool TryParseEffect(string? raw, out PermissionEffect effect)
    {
        if (string.Equals(raw, "grant", StringComparison.OrdinalIgnoreCase))
        {
            effect = PermissionEffect.Grant;
            return true;
        }
        if (string.Equals(raw, "deny", StringComparison.OrdinalIgnoreCase))
        {
            effect = PermissionEffect.Deny;
            return true;
        }
        effect = PermissionEffect.Deny;
        return false;
    }

    private static AdminUserDto ToDto(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.FirstName,
        user.LastName,
        user.RoleKey,
        user.DisabledAt,
        user.CreatedAt,
        user.PasswordHash is not null,
        user.UiLanguage,
        user.TimeZone,
        user.LastLoginAt,
        user.PasswordChangedAt);
}

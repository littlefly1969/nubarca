using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Users;

// Admin-facing user management: list/create/update/reset-password/set-role/
// disable. Reuses IUserService (creation + email normalization/uniqueness),
// IAuthService (password hashing, which is also the credential-security event)
// and IRoleService (what a role means) rather than duplicating any of it — this
// service owns only the admin-specific projection and the guard rules.
//
// Four guards live here: last administrator, self-demotion, self-disable, and
// privilege escalation. The last one is the reason role assignment takes the
// CALLER's id: a user manager may only hand out authority they already hold
// themselves, and only an administrator may create another administrator.
public sealed class AdminUserService : IAdminUserService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly AppDbContext _db;
    private readonly IUserService _users;
    private readonly IAuthService _auth;
    private readonly IUserPermissionService _permissions;
    private readonly IRoleService _roles;
    private readonly TimeProvider _clock;

    public AdminUserService(
        AppDbContext db,
        IUserService users,
        IAuthService auth,
        IUserPermissionService permissions,
        IRoleService roles,
        TimeProvider clock)
    {
        _db = db;
        _users = users;
        _auth = auth;
        _permissions = permissions;
        _roles = roles;
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

    public async Task<AdminUserDetailDto?> GetDetailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : new AdminUserDetailDto(ToDto(user));
    }

    public async Task<(AdminSetRoleResult Result, AdminUserDto? User)> CreateAsync(
        Guid callerUserId,
        CreateAdminUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);

        // The role is settled BEFORE the account exists, so a refused
        // escalation never leaves a half-created user behind.
        var roleKey = await _roles.ResolveRoleKeyAsync(request.Role ?? RoleKeys.Member, cancellationToken);
        if (roleKey is null)
        {
            return (AdminSetRoleResult.UnknownRole, null);
        }
        if (!await MayAssignAsync(callerUserId, roleKey, cancellationToken))
        {
            return (AdminSetRoleResult.Escalation, null);
        }

        var created = await _users.CreateAsync(request.Email, request.DisplayName, cancellationToken);

        if (!string.IsNullOrEmpty(request.Password))
        {
            await _auth.SetPasswordAsync(created.Id, request.Password, cancellationToken);
        }

        var user = await _db.Users.FirstAsync(u => u.Id == created.Id, cancellationToken);
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

        return (AdminSetRoleResult.Ok, ToDto(user));
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
        // Resolved against the role table, so an unknown value fails rather than
        // silently becoming Member.
        var normalizedRole = await _roles.ResolveRoleKeyAsync(roleKey, cancellationToken);
        if (normalizedRole is null)
        {
            return (AdminSetRoleResult.UnknownRole, null);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return (AdminSetRoleResult.NotFound, null);
        }

        var losingAdministrator =
            RoleKeys.IsAdministrator(user.RoleKey)
            && !RoleKeys.IsAdministrator(normalizedRole);

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

        // Demoting an administrator is a guard question, answered above.
        // Handing out a role is an ESCALATION question: the caller may only
        // assign authority they already hold.
        if (!await MayAssignAsync(callerUserId, normalizedRole, cancellationToken))
        {
            return (AdminSetRoleResult.Escalation, null);
        }

        user.RoleKey = normalizedRole;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetRoleResult.Ok, ToDto(user));
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

            if (RoleKeys.IsAdministrator(user.RoleKey)
                && await ActiveAdministratorCountAsync(cancellationToken) <= 1)
            {
                return (AdminSetDisabledResult.LastAdmin, null);
            }
        }

        user.DisabledAt = disabled ? _clock.GetUtcNow().UtcDateTime : null;
        await _db.SaveChangesAsync(cancellationToken);
        return (AdminSetDisabledResult.Ok, ToDto(user));
    }

    // May this caller put this role on somebody?
    //
    // Administrator is gated on admin.roles.manage, which the catalogue makes
    // Administrator-only — so only an administrator can create another. Every
    // other role is gated on coverage: a manager holding admin.users.manage
    // alone cannot hand out People, the Private Vault or the jobs console by
    // assigning a role that carries them.
    private async Task<bool> MayAssignAsync(
        Guid callerUserId, string roleKey, CancellationToken cancellationToken)
    {
        var caller = await _permissions.GetEffectiveAsync(callerUserId, cancellationToken);

        if (RoleKeys.IsAdministrator(roleKey))
        {
            return caller.Has(Permissions.AdminRolesManage);
        }

        var target = await _roles.GetEffectivePermissionsAsync(roleKey, cancellationToken);
        return caller.Covers(target);
    }

    private Task<int> ActiveAdministratorCountAsync(CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().CountAsync(
            u => u.RoleKey == RoleKeys.Administrator && u.DisabledAt == null,
            cancellationToken);

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

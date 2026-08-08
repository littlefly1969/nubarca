using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Access;

// Resolves a user to the permission set of the role they hold.
//
// USER → ROLE → PERMISSIONS, with nothing in between. The whole resolution is
// one lookup of the role's rows, which is why editing a role takes effect for
// every user assigned to it on their NEXT request and needs no re-login: there
// is no cached authority anywhere, and no per-user state that would have to be
// recomputed.
//
// Scoped, with a per-request memo: the authorization handler may be invoked
// several times for one request (a composite Laboratory policy checks two keys)
// and `/api/auth/me` asks again afterwards. Reading current database state each
// REQUEST — rather than trusting a claim minted at login — is what makes a role
// change take effect immediately, and it is why no second session subsystem was
// needed for it. The memo is deliberately per-request and never longer-lived.
public sealed class UserPermissionService : IUserPermissionService
{
    private readonly AppDbContext _db;
    private readonly IRoleService _roles;
    private readonly Dictionary<Guid, EffectivePermissions> _memo = [];

    public UserPermissionService(AppDbContext db, IRoleService roles)
    {
        _db = db;
        _roles = roles;
    }

    public async Task<EffectivePermissions> GetEffectiveAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        if (_memo.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || user.DisabledAt is not null)
        {
            // A disabled account holds no authority. The cookie revalidator
            // rejects it a step earlier, but an authorization decision must not
            // depend on that having already happened.
            return EffectivePermissions.None;
        }

        return await ResolveAsync(user, cancellationToken);
    }

    public async Task<EffectivePermissions> GetEffectiveAsync(
        User user, CancellationToken cancellationToken = default)
    {
        if (_memo.TryGetValue(user.Id, out var cached))
        {
            return cached;
        }

        if (user.DisabledAt is not null)
        {
            return EffectivePermissions.None;
        }

        return await ResolveAsync(user, cancellationToken);
    }

    private async Task<EffectivePermissions> ResolveAsync(User user, CancellationToken cancellationToken)
    {
        // A missing role cannot happen — users.RoleKey is a foreign key with
        // RESTRICT on delete — so an empty answer here is least privilege for a
        // state the schema forbids, never a silent fallback to somebody else's
        // permissions.
        var keys = await _roles.GetEffectivePermissionsAsync(user.RoleKey, cancellationToken);

        var resolved = new EffectivePermissions(user.Id, user.RoleKey, keys);
        _memo[user.Id] = resolved;
        return resolved;
    }
}

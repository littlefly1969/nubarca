using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Access;

// Resolves role baseline + explicit overrides into one answer, and owns the
// override rows themselves.
//
// Scoped, with a per-request memo: the authorization handler may be invoked
// several times for one request (a composite Laboratory policy checks two keys)
// and `/api/auth/me` asks again afterwards. Reading current database state each
// REQUEST — rather than trusting a claim minted at login — is what makes a role
// or permission change take effect on the next request instead of at cookie
// expiry, and it is why no second session subsystem was needed for it.
public sealed class UserPermissionService : IUserPermissionService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, EffectivePermissions> _memo = [];

    public UserPermissionService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
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
        var baseline = RolePermissionCatalog.For(user.RoleKey);

        EffectivePermissions resolved;
        if (RolePermissionCatalog.IsAdministrator(user.RoleKey))
        {
            // An Administrator always holds the complete catalogue. Overrides
            // are not consulted at all: a DENY row must never be able to strip
            // an administrator of the authority that would let another
            // administrator put it back.
            resolved = new EffectivePermissions(
                user.Id, user.RoleKey, baseline.ToHashSet(StringComparer.Ordinal));
        }
        else
        {
            var overrides = await _db.UserPermissionOverrides
                .AsNoTracking()
                .Where(o => o.UserId == user.Id)
                .ToListAsync(cancellationToken);

            var keys = baseline.ToHashSet(StringComparer.Ordinal);
            foreach (var row in overrides)
            {
                // A row naming a key the catalogue no longer defines is inert
                // rather than an error: dropping a feature must not break the
                // login of everybody who had an override for it.
                if (!PermissionCatalog.IsKnown(row.PermissionKey))
                {
                    continue;
                }
                if (row.Effect == PermissionEffect.Grant)
                {
                    keys.Add(row.PermissionKey);
                }
                else
                {
                    keys.Remove(row.PermissionKey);
                }
            }

            resolved = new EffectivePermissions(user.Id, user.RoleKey, keys);
        }

        _memo[user.Id] = resolved;
        return resolved;
    }

    public async Task<IReadOnlyList<UserPermissionOverride>> ListOverridesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.UserPermissionOverrides
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderBy(o => o.PermissionKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> SetOverrideAsync(
        Guid userId, string permissionKey, PermissionEffect effect, CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalog.IsKnown(permissionKey))
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionKey == permissionKey, cancellationToken);

        if (existing is null)
        {
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PermissionKey = permissionKey,
                Effect = effect,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Effect = effect;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _memo.Remove(userId);
        return true;
    }

    public async Task<bool> ClearOverrideAsync(
        Guid userId, string permissionKey, CancellationToken cancellationToken = default)
    {
        if (!PermissionCatalog.IsKnown(permissionKey))
        {
            return false;
        }

        var removed = await _db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.PermissionKey == permissionKey)
            .ExecuteDeleteAsync(cancellationToken);

        _memo.Remove(userId);
        return removed > 0;
    }
}

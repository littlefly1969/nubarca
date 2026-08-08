using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Access;

// Roles as first-class, persisted objects: the seeder for the built-ins, the
// CRUD an administrator drives, and the validation that keeps a role from
// carrying a permission it must not.
//
// Three invariants live here and nowhere else:
//
//   * An Administrator resolves to the complete catalogue whatever rows exist.
//   * `admin.roles.manage` may only be held through the Administrator role, so
//     a user manager cannot mint a role that grants themselves role editing and
//     then step into it.
//   * A Laboratory section requires the Laboratory shell, which is the same
//     composite the endpoint policies enforce — stated once so the editor and
//     the guards cannot drift apart.
public sealed class RoleService : IRoleService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public RoleService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task EnsureBuiltInRolesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _db.AccessRoles
            .Where(r => RoleKeys.BuiltIn.Contains(r.Key))
            .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);

        foreach (var key in RoleKeys.BuiltIn)
        {
            var (name, description) = RoleDefaults.MetadataFor(key);
            if (!existing.TryGetValue(key, out var role))
            {
                _db.AccessRoles.Add(new AccessRole
                {
                    Key = key,
                    Name = name,
                    Description = description,
                    IsSystem = true,
                    IsAdministrator = RoleKeys.IsAdministrator(key),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Version = 1,
                });
                foreach (var permission in RoleDefaults.PermissionsFor(key))
                {
                    _db.RolePermissions.Add(new RolePermission
                    {
                        RoleKey = key,
                        PermissionKey = permission,
                    });
                }
                continue;
            }

            // Self-healing for facts that are code, not operator preference.
            if (!role.IsSystem || role.IsAdministrator != RoleKeys.IsAdministrator(key))
            {
                role.IsSystem = true;
                role.IsAdministrator = RoleKeys.IsAdministrator(key);
                role.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // The Administrator's rows are re-synced every boot so a release that
        // ADDS a permission does not leave the administration UI showing an
        // administrator who is missing one. Member and Restricted are left
        // exactly as the operator last saved them.
        //
        // Read untracked and reconciled against the database, so this is correct
        // on a context that has already seen these rows — the change tracker's
        // idea of what exists is not the authority here.
        var have = (await _db.RolePermissions.AsNoTracking()
                .Where(p => p.RoleKey == RoleKeys.Administrator)
                .Select(p => p.PermissionKey)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var want = PermissionCatalog.AllKeys.ToHashSet(StringComparer.Ordinal);
        if (have.SetEquals(want))
        {
            return;
        }

        var stale = have.Except(want, StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            await _db.RolePermissions
                .Where(p => p.RoleKey == RoleKeys.Administrator && stale.Contains(p.PermissionKey))
                .ExecuteDeleteAsync(cancellationToken);
        }

        DetachTrackedPermissions(RoleKeys.Administrator);

        foreach (var key in want.Except(have, StringComparer.Ordinal))
        {
            _db.RolePermissions.Add(new RolePermission
            {
                RoleKey = RoleKeys.Administrator,
                PermissionKey = key,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _db.AccessRoles.AsNoTracking().ToListAsync(cancellationToken);
        var permissions = await _db.RolePermissions.AsNoTracking().ToListAsync(cancellationToken);
        var counts = await _db.Users.AsNoTracking()
            .GroupBy(u => u.RoleKey)
            .Select(g => new { RoleKey = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleKey, x => x.Count, StringComparer.Ordinal, cancellationToken);

        var byRole = permissions
            .GroupBy(p => p.RoleKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToList(), StringComparer.Ordinal);

        return roles
            // System roles first, in their declared order, then custom roles by
            // name: the list an operator reads should start with the three they
            // did not create.
            .OrderBy(r => r.IsSystem ? RoleKeys.BuiltIn.ToList().IndexOf(r.Key) : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToDto(
                r,
                byRole.TryGetValue(r.Key, out var keys) ? keys : [],
                counts.TryGetValue(r.Key, out var count) ? count : 0))
            .ToList();
    }

    public async Task<RoleDto?> GetAsync(string roleKey, CancellationToken cancellationToken = default)
    {
        var role = await _db.AccessRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == roleKey, cancellationToken);
        if (role is null)
        {
            return null;
        }

        var permissions = await _db.RolePermissions.AsNoTracking()
            .Where(p => p.RoleKey == roleKey)
            .Select(p => p.PermissionKey)
            .ToListAsync(cancellationToken);
        var count = await _db.Users.AsNoTracking()
            .CountAsync(u => u.RoleKey == roleKey, cancellationToken);

        return ToDto(role, permissions, count);
    }

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        string roleKey, CancellationToken cancellationToken = default)
    {
        if (RoleKeys.IsAdministrator(roleKey))
        {
            // Never a query. An Administrator holds the complete catalogue by
            // definition, so no missing row and no edit can strip the authority
            // that would let another administrator restore it.
            return PermissionCatalog.AllKeys.ToHashSet(StringComparer.Ordinal);
        }

        var keys = await _db.RolePermissions.AsNoTracking()
            .Where(p => p.RoleKey == roleKey)
            .Select(p => p.PermissionKey)
            .ToListAsync(cancellationToken);

        // A row naming a key the catalogue no longer defines is inert rather
        // than an error: retiring a feature must not break the login of
        // everybody whose role mentioned it.
        return keys.Where(PermissionCatalog.IsKnown).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<(RoleMutationResult Result, RoleDto? Role)> CreateAsync(
        CreateRoleRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(request.Name, out var name))
        {
            return (RoleMutationResult.InvalidName, null);
        }

        var validation = ValidatePermissions(RoleKeys.NewCustomKey(), request.Permissions, out var permissions);
        if (validation != RoleMutationResult.Ok)
        {
            return (validation, null);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var role = new AccessRole
        {
            // Generated server-side and opaque. A display name is never
            // identity: renaming a role must not re-point every user row.
            Key = RoleKeys.NewCustomKey(),
            Name = name,
            Description = NormalizeDescription(request.Description),
            IsSystem = false,
            IsAdministrator = false,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        _db.AccessRoles.Add(role);
        foreach (var key in permissions)
        {
            _db.RolePermissions.Add(new RolePermission { RoleKey = role.Key, PermissionKey = key });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return (RoleMutationResult.Ok, ToDto(role, permissions, 0));
    }

    public async Task<(RoleMutationResult Result, RoleDto? Role)> UpdateAsync(
        string roleKey, UpdateRoleRequest request, CancellationToken cancellationToken = default)
    {
        var role = await _db.AccessRoles.FirstOrDefaultAsync(r => r.Key == roleKey, cancellationToken);
        if (role is null)
        {
            return (RoleMutationResult.NotFound, null);
        }

        // The Administrator role is not editable at all. Its permission set is
        // the complete catalogue by definition and its name is what every
        // operator recognises; allowing either to be changed would let one
        // administrator make the role unrecognisable to the next.
        if (role.IsAdministrator)
        {
            return (RoleMutationResult.SystemRoleProtected, null);
        }

        if (request.Version is int expected && expected != role.Version)
        {
            return (RoleMutationResult.VersionConflict, null);
        }

        if (!TryNormalizeName(request.Name, out var name))
        {
            return (RoleMutationResult.InvalidName, null);
        }

        var validation = ValidatePermissions(roleKey, request.Permissions, out var permissions);
        if (validation != RoleMutationResult.Ok)
        {
            return (validation, null);
        }

        role.Name = name;
        role.Description = NormalizeDescription(request.Description);
        role.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        role.Version += 1;

        // The whole set is replaced in one transaction, so no request ever
        // leaves a role half-edited for the users assigned to it.
        await _db.RolePermissions.Where(p => p.RoleKey == roleKey).ExecuteDeleteAsync(cancellationToken);
        DetachTrackedPermissions(roleKey);
        foreach (var key in permissions)
        {
            _db.RolePermissions.Add(new RolePermission { RoleKey = roleKey, PermissionKey = key });
        }
        await _db.SaveChangesAsync(cancellationToken);

        var count = await _db.Users.AsNoTracking()
            .CountAsync(u => u.RoleKey == roleKey, cancellationToken);
        return (RoleMutationResult.Ok, ToDto(role, permissions, count));
    }

    public async Task<RoleMutationResult> DeleteAsync(
        string roleKey, CancellationToken cancellationToken = default)
    {
        var role = await _db.AccessRoles.FirstOrDefaultAsync(r => r.Key == roleKey, cancellationToken);
        if (role is null)
        {
            return RoleMutationResult.NotFound;
        }
        if (role.IsSystem)
        {
            return RoleMutationResult.SystemRoleProtected;
        }

        // Never cascade into accounts and never silently move them somewhere
        // else: an operator reassigns the users, then deletes the role.
        if (await _db.Users.AsNoTracking().AnyAsync(u => u.RoleKey == roleKey, cancellationToken))
        {
            return RoleMutationResult.RoleInUse;
        }

        await _db.RolePermissions.Where(p => p.RoleKey == roleKey).ExecuteDeleteAsync(cancellationToken);
        _db.AccessRoles.Remove(role);
        await _db.SaveChangesAsync(cancellationToken);
        return RoleMutationResult.Ok;
    }

    public async Task<string?> ResolveRoleKeyAsync(
        string? raw, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var exact = await _db.AccessRoles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == trimmed, cancellationToken);
        if (exact is not null)
        {
            return exact.Key;
        }

        // A CLI operator types "member" or "Laboratorio", not an opaque key.
        // Loaded and compared in memory so the match is culture-invariant rather
        // than dependent on the database collation.
        var all = await _db.AccessRoles.AsNoTracking().ToListAsync(cancellationToken);
        return all.FirstOrDefault(r =>
                   string.Equals(r.Key, trimmed, StringComparison.OrdinalIgnoreCase))?.Key
               ?? all.FirstOrDefault(r =>
                   string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    // A permission set is replaced by DELETE-then-INSERT, and the delete runs as
    // SQL rather than through the change tracker. Any row this context happens
    // to be tracking is therefore stale immediately afterwards, and re-adding
    // the same (role, permission) pair would collide with that stale instance
    // instead of inserting. Forgetting them first is what makes the replacement
    // safe on a context that has already read these rows.
    private void DetachTrackedPermissions(string roleKey)
    {
        foreach (var tracked in _db.ChangeTracker.Entries<RolePermission>()
                     .Where(e => e.Entity.RoleKey == roleKey)
                     .ToList())
        {
            tracked.State = EntityState.Detached;
        }
    }

    // Validates a caller-supplied permission set against the catalogue and the
    // two structural rules. Returns the normalised, ordinal-sorted set.
    private static RoleMutationResult ValidatePermissions(
        string roleKey, IReadOnlyList<string>? requested, out IReadOnlyList<string> normalized)
    {
        normalized = [];
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in requested ?? [])
        {
            var key = raw?.Trim();
            if (!PermissionCatalog.IsKnown(key))
            {
                return RoleMutationResult.UnknownPermission;
            }
            if (PermissionCatalog.IsAdministratorOnly(key) && !RoleKeys.IsAdministrator(roleKey))
            {
                return RoleMutationResult.AdministratorOnlyPermission;
            }
            set.Add(key!);
        }

        // A section without its shell grants nothing, so accepting it would
        // persist a setting that reads as working and is not. The browser
        // enables the parent for the operator; the server refuses either way,
        // because a crafted request must not be able to store the broken shape.
        foreach (var key in set)
        {
            var parent = PermissionCatalog.ParentOf(key);
            if (parent is not null && !set.Contains(parent))
            {
                return RoleMutationResult.MissingParentPermission;
            }
        }

        normalized = set.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return RoleMutationResult.Ok;
    }

    private static bool TryNormalizeName(string? raw, out string name)
    {
        name = raw?.Trim() ?? string.Empty;
        return name.Length is > 0 and <= 64;
    }

    private static string? NormalizeDescription(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return trimmed.Length <= 256 ? trimmed : trimmed[..256];
    }

    private static RoleDto ToDto(AccessRole role, IReadOnlyList<string> permissions, int userCount)
    {
        // The Administrator's presented set is the complete catalogue, matching
        // what authorization actually resolves rather than whatever rows happen
        // to exist.
        var keys = role.IsAdministrator
            ? PermissionCatalog.AllKeys
            : permissions.Where(PermissionCatalog.IsKnown)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

        return new RoleDto(
            role.Key,
            role.Name,
            role.Description,
            role.IsSystem,
            role.IsAdministrator,
            userCount,
            keys,
            role.Version);
    }
}

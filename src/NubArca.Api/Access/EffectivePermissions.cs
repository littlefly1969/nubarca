namespace NubArca.Api.Access;

// What a user may actually do, right now: the role baseline with their explicit
// overrides applied. Produced by IUserPermissionService and never assembled by
// hand at a call site.
public sealed class EffectivePermissions
{
    public static readonly EffectivePermissions None =
        new(Guid.Empty, RoleKeys.Restricted, new HashSet<string>(StringComparer.Ordinal));

    private readonly IReadOnlySet<string> _keys;

    public EffectivePermissions(Guid userId, string roleKey, IReadOnlySet<string> keys)
    {
        UserId = userId;
        RoleKey = roleKey;
        _keys = keys;
    }

    public Guid UserId { get; }

    public string RoleKey { get; }

    public bool IsAdministrator => RolePermissionCatalog.IsAdministrator(RoleKey);

    public bool Has(string permissionKey) => _keys.Contains(permissionKey);

    public bool HasAll(IReadOnlyList<string> permissionKeys)
    {
        foreach (var key in permissionKeys)
        {
            if (!_keys.Contains(key))
            {
                return false;
            }
        }
        return true;
    }

    // Ordinal-sorted, so `/api/auth/me` is byte-stable for the same authority.
    public IReadOnlyList<string> ToSortedList() =>
        _keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
}

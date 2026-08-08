namespace NubArca.Api.Access;

// What a user may actually do, right now: exactly the permission set of the
// role they hold. There is no second term — no grant, no deny, no exception —
// so this object is the role's set with the user's identity attached, and it is
// never assembled by hand at a call site.
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

    public bool IsAdministrator => RoleKeys.IsAdministrator(RoleKey);

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

    public bool HasAny(IReadOnlyList<string> permissionKeys)
    {
        foreach (var key in permissionKeys)
        {
            if (_keys.Contains(key))
            {
                return true;
            }
        }
        return false;
    }

    // True when every key in `keys` is one this authority also holds. The
    // privilege-escalation test: a user manager may only hand out a role whose
    // permissions they already have themselves.
    public bool Covers(IEnumerable<string> keys)
    {
        foreach (var key in keys)
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

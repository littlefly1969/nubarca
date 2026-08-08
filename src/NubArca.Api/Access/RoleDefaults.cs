namespace NubArca.Api.Access;

// What the three built-in roles are when an installation first meets them.
//
// Only Administrator is a permanent contract: it always carries the COMPLETE
// catalogue, re-synced on every boot, because an administrator's authority must
// never be quietly narrowed by a release that adds a permission.
//
// Member and Restricted are DEFAULTS, not contracts. Member is deliberately
// "everything a normal NubArca user could do before roles existed" — that is
// the migration promise, since every pre-role non-admin account became a
// Member — and Restricted is empty because the core personal cloud (files,
// media, albums, sharing, account, trash) is not permission-gated at all. Once
// seeded, both belong to the operator: the seeder never rewrites an existing
// role's permissions, so an edit survives the next deploy.
public static class RoleDefaults
{
    public const string AdministratorName = "Administrator";
    public const string AdministratorDescription =
        "Full control of NubArca, including users and roles.";

    public const string MemberName = "Member";
    public const string MemberDescription =
        "Standard access to NubArca advanced features.";

    public const string RestrictedName = "Restricted";
    public const string RestrictedDescription =
        "Files, media, albums, sharing and trash only.";

    // The complete catalogue, always.
    public static IReadOnlyList<string> AdministratorPermissions => PermissionCatalog.AllKeys;

    // Every non-administrative feature permission. Derived from the catalogue
    // rather than listed by hand: a new feature key added without a decision
    // about Member would otherwise silently remove a capability from every
    // account that migrated into this role.
    public static readonly IReadOnlyList<string> MemberPermissions =
        PermissionCatalog.All
            .Where(p => !p.Administrative)
            .Select(p => p.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

    public static readonly IReadOnlyList<string> RestrictedPermissions = [];

    public static IReadOnlyList<string> PermissionsFor(string roleKey) => roleKey switch
    {
        RoleKeys.Administrator => AdministratorPermissions,
        RoleKeys.Member => MemberPermissions,
        _ => RestrictedPermissions,
    };

    public static (string Name, string Description) MetadataFor(string roleKey) => roleKey switch
    {
        RoleKeys.Administrator => (AdministratorName, AdministratorDescription),
        RoleKeys.Member => (MemberName, MemberDescription),
        _ => (RestrictedName, RestrictedDescription),
    };
}

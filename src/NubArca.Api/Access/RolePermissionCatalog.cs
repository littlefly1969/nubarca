namespace NubArca.Api.Access;

// The baseline permission bundle each built-in role carries.
//
// Member is deliberately "everything a normal NubArca user could do before
// roles existed". That is the migration contract, not a product preference: an
// existing non-admin account becomes a Member and must not notice the change.
// Adding a NEW feature permission therefore has to be a decision about Member
// as well — a key added to the catalogue but not to Member silently removes a
// capability from every existing account.
//
// Restricted keeps the core personal cloud — files/folders, media library,
// albums, authenticated sharing, account/profile and trash. None of those are
// permission-gated at all, so Restricted needs no key to reach them; its bundle
// is empty by construction rather than by omission.
public static class RolePermissionCatalog
{
    private static readonly IReadOnlySet<string> AdministratorPermissions =
        PermissionCatalog.AllKeys.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> MemberPermissions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.PeopleAccess,
            Permissions.SemanticSearchAccess,
            Permissions.LaboratoryAccess,
            Permissions.LaboratoryPlates,
            Permissions.LaboratoryAesthetics,
            Permissions.CloudFunctionsAccess,
            Permissions.PrivateVaultAccess,
            Permissions.TvManage,
        };

    private static readonly IReadOnlySet<string> RestrictedPermissions =
        new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> For(string? roleKey) => roleKey switch
    {
        RoleKeys.Administrator => AdministratorPermissions,
        RoleKeys.Restricted => RestrictedPermissions,
        // An unrecognised value can only come from a row written by an older or
        // newer build. Member is the safe reading: it is what every pre-role
        // account migrated to, so an unknown key never silently grants
        // administration and never silently strips a normal user's features.
        _ => MemberPermissions,
    };

    public static bool IsAdministrator(string? roleKey) =>
        string.Equals(roleKey, RoleKeys.Administrator, StringComparison.Ordinal);
}

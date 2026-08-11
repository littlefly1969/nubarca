namespace NubArca.Api.Access;

// A permission as the product describes it: the stable key, the grouping the
// admin UI needs so an administrator never has to read raw keys, and the two
// structural facts a role editor cannot be correct without.
//
// `Parent` names a permission this one is meaningless without — a Laboratory
// section requires the Laboratory shell. It is the SAME fact the composite
// authorization policies enforce, stated once so the role editor and the
// endpoint guards cannot drift apart.
//
// `AdministratorOnly` marks a capability that may only ever be held through the
// built-in Administrator role. `Administrative` is the weaker property: an
// administration surface, which an operator may legitimately delegate.
public sealed record PermissionDefinition(
    string Key,
    string Group,
    bool Administrative,
    string? Parent = null,
    bool AdministratorOnly = false);

// Permission groups, used only to organise the admin editor.
public static class PermissionGroups
{
    public const string Features = "features";
    public const string Administration = "administration";
}

// The authoritative catalogue. The browser never defines a permission: a role
// permission naming a key that is not here is rejected server-side, so a
// hand-crafted request cannot persist an arbitrary string and cannot invent a
// permission that some future release might accidentally honour.
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permissions.PeopleAccess, PermissionGroups.Features, false),
        new(Permissions.PeopleClusterRebuild, PermissionGroups.Features, false,
            Parent: Permissions.PeopleAccess),
        new(Permissions.SemanticSearchAccess, PermissionGroups.Features, false),
        new(Permissions.LaboratoryAccess, PermissionGroups.Features, false),
        new(Permissions.LaboratoryPlates, PermissionGroups.Features, false,
            Parent: Permissions.LaboratoryAccess),
        new(Permissions.LaboratoryAesthetics, PermissionGroups.Features, false,
            Parent: Permissions.LaboratoryAccess),
        new(Permissions.CloudFunctionsAccess, PermissionGroups.Features, false),
        new(Permissions.PrivateVaultAccess, PermissionGroups.Features, false),
        new(Permissions.TvManage, PermissionGroups.Features, false),
        new(Permissions.CastAccess, PermissionGroups.Features, false),
        new(Permissions.AdminDashboard, PermissionGroups.Administration, true),
        new(Permissions.AdminUsersManage, PermissionGroups.Administration, true),
        new(Permissions.AdminImport, PermissionGroups.Administration, true),
        new(Permissions.AdminJobsManage, PermissionGroups.Administration, true),
        new(Permissions.AdminRolesManage, PermissionGroups.Administration, true,
            AdministratorOnly: true),
    ];

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    // Every key, ordinal-sorted. The sort is part of the contract: `/api/auth/me`
    // returns effective permissions in a deterministic order so a client (or a
    // test) can compare two responses without normalising them first.
    public static readonly IReadOnlyList<string> AllKeys =
        All.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // What an ordinary (non-Administrator) role may carry. Everything except the
    // Administrator-only capabilities, so "which boxes can this editor show" has
    // one answer rather than a filter repeated per call site.
    public static readonly IReadOnlyList<string> AssignableKeys =
        All.Where(p => !p.AdministratorOnly)
            .Select(p => p.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

    public static bool IsKnown(string? key) =>
        key is not null && ByKey.ContainsKey(key);

    public static PermissionDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    public static bool IsAdministrative(string key) =>
        ByKey.TryGetValue(key, out var definition) && definition.Administrative;

    public static bool IsAdministratorOnly(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) && definition.AdministratorOnly;

    // The permission this one requires as well, or null. Used by the role editor
    // and by role validation; the endpoint guards state the same requirement as
    // a composite policy.
    public static string? ParentOf(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition.Parent : null;
}

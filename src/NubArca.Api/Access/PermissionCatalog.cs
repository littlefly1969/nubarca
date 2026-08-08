namespace NubArca.Api.Access;

// A permission as the product describes it: the stable key plus the grouping
// and label the admin UI needs so an administrator never has to read raw keys.
// `Administrative` marks the safety-critical subset an explicit per-user DENY
// may not remove from an Administrator.
public sealed record PermissionDefinition(
    string Key,
    string Group,
    bool Administrative);

// Permission groups, used only to organise the admin editor.
public static class PermissionGroups
{
    public const string Features = "features";
    public const string Administration = "administration";
}

// The authoritative catalogue. The browser never defines a permission: an
// override naming a key that is not here is rejected server-side, so a
// hand-crafted request cannot persist an arbitrary string and cannot invent a
// permission that some future release might accidentally honour.
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permissions.PeopleAccess, PermissionGroups.Features, false),
        new(Permissions.SemanticSearchAccess, PermissionGroups.Features, false),
        new(Permissions.LaboratoryAccess, PermissionGroups.Features, false),
        new(Permissions.LaboratoryPlates, PermissionGroups.Features, false),
        new(Permissions.LaboratoryAesthetics, PermissionGroups.Features, false),
        new(Permissions.CloudFunctionsAccess, PermissionGroups.Features, false),
        new(Permissions.PrivateVaultAccess, PermissionGroups.Features, false),
        new(Permissions.TvManage, PermissionGroups.Features, false),
        new(Permissions.AdminDashboard, PermissionGroups.Administration, true),
        new(Permissions.AdminUsersManage, PermissionGroups.Administration, true),
        new(Permissions.AdminImport, PermissionGroups.Administration, true),
        new(Permissions.AdminJobsManage, PermissionGroups.Administration, true),
    ];

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    // Every key, ordinal-sorted. The sort is part of the contract: `/api/auth/me`
    // returns effective permissions in a deterministic order so a client (or a
    // test) can compare two responses without normalising them first.
    public static readonly IReadOnlyList<string> AllKeys =
        All.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public static bool IsKnown(string? key) =>
        key is not null && ByKey.ContainsKey(key);

    public static PermissionDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    public static bool IsAdministrative(string key) =>
        ByKey.TryGetValue(key, out var definition) && definition.Administrative;
}

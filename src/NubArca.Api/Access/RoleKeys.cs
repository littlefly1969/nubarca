namespace NubArca.Api.Access;

// The identity of a role.
//
// A role is a ROW now — an operator creates, edits and deletes them — so this
// type no longer enumerates what exists. What it still owns is the part that
// must never be operator-editable: the three built-in keys, and the shape of a
// custom one.
//
// A key is an immutable internal identifier, never a display name. Renaming
// "Famiglia" to "Casa" must not re-point every user row, and two roles are
// allowed to be called the same thing by accident without becoming the same
// role. Custom keys are therefore generated server-side and are opaque.
public static class RoleKeys
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";
    public const string Restricted = "Restricted";

    // Custom keys are namespaced so a built-in can never be forged by supplying
    // its name, and so a row's origin is readable straight out of psql.
    public const string CustomPrefix = "custom:";

    public static readonly IReadOnlyList<string> BuiltIn =
        [Administrator, Member, Restricted];

    public static bool IsBuiltIn(string? roleKey) =>
        roleKey is not null && BuiltIn.Contains(roleKey, StringComparer.Ordinal);

    // The one role that carries administration. Custom roles are never
    // administrators — the catalogue refuses to put an Administrator-only
    // permission on one — so this comparison is the whole test.
    public static bool IsAdministrator(string? roleKey) =>
        string.Equals(roleKey, Administrator, StringComparison.Ordinal);

    public static string NewCustomKey() =>
        CustomPrefix + Guid.NewGuid().ToString("n");

    public static bool IsCustom(string? roleKey) =>
        roleKey is not null && roleKey.StartsWith(CustomPrefix, StringComparison.Ordinal);
}

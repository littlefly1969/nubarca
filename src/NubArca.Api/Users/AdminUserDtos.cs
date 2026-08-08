namespace NubArca.Api.Users;

// Safe admin-facing projection of a User row. Never includes PasswordHash, raw
// auth claims, token hashes or storage internals — HasPassword (a boolean
// derived from PasswordHash != null) is the only credential signal, and
// SecurityVersion stays internal because it is a session mechanism, not
// information an operator acts on.
//
// `IsAdmin` is gone from this projection: Role is the source of truth and the
// admin UI shows it directly.
public sealed record AdminUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string? FirstName,
    string? LastName,
    string Role,
    DateTime? DisabledAt,
    DateTime CreatedAt,
    bool HasPassword,
    string Language,
    string? TimeZone,
    DateTime? LastLoginAt,
    DateTime? PasswordChangedAt);

// One permission as the Access editor needs to show it: what the role says,
// what the override says, and what the two add up to. Keeping the three apart
// is the point — "inherited", "explicitly granted" and "explicitly denied" look
// the same in a plain list of effective keys, and an administrator editing
// access has to be able to tell them apart.
public sealed record AdminUserPermissionDto(
    string Key,
    string Group,
    bool Administrative,
    bool InheritedFromRole,
    // "grant" | "deny" | null (no override; the role baseline applies).
    string? Override,
    bool Effective);

public sealed record AdminUserDetailDto(
    AdminUserDto User,
    IReadOnlyList<AdminUserPermissionDto> Permissions);

public sealed record ListAdminUsersResponse(
    IReadOnlyList<AdminUserDto> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record CreateAdminUserRequest(
    string? Email,
    string? DisplayName,
    string? Password,
    string? Role = null,
    bool Disabled = false,
    string? Language = null,
    string? FirstName = null,
    string? LastName = null,
    string? TimeZone = null);

// Profile fields only. Role, permissions, disabled state and email are separate
// guarded endpoints so no profile write can reach them.
public sealed record UpdateAdminUserRequest(
    string? DisplayName,
    string? FirstName,
    string? LastName,
    string? Language,
    string? TimeZone);

public sealed record SetAdminUserPasswordRequest(string? Password);

public sealed record SetAdminUserRoleRequest(string? Role);

// "grant" | "deny". Clearing an override is a DELETE, not a third effect value,
// so "remove the exception" and "set an exception" are different verbs.
public sealed record SetAdminUserPermissionRequest(string? Effect);

public sealed record SetAdminUserDisabledRequest(bool Disabled);

// The catalogue as the admin UI reads it, so labels and grouping come from the
// server rather than from a list the frontend has to keep in step.
public sealed record PermissionCatalogEntryDto(string Key, string Group, bool Administrative);

public sealed record PermissionCatalogResponse(
    IReadOnlyList<string> Roles,
    IReadOnlyList<PermissionCatalogEntryDto> Permissions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RoleBaselines);

// Outcomes for the guarded mutations. The endpoint layer maps these to HTTP
// status/messages; the service never throws for an expected, guardable state.
public enum AdminSetRoleResult
{
    Ok,
    NotFound,
    UnknownRole,
    LastAdmin,
    SelfDemotion,
}

public enum AdminSetDisabledResult
{
    Ok,
    NotFound,
    LastAdmin,
    SelfDisable,
}

public enum AdminSetPermissionResult
{
    Ok,
    NotFound,
    UnknownPermission,
    UnknownEffect,
    // An Administrator may not be stripped of an administrative permission by a
    // per-user DENY. Without this, one administrator could quietly remove
    // another's ability to put it back.
    AdministratorProtected,
}

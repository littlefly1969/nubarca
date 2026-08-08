namespace NubArca.Api.Users;

// Safe admin-facing projection of a User row. Never includes PasswordHash, raw
// auth claims, token hashes or storage internals — HasPassword (a boolean
// derived from PasswordHash != null) is the only credential signal, and
// SecurityVersion stays internal because it is a session mechanism, not
// information an operator acts on.
//
// `Role` is the whole authorization story for this account: a user holds one
// role and the role owns its permissions.
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

// The admin detail view. Deliberately carries NO permission list: permissions
// belong to the role, are read from the role catalogue, and duplicating them
// into a user-shaped DTO is exactly what made a role change render stale — the
// user object was refreshed while the permission rows beside it still described
// the previous role.
public sealed record AdminUserDetailDto(AdminUserDto User);

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

// Profile fields only. Role, disabled state and email are separate guarded
// endpoints so no profile write can reach them.
public sealed record UpdateAdminUserRequest(
    string? DisplayName,
    string? FirstName,
    string? LastName,
    string? Language,
    string? TimeZone);

public sealed record SetAdminUserPasswordRequest(string? Password);

public sealed record SetAdminUserRoleRequest(string? Role);

public sealed record SetAdminUserDisabledRequest(bool Disabled);

// The permission catalogue as the admin UI reads it, so grouping, the
// Laboratory dependency and the Administrator-only marker come from the server
// rather than from a list the frontend has to keep in step. Labels stay in the
// browser: they are product copy in two languages, not an authorization fact.
public sealed record PermissionCatalogEntryDto(
    string Key,
    string Group,
    bool Administrative,
    string? Parent,
    bool Assignable);

public sealed record PermissionCatalogResponse(
    IReadOnlyList<PermissionCatalogEntryDto> Permissions);

// Outcomes for the guarded mutations. The endpoint layer maps these to HTTP
// status/messages; the service never throws for an expected, guardable state.
public enum AdminSetRoleResult
{
    Ok,
    NotFound,
    UnknownRole,
    LastAdmin,
    SelfDemotion,
    // The caller tried to hand out authority they do not hold themselves —
    // promoting to Administrator without admin.roles.manage, or assigning a
    // role carrying a permission the caller lacks.
    Escalation,
}

public enum AdminSetDisabledResult
{
    Ok,
    NotFound,
    LastAdmin,
    SelfDisable,
}

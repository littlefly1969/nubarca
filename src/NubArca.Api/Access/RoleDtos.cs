namespace NubArca.Api.Access;

// A role as the admin UI reads it. `Permissions` is the WHOLE set the role
// carries, ordinal-sorted, so a client renders a preview from role data alone
// and never has to reconstruct one from a user's cached detail.
public sealed record RoleDto(
    string Key,
    string Name,
    string? Description,
    bool IsSystem,
    bool IsAdministrator,
    int UserCount,
    IReadOnlyList<string> Permissions,
    int Version);

public sealed record ListRolesResponse(IReadOnlyList<RoleDto> Roles);

public sealed record CreateRoleRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? Permissions);

// Name, description and the FULL permission set in one deliberate save. A
// request per checkbox would leave a half-edited role live for everybody
// assigned to it, so the editor sends a draft and this replaces the set
// atomically.
public sealed record UpdateRoleRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? Permissions,
    int? Version);

// Outcomes for the guarded mutations. The endpoint layer maps these to HTTP
// status/messages; the service never throws for an expected, guardable state.
public enum RoleMutationResult
{
    Ok,
    NotFound,
    InvalidName,
    UnknownPermission,
    // A permission that may only be held through the built-in Administrator
    // role was named on another role.
    AdministratorOnlyPermission,
    // A Laboratory section without the Laboratory shell: the role would carry a
    // key that grants nothing, which reads as a working setting and is not one.
    MissingParentPermission,
    // The Administrator role is not editable at all, and no system role is
    // deletable.
    SystemRoleProtected,
    // Users still reference the role. Reassign them first: nothing here
    // cascade-deletes accounts or silently moves them to another role.
    RoleInUse,
    VersionConflict,
}

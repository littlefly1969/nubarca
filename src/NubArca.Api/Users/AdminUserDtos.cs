namespace NubArca.Api.Users;

// Safe admin-facing projection of a User row. Never includes PasswordHash,
// raw auth claims, token hashes, or storage internals — only HasPassword
// (a boolean derived from PasswordHash != null).
public sealed record AdminUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    DateTime? DisabledAt,
    DateTime CreatedAt,
    bool HasPassword,
    string Language);

public sealed record ListAdminUsersResponse(
    IReadOnlyList<AdminUserDto> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record CreateAdminUserRequest(
    string? Email,
    string? DisplayName,
    string? Password,
    bool IsAdmin = false,
    bool Disabled = false,
    string? Language = null);

public sealed record UpdateAdminUserRequest(string? DisplayName, string? Language);

public sealed record SetAdminUserPasswordRequest(string? Password);

public sealed record SetAdminUserAdminRequest(bool IsAdmin);

public sealed record SetAdminUserDisabledRequest(bool Disabled);

// Outcomes for the admin-privilege and disabled-state guarded mutations.
// The endpoint layer maps these to HTTP status/messages; the service never
// throws for these expected, guardable states.
public enum AdminSetAdminResult
{
    Ok,
    NotFound,
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

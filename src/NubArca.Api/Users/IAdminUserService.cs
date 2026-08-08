using NubArca.Api.Domain;

namespace NubArca.Api.Users;

public interface IAdminUserService
{
    Task<ListAdminUsersResponse> ListAsync(
        string? query,
        bool includeDisabled,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<AdminUserDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    // The admin detail view: the user alone. What they may do is the role's
    // permission set, read from the role catalogue.
    Task<AdminUserDetailDto?> GetDetailAsync(Guid userId, CancellationToken cancellationToken = default);

    // Takes the CALLER because the requested role is an escalation question:
    // nobody may create an account with authority they do not hold themselves.
    Task<(AdminSetRoleResult Result, AdminUserDto? User)> CreateAsync(
        Guid callerUserId,
        CreateAdminUserRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminUserDto?> UpdateAsync(
        Guid userId,
        UpdateAdminUserRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ResetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    Task<(AdminSetRoleResult Result, AdminUserDto? User)> SetRoleAsync(
        Guid callerUserId,
        Guid targetUserId,
        string? roleKey,
        CancellationToken cancellationToken = default);

    Task<(AdminSetDisabledResult Result, AdminUserDto? User)> SetDisabledAsync(
        Guid callerUserId,
        Guid targetUserId,
        bool disabled,
        CancellationToken cancellationToken = default);

    // The raw user row, for endpoints that need the email to send a recovery
    // message. Never surfaced to the browser — AdminUserDto is what leaves.
    Task<User?> FindAsync(Guid userId, CancellationToken cancellationToken = default);
}

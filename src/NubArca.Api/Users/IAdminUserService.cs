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

    Task<AdminUserDto> CreateAsync(CreateAdminUserRequest request, CancellationToken cancellationToken = default);

    Task<AdminUserDto?> UpdateAsync(
        Guid userId,
        UpdateAdminUserRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ResetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    Task<(AdminSetAdminResult Result, AdminUserDto? User)> SetAdminAsync(
        Guid callerUserId,
        Guid targetUserId,
        bool isAdmin,
        CancellationToken cancellationToken = default);

    Task<(AdminSetDisabledResult Result, AdminUserDto? User)> SetDisabledAsync(
        Guid callerUserId,
        Guid targetUserId,
        bool disabled,
        CancellationToken cancellationToken = default);
}

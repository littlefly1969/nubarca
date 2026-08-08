using NubArca.Api.Domain;

namespace NubArca.Api.Access;

public interface IUserPermissionService
{
    // Resolves the caller's effective permissions from CURRENT database state.
    // Scoped, and memoised for the lifetime of one request, so an endpoint that
    // checks two permissions still reads the user and their overrides once.
    Task<EffectivePermissions> GetEffectiveAsync(Guid userId, CancellationToken cancellationToken = default);

    // Same resolution for a user row already in hand (the cookie revalidator and
    // /api/auth/me both have one), without a second round trip for the user.
    Task<EffectivePermissions> GetEffectiveAsync(User user, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserPermissionOverride>> ListOverridesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    // Creates or updates the single override row for (user, permission).
    // Returns false when the key is not in the catalogue — the catalogue is
    // authoritative and an unknown key is never persisted.
    Task<bool> SetOverrideAsync(
        Guid userId, string permissionKey, PermissionEffect effect, CancellationToken cancellationToken = default);

    // Removes the override so the role baseline applies again. Returns false
    // when the key is unknown or no override existed.
    Task<bool> ClearOverrideAsync(
        Guid userId, string permissionKey, CancellationToken cancellationToken = default);
}

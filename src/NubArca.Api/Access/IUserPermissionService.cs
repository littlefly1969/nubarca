using NubArca.Api.Domain;

namespace NubArca.Api.Access;

public interface IUserPermissionService
{
    // Resolves the caller's effective permissions from CURRENT database state.
    // Scoped, and memoised for the lifetime of one request, so an endpoint that
    // checks two permissions still reads the user and their role once.
    Task<EffectivePermissions> GetEffectiveAsync(Guid userId, CancellationToken cancellationToken = default);

    // Same resolution for a user row already in hand (the cookie revalidator and
    // /api/auth/me both have one), without a second round trip for the user.
    Task<EffectivePermissions> GetEffectiveAsync(User user, CancellationToken cancellationToken = default);

    // Same resolution, but never from the memo. A background job runs many
    // slices inside one worker scope, so a job that re-checks a permission on
    // each slice must use this: a revocation between two slices has to be seen
    // by the second one.
    Task<EffectivePermissions> GetCurrentAsync(Guid userId, CancellationToken cancellationToken = default);
}

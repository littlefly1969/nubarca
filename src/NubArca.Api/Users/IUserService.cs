using NubArca.Api.Domain;

namespace NubArca.Api.Users;

public interface IUserService
{
    Task<User> CreateAsync(string email, string displayName, CancellationToken cancellationToken = default);

    Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    // Atomically toggles the admin marker on the user. Returns true when a
    // row was updated, false when the user does not exist. Idempotent —
    // setting an already-admin user to admin returns true and does nothing
    // observable (EF still issues the UPDATE, but the value is unchanged).
    Task<bool> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken = default);

    // Persists the user's UI language preference. `language` must be a supported
    // code (see UiLanguages); an unsupported/invalid value throws
    // ArgumentException before touching the row. Returns true when a row was
    // updated, false when the user does not exist.
    Task<bool> SetLanguageAsync(Guid userId, string language, CancellationToken cancellationToken = default);
}

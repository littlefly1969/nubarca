using NubArca.Api.Domain;

namespace NubArca.Api.Users;

public interface IUserService
{
    Task<User> CreateAsync(string email, string displayName, CancellationToken cancellationToken = default);

    Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    // Atomically sets the user's role. `roleKey` must be one of RoleKeys; an
    // unrecognised value throws before touching the row rather than writing a
    // role nothing can resolve. Returns true when a row was updated, false when
    // the user does not exist. This replaced SetAdminAsync outright — the role
    // IS the authorization source, so there is no second flag to keep in sync.
    Task<bool> SetRoleAsync(Guid userId, string roleKey, CancellationToken cancellationToken = default);

    // Persists the user's UI language preference. `language` must be a supported
    // code (see UiLanguages); an unsupported/invalid value throws
    // ArgumentException before touching the row. Returns true when a row was
    // updated, false when the user does not exist.
    Task<bool> SetLanguageAsync(Guid userId, string language, CancellationToken cancellationToken = default);

    // Stamps LastLoginAt. Called from the interactive login path only, so the
    // column keeps meaning "last sign-in" rather than "last request".
    Task<bool> RecordLoginAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken = default);

    // Updates the caller-editable profile fields. A null field means "leave
    // unchanged"; an empty (or whitespace) FirstName/LastName/TimeZone clears
    // it. Values are expected to be normalised by UserProfileFields first.
    Task<bool> UpdateProfileAsync(
        Guid userId,
        UserProfileUpdate update,
        CancellationToken cancellationToken = default);
}

// The profile fields a user may change about themselves, and the same set an
// administrator may edit on somebody else. Role, permissions, disabled state and
// email are deliberately NOT here — they are separate, guarded operations, so no
// profile write can reach them however the request is shaped.
public sealed record UserProfileUpdate(
    string? DisplayName = null,
    string? FirstName = null,
    string? LastName = null,
    string? Language = null,
    string? TimeZone = null,
    bool ClearFirstName = false,
    bool ClearLastName = false,
    bool ClearTimeZone = false);

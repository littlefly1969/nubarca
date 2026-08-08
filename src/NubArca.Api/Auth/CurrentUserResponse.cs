using NubArca.Api.Access;
using NubArca.Api.Domain;

namespace NubArca.Api.Auth;

// The authenticated user as the browser is allowed to see themselves.
//
// Never carries PasswordHash, a reset-token hash, SMTP configuration, or the
// raw override rows — `EffectivePermissions` is the resolved answer, which is
// all a client can act on anyway. The list is ordinal-sorted so two responses
// for the same authority are byte-identical.
//
// `IsAdmin` is a COMPUTED compatibility value (role == Administrator), kept
// only because the mobile client's own model still declares the field. It is
// derived on the way out and is not stored anywhere: the database source of
// truth is Role plus permissions, and nothing writes both.
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string? FirstName,
    string? LastName,
    bool IsAdmin,
    string Role,
    IReadOnlyList<string> EffectivePermissions,
    string Language,
    string? TimeZone,
    DateTime? LastLoginAt)
{
    public static CurrentUserResponse From(User user, EffectivePermissions permissions) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.FirstName,
        user.LastName,
        RolePermissionCatalog.IsAdministrator(user.RoleKey),
        user.RoleKey,
        permissions.ToSortedList(),
        user.UiLanguage,
        user.TimeZone,
        user.LastLoginAt);
}

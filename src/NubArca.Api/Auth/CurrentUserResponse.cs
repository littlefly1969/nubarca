namespace NubArca.Api.Auth;

// Safe-to-serialise projection of a User. Deliberately omits PasswordHash,
// DisabledAt, CreatedAt — none of those belong in a response body. The
// `IsAdmin` flag (slice 47) lets the frontend decide whether to show the
// admin nav; the backend remains the authoritative gate via the `Admin`
// policy on `/api/admin/*`. `Language` is the persisted UI language
// preference ("it" | "en") so the app can localize on login/session restore.
public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, bool IsAdmin, string Language);

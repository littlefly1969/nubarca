namespace NubArca.Api.Auth;

// Self-service profile edit. The shape is the guard: there is no role,
// permissions, disabled or email field to send, so no amount of crafting a
// request body reaches them through this endpoint.
public sealed record UpdateMyProfileRequest(
    string? DisplayName,
    string? FirstName,
    string? LastName,
    string? Language,
    string? TimeZone);

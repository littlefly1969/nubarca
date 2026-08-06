namespace NubArca.Api.Auth;

// Body of PUT /api/auth/me/language. `Language` must be a supported UI language
// code (see UiLanguages); anything else is rejected with a 400 before any write.
public sealed record UpdateLanguageRequest(string? Language);

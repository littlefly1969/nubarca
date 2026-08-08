namespace NubArca.Api.Auth.Recovery;

public sealed record PasswordRecoveryRequest(string? Email);

// The token arrives in the JSON BODY, never in the URL: the frontend reads it
// from the fragment and posts it here, so it stays out of proxy access logs.
public sealed record PasswordResetRequest(string? Token, string? NewPassword);

// The only thing the public status endpoint says. No account information, no
// configured host, no from-address — just whether the forgot-password form
// should offer to send anything.
public sealed record PasswordRecoveryStatusResponse(bool Enabled);

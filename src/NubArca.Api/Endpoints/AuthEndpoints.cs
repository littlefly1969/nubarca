using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Users;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, auth requirements, status codes, and
// audit/cookie/password behavior are unchanged from the original inline
// mappings.
public static class AuthEndpoints
{
    // Mirrors the top-level `LoginRateLimitPolicy` constant still defined in
    // Program.cs for the (untouched) rate limiter policy registration —
    // duplicated here only as the literal policy name, not a new policy.
    private const string LoginRateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            LoginRequest? body,
            HttpContext httpContext,
            [FromServices] IAuthService auth,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var attemptedEmail = NormalizeForAudit(body?.Email);

            var user = await auth.AuthenticateAsync(body?.Email, body?.Password, cancellationToken);
            if (user is null)
            {
                await audit.LogAsync(
                    userId: null,
                    action: AuditActions.LoginFailure,
                    entityType: AuditEntityTypes.User,
                    entityId: null,
                    ipAddress: ip,
                    metadata: attemptedEmail is null ? null : new { email = attemptedEmail },
                    cancellationToken: cancellationToken);
                return Results.Unauthorized();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Name, user.DisplayName),
            };
            if (user.IsAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, CookieSessionValidator.AdminRole));
            }
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            await audit.LogAsync(
                userId: user.Id,
                action: AuditActions.LoginSuccess,
                entityType: AuditEntityTypes.User,
                entityId: user.Id,
                ipAddress: ip,
                metadata: new { email = user.Email },
                cancellationToken: cancellationToken);

            return Results.Ok(new CurrentUserResponse(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.UiLanguage));
        }).WithName("Login").RequireRateLimiting(LoginRateLimitPolicy);

        app.MapPost("/api/auth/logout", async (
            HttpContext httpContext,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId();
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            if (userId is Guid id)
            {
                await audit.LogAsync(
                    userId: id,
                    action: AuditActions.Logout,
                    entityType: AuditEntityTypes.User,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);
            }

            return Results.NoContent();
        }).WithName("Logout");

        app.MapGet("/api/auth/me", async (
            HttpContext httpContext,
            [FromServices] IUserService users,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.GetCurrentUserId() is not Guid userId)
            {
                return Results.Unauthorized();
            }

            var user = await users.GetByIdAsync(userId, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new CurrentUserResponse(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.UiLanguage));
        }).WithName("Me").RequireAuthorization();

        // Update ONLY the caller's own UI language preference. Cookie session, no
        // bearer/token auth. Unsupported/invalid codes are rejected (400) before any
        // write; the user id comes from the session, so a user can never change another
        // user's language. Returns the refreshed CurrentUserResponse.
        app.MapPut("/api/auth/me/language", async (
            UpdateLanguageRequest? body,
            HttpContext httpContext,
            [FromServices] IUserService users,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.GetCurrentUserId() is not Guid userId)
            {
                return Results.Unauthorized();
            }

            if (!UiLanguages.TryNormalize(body?.Language, out var language))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["language"] = new[] { "Unsupported language. Supported values: it, en." },
                });
            }

            var updated = await users.SetLanguageAsync(userId, language, cancellationToken);
            if (!updated)
            {
                return Results.Unauthorized();
            }

            var user = await users.GetByIdAsync(userId, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new CurrentUserResponse(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.UiLanguage));
        }).WithName("UpdateMyLanguage").RequireAuthorization();

        // Self-service password change. Requires the caller's CURRENT password —
        // this is a change, not an admin reset. A user with no PasswordHash set
        // (e.g. created by an admin without an initial password) cannot bootstrap
        // one here: an admin must set the first password via
        // POST /api/admin/users/{id}/password. That keeps "prove you know the
        // current password" simple and avoids an empty-current-password bypass.
        app.MapPost("/api/auth/me/password", async (
            ChangeMyPasswordRequest? body,
            HttpContext httpContext,
            [FromServices] IUserService users,
            [FromServices] IAuthService auth,
            [FromServices] IPasswordHasher<User> hasher,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.GetCurrentUserId() is not Guid userId)
            {
                return Results.Unauthorized();
            }

            var user = await users.GetByIdAsync(userId, cancellationToken);
            if (user is null || user.DisabledAt is not null)
            {
                return Results.Unauthorized();
            }

            if (user.PasswordHash is null)
            {
                return Results.Conflict(new
                {
                    error = "No password is set for this account. Ask an administrator to set one.",
                });
            }

            if (string.IsNullOrEmpty(body?.CurrentPassword)
                || hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword)
                    == PasswordVerificationResult.Failed)
            {
                return Results.BadRequest(new { error = "Current password is incorrect." });
            }

            if (!PasswordPolicy.TryValidate(body.NewPassword, out var policyError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = new[] { policyError! },
                });
            }

            if (string.Equals(body.CurrentPassword, body.NewPassword, StringComparison.Ordinal))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = new[] { "New password must be different from the current password." },
                });
            }

            await auth.SetPasswordAsync(userId, body.NewPassword!, cancellationToken);

            await audit.LogAsync(
                userId: userId,
                action: AuditActions.AuthPasswordChange,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.NoContent();
        }).WithName("ChangeMyPassword").RequireAuthorization();

        return app;
    }

    private static string? NormalizeForAudit(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }
        var trimmed = email.Trim();
        return trimmed.Length > 320 ? trimmed[..320] : trimmed;
    }
}

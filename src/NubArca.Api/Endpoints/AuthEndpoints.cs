using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Users;

namespace NubArca.Api.Endpoints;

// Authentication, the caller's own profile, and the public password-recovery
// flow. Route paths, HTTP methods, endpoint names, auth requirements, status
// codes and audit/cookie behavior of the pre-existing endpoints are unchanged;
// the identity slice added the profile fields, the role/permission projection
// and the two recovery endpoints.
public static class AuthEndpoints
{
    // Mirrors the top-level `LoginRateLimitPolicy` constant still defined in
    // Program.cs for the (untouched) rate limiter policy registration —
    // duplicated here only as the literal policy name, not a new policy.
    private const string LoginRateLimitPolicy = "login";

    // Per-IP limiter for the recovery request endpoint. The second axis (per
    // normalized email) lives in PasswordRecoveryThrottle; one alone is not
    // enough, because they stop different attacks.
    private const string PasswordRecoveryRateLimitPolicy = "password-recovery";

    // The ONE public answer to a recovery request. Identical for a known
    // address, an unknown address, a disabled account and a delivery failure.
    private const string RecoveryAcceptedMessage =
        "If the address belongs to an active account, an email with instructions has been sent.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            LoginRequest? body,
            HttpContext httpContext,
            [FromServices] IAuthService auth,
            [FromServices] IUserService users,
            [FromServices] IUserPermissionService permissions,
            [FromServices] IAuditLogger audit,
            [FromServices] TimeProvider clock,
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

            // This is the ONLY place LastLoginAt moves. A background request or
            // a token validation must never touch it, or the column stops
            // meaning "last sign-in".
            var now = clock.GetUtcNow().UtcDateTime;
            await users.RecordLoginAsync(user.Id, now, cancellationToken);
            user.LastLoginAt = now;

            var identity = new ClaimsIdentity(
                CookieSessionValidator.BuildClaims(user),
                CookieAuthenticationDefaults.AuthenticationScheme);
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

            var effective = await permissions.GetEffectiveAsync(user, cancellationToken);
            return Results.Ok(CurrentUserResponse.From(user, effective));
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
            [FromServices] IUserPermissionService permissions,
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

            var effective = await permissions.GetEffectiveAsync(user, cancellationToken);
            return Results.Ok(CurrentUserResponse.From(user, effective));
        }).WithName("Me").RequireAuthorization();

        // Update ONLY the caller's own UI language preference. Cookie session, no
        // bearer/token auth. Unsupported/invalid codes are rejected (400) before any
        // write; the user id comes from the session, so a user can never change another
        // user's language. Returns the refreshed CurrentUserResponse.
        app.MapPut("/api/auth/me/language", async (
            UpdateLanguageRequest? body,
            HttpContext httpContext,
            [FromServices] IUserService users,
            [FromServices] IUserPermissionService permissions,
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

            var effective = await permissions.GetEffectiveAsync(user, cancellationToken);
            return Results.Ok(CurrentUserResponse.From(user, effective));
        }).WithName("UpdateMyLanguage").RequireAuthorization();

        // Self-service PROFILE edit: display name, first/last name, language and
        // time zone. Role, permissions, disabled state and email are absent from
        // the request record entirely, so this endpoint has no path to them
        // however the body is crafted. Email stays the login and recovery
        // identity and is changed by neither this nor the admin editor —
        // changing it would need a verification workflow of its own.
        app.MapPut("/api/auth/me/profile", async (
            UpdateMyProfileRequest? body,
            HttpContext httpContext,
            [FromServices] IUserService users,
            [FromServices] IUserPermissionService permissions,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (httpContext.GetCurrentUserId() is not Guid userId)
            {
                return Results.Unauthorized();
            }

            if (!TryBuildProfileUpdate(body, out var update, out var problems))
            {
                return Results.ValidationProblem(problems!);
            }

            var updated = await users.UpdateProfileAsync(userId, update, cancellationToken);
            if (!updated)
            {
                return Results.Unauthorized();
            }

            var user = await users.GetByIdAsync(userId, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await audit.LogAsync(
                userId: userId,
                action: AuditActions.AuthProfileUpdate,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            var effective = await permissions.GetEffectiveAsync(user, cancellationToken);
            return Results.Ok(CurrentUserResponse.From(user, effective));
        }).WithName("UpdateMyProfile").RequireAuthorization();

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

            // Bumps SecurityVersion, so every OTHER session opened with the old
            // password is dead on its next request, and invalidates outstanding
            // recovery links.
            var securityVersion = await auth.SetPasswordAsync(userId, body.NewPassword!, cancellationToken);

            // …including this one, which is why the caller is immediately
            // re-issued a cookie at the new version. Changing your own password
            // signs out your other devices, not the browser you did it from.
            user.SecurityVersion = securityVersion;
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(
                    CookieSessionValidator.BuildClaims(user),
                    CookieAuthenticationDefaults.AuthenticationScheme)));

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

        MapPasswordRecoveryEndpoints(app);

        return app;
    }

    private static void MapPasswordRecoveryEndpoints(IEndpointRouteBuilder app)
    {
        // PUBLIC. Says only whether the operator has configured email recovery,
        // so the forgot-password page can either offer the form or explain that
        // the administrator must reset the password manually. No account
        // information of any kind passes through here.
        app.MapGet("/api/auth/password-recovery/status", (
            [FromServices] IPasswordRecoveryService recovery) =>
            Results.Ok(new PasswordRecoveryStatusResponse(recovery.IsEnabled)))
            .WithName("PasswordRecoveryStatus");

        // PUBLIC. Always 202 with the same message — for a real address, an
        // unknown one, a disabled account, an account with no password, and a
        // send that failed. The handler cannot leak the difference because the
        // service returns nothing to branch on.
        app.MapPost("/api/auth/password-recovery/request", async (
            PasswordRecoveryRequest? body,
            [FromServices] IPasswordRecoveryService recovery,
            CancellationToken cancellationToken) =>
        {
            if (!recovery.TryConsumeEmailQuota(body?.Email))
            {
                // Counted per submitted address regardless of whether it exists,
                // so a 429 tells an enumerator nothing about the account.
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            await recovery.RequestAsync(body?.Email, cancellationToken);
            return Results.Accepted(value: new { message = RecoveryAcceptedMessage });
        }).WithName("PasswordRecoveryRequest")
          .RequireRateLimiting(PasswordRecoveryRateLimitPolicy);

        // PUBLIC. Consumes the token from the BODY and sets the new password.
        // No sign-in happens: a reset returns the user to the login form, and
        // every pre-reset session is already invalid by the time this responds.
        app.MapPost("/api/auth/password-recovery/reset", async (
            PasswordResetRequest? body,
            [FromServices] IPasswordRecoveryService recovery,
            CancellationToken cancellationToken) =>
        {
            var result = await recovery.ResetAsync(body?.Token, body?.NewPassword, cancellationToken);
            return result switch
            {
                PasswordResetResult.Ok => Results.NoContent(),
                PasswordResetResult.WeakPassword => Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["newPassword"] = new[] { PasswordPolicy.ErrorMessage },
                    }),
                // Expired, spent, unknown and malformed all land here with one
                // message. Distinguishing them would confirm that a token once
                // existed.
                _ => Results.BadRequest(new { error = "This password reset link is no longer valid." }),
            };
        }).WithName("PasswordRecoveryReset")
          .RequireRateLimiting(PasswordRecoveryRateLimitPolicy);
    }

    // Shared by the self-service endpoint and the admin editor so both accept
    // exactly the same values. An empty string clears an optional field; a null
    // leaves it untouched.
    internal static bool TryBuildProfileUpdate(
        UpdateMyProfileRequest? body,
        out UserProfileUpdate update,
        out Dictionary<string, string[]>? problems)
    {
        update = new UserProfileUpdate();
        problems = null;
        if (body is null)
        {
            return true;
        }

        var errors = new Dictionary<string, string[]>();

        if (!UserProfileFields.TryNormalizeDisplayName(body.DisplayName, out var displayName, out var displayError))
        {
            errors["displayName"] = [displayError!];
        }
        if (!UserProfileFields.TryNormalizeOptionalName(body.FirstName, out var firstName, out var firstError))
        {
            errors["firstName"] = [firstError!];
        }
        if (!UserProfileFields.TryNormalizeOptionalName(body.LastName, out var lastName, out var lastError))
        {
            errors["lastName"] = [lastError!];
        }
        if (!UserProfileFields.TryNormalizeTimeZone(body.TimeZone, out var timeZone, out var zoneError))
        {
            errors["timeZone"] = [zoneError!];
        }
        if (body.Language is not null && !UiLanguages.TryNormalize(body.Language, out _))
        {
            errors["language"] = ["Unsupported language. Supported values: it, en."];
        }

        if (errors.Count > 0)
        {
            problems = errors;
            return false;
        }

        update = new UserProfileUpdate(
            DisplayName: displayName,
            FirstName: firstName,
            LastName: lastName,
            Language: body.Language,
            TimeZone: timeZone,
            ClearFirstName: body.FirstName is not null && firstName is null,
            ClearLastName: body.LastName is not null && lastName is null,
            ClearTimeZone: body.TimeZone is not null && timeZone is null);
        return true;
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

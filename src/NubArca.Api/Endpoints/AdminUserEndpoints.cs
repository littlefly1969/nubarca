using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Http;
using NubArca.Api.Users;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, admin authorization, status codes, and
// audit/safety behavior are unchanged from the original inline mappings.
//
// All endpoints are admin-gated (401 unauth / 403 non-admin). Responses are
// always the safe AdminUserDto projection — PasswordHash is never returned.
// Grant/revoke-admin and enable/disable guard against removing the last
// active admin and against an admin acting on their own account (self-
// demotion / self-disable are outright disallowed, not just confirmed).
public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/users", async (
            [FromQuery] string? q,
            [FromQuery] bool? includeDisabled,
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            [FromServices] IAdminUserService adminUsers,
            CancellationToken cancellationToken) =>
        {
            var result = await adminUsers.ListAsync(
                q,
                includeDisabled ?? false,
                limit ?? 0,
                offset ?? 0,
                cancellationToken);
            return Results.Ok(result);
        }).WithName("AdminUsersList").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/users", async (
            CreateAdminUserRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Email) || string.IsNullOrWhiteSpace(body.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["email"] = new[] { "Email and display name are required." },
                });
            }

            if (!PasswordPolicy.TryValidate(body.Password, out var policyError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = new[] { policyError! },
                });
            }

            try
            {
                var created = await adminUsers.CreateAsync(body, cancellationToken);

                await audit.LogAsync(
                    userId: httpContext.GetCurrentUserId(),
                    action: AuditActions.AdminUserCreate,
                    entityType: AuditEntityTypes.User,
                    entityId: created.Id,
                    ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    metadata: null,
                    cancellationToken: cancellationToken);

                return Results.Created($"/api/admin/users/{created.Id}", created);
            }
            catch (UserAlreadyExistsException)
            {
                return Results.Conflict(new { error = "A user with this email already exists." });
            }
        }).WithName("AdminUsersCreate").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/users/{userId:guid}", async (
            Guid userId,
            [FromServices] IAdminUserService adminUsers,
            CancellationToken cancellationToken) =>
        {
            var user = await adminUsers.GetAsync(userId, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).WithName("AdminUsersGet").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPut("/api/admin/users/{userId:guid}", async (
            Guid userId,
            UpdateAdminUserRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var updated = await adminUsers.UpdateAsync(
                userId,
                body ?? new UpdateAdminUserRequest(null, null),
                cancellationToken);
            if (updated is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminUserUpdate,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.Ok(updated);
        }).WithName("AdminUsersUpdate").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/users/{userId:guid}/password", async (
            Guid userId,
            SetAdminUserPasswordRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (!PasswordPolicy.TryValidate(body?.Password, out var policyError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = new[] { policyError! },
                });
            }

            var updated = await adminUsers.ResetPasswordAsync(userId, body!.Password!, cancellationToken);
            if (!updated)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminUserPasswordReset,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.NoContent();
        }).WithName("AdminUsersResetPassword").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPut("/api/admin/users/{userId:guid}/admin", async (
            Guid userId,
            SetAdminUserAdminRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var callerUserId = httpContext.GetCurrentUserId()!.Value;
            var (result, user) = await adminUsers.SetAdminAsync(
                callerUserId, userId, body?.IsAdmin ?? false, cancellationToken);

            switch (result)
            {
                case AdminSetAdminResult.NotFound:
                    return Results.NotFound();
                case AdminSetAdminResult.SelfDemotion:
                    return Results.Conflict(new { error = "You cannot remove your own admin privilege." });
                case AdminSetAdminResult.LastAdmin:
                    return Results.Conflict(new { error = "You cannot remove admin from the last administrator." });
            }

            await audit.LogAsync(
                userId: callerUserId,
                action: body!.IsAdmin ? AuditActions.AdminUserAdminGrant : AuditActions.AdminUserAdminRevoke,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.Ok(user);
        }).WithName("AdminUsersSetAdmin").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPut("/api/admin/users/{userId:guid}/disabled", async (
            Guid userId,
            SetAdminUserDisabledRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var callerUserId = httpContext.GetCurrentUserId()!.Value;
            var (result, user) = await adminUsers.SetDisabledAsync(
                callerUserId, userId, body?.Disabled ?? false, cancellationToken);

            switch (result)
            {
                case AdminSetDisabledResult.NotFound:
                    return Results.NotFound();
                case AdminSetDisabledResult.SelfDisable:
                    return Results.Conflict(new { error = "You cannot disable your own account." });
                case AdminSetDisabledResult.LastAdmin:
                    return Results.Conflict(new { error = "You cannot disable the last administrator." });
            }

            await audit.LogAsync(
                userId: callerUserId,
                action: body!.Disabled ? AuditActions.AdminUserDisable : AuditActions.AdminUserEnable,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.Ok(user);
        }).WithName("AdminUsersSetDisabled").RequireAuthorization(CookieSessionValidator.AdminRole);

        return app;
    }
}

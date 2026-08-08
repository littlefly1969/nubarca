using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Http;
using NubArca.Api.Users;

namespace NubArca.Api.Endpoints;

// Admin user management, gated on `admin.users.manage` specifically. Holding a
// different administrative permission (import, jobs, dashboard, roles) does NOT
// open this API — that separation is the reason there are five admin
// permissions rather than one.
//
// Responses are always the safe AdminUserDto / AdminUserDetailDto projections:
// no PasswordHash, no token hashes, no SecurityVersion, no storage internals.
// Role changes and enable/disable guard against removing the last active
// administrator and against an administrator acting on their own account
// (self-demotion / self-disable are outright disallowed, not just confirmed),
// and role assignment additionally refuses to hand out authority the caller
// does not hold.
//
// There is no per-user permission endpoint. A user's authority IS their role's
// permission set: to change what somebody may do, either move them to another
// role or edit the role — and editing roles is a different permission.
public static class AdminUserEndpoints
{
    private const string EscalationMessage =
        "You cannot assign a role that grants more than your own permissions.";

    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        // The catalogue itself, so the role editor renders grouping, the
        // Laboratory dependency and the Administrator-only marker from the
        // server's list rather than from a copy the frontend has to keep in
        // step. Nothing here is user-specific. Readable by either administrative
        // editor: the Users page needs it to explain a role before it is
        // assigned.
        app.MapGet("/api/admin/permissions", () =>
            Results.Ok(new PermissionCatalogResponse(
                PermissionCatalog.All
                    .Select(p => new PermissionCatalogEntryDto(
                        p.Key, p.Group, p.Administrative, p.Parent, !p.AdministratorOnly))
                    .ToList())))
            .WithName("AdminPermissionCatalog").RequireRolesRead();

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
        }).WithName("AdminUsersList").RequirePermission(Permissions.AdminUsersManage);

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
                var callerUserId = httpContext.GetCurrentUserId()!.Value;
                var (result, created) = await adminUsers.CreateAsync(callerUserId, body, cancellationToken);

                switch (result)
                {
                    case AdminSetRoleResult.UnknownRole:
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["role"] = new[] { "Unknown role." },
                        });
                    case AdminSetRoleResult.Escalation:
                        return Results.Json(
                            new { error = EscalationMessage },
                            statusCode: StatusCodes.Status403Forbidden);
                }

                await audit.LogAsync(
                    userId: callerUserId,
                    action: AuditActions.AdminUserCreate,
                    entityType: AuditEntityTypes.User,
                    entityId: created!.Id,
                    ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    metadata: new { role = created.Role },
                    cancellationToken: cancellationToken);

                return Results.Created($"/api/admin/users/{created.Id}", created);
            }
            catch (UserAlreadyExistsException)
            {
                return Results.Conflict(new { error = "A user with this email already exists." });
            }
        }).WithName("AdminUsersCreate").RequirePermission(Permissions.AdminUsersManage);

        app.MapGet("/api/admin/users/{userId:guid}", async (
            Guid userId,
            [FromServices] IAdminUserService adminUsers,
            CancellationToken cancellationToken) =>
        {
            var user = await adminUsers.GetDetailAsync(userId, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).WithName("AdminUsersGet").RequirePermission(Permissions.AdminUsersManage);

        app.MapPut("/api/admin/users/{userId:guid}", async (
            Guid userId,
            UpdateAdminUserRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var request = body ?? new UpdateAdminUserRequest(null, null, null, null, null);

            // Validated with the same helper the self-service endpoint uses, so
            // the two surfaces cannot disagree about what a valid time zone or
            // display name is.
            if (!AuthEndpoints.TryBuildProfileUpdate(
                    new NubArca.Api.Auth.UpdateMyProfileRequest(
                        request.DisplayName, request.FirstName, request.LastName,
                        request.Language, request.TimeZone),
                    out _,
                    out var problems))
            {
                return Results.ValidationProblem(problems!);
            }

            var updated = await adminUsers.UpdateAsync(userId, request, cancellationToken);
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
        }).WithName("AdminUsersUpdate").RequirePermission(Permissions.AdminUsersManage);

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
        }).WithName("AdminUsersResetPassword").RequirePermission(Permissions.AdminUsersManage);

        // Sends the ordinary recovery email to a specific user. Unlike the
        // public request endpoint this one is allowed to be explicit about
        // whether it worked: the caller can already see the account, so there is
        // no existence to protect — only the token, which never appears in the
        // response, the audit metadata or a log line.
        app.MapPost("/api/admin/users/{userId:guid}/password-reset-email", async (
            Guid userId,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IPasswordRecoveryService recovery,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var user = await adminUsers.FindAsync(userId, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!recovery.IsEnabled)
            {
                return Results.Conflict(new
                {
                    error = "Email recovery is not configured on this installation.",
                });
            }

            if (user.DisabledAt is not null)
            {
                return Results.Conflict(new
                {
                    error = "This account is disabled. Enable it before sending a recovery email.",
                });
            }

            await recovery.RequestAsync(user.Email, cancellationToken);

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminUserPasswordResetEmail,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.Accepted();
        }).WithName("AdminUsersSendPasswordResetEmail").RequirePermission(Permissions.AdminUsersManage);

        app.MapPut("/api/admin/users/{userId:guid}/role", async (
            Guid userId,
            SetAdminUserRoleRequest? body,
            HttpContext httpContext,
            [FromServices] IAdminUserService adminUsers,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var callerUserId = httpContext.GetCurrentUserId()!.Value;
            var (result, user) = await adminUsers.SetRoleAsync(
                callerUserId, userId, body?.Role, cancellationToken);

            switch (result)
            {
                case AdminSetRoleResult.NotFound:
                    return Results.NotFound();
                case AdminSetRoleResult.UnknownRole:
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["role"] = new[] { "Unknown role." },
                    });
                case AdminSetRoleResult.SelfDemotion:
                    return Results.Conflict(new { error = "You cannot remove your own administrator role." });
                case AdminSetRoleResult.LastAdmin:
                    return Results.Conflict(new { error = "You cannot demote the last administrator." });
                case AdminSetRoleResult.Escalation:
                    // 403, not 409: this is an authority the caller does not
                    // have, not a state the installation refuses to enter.
                    return Results.Json(
                        new { error = EscalationMessage },
                        statusCode: StatusCodes.Status403Forbidden);
            }

            await audit.LogAsync(
                userId: callerUserId,
                action: AuditActions.AdminUserRoleChange,
                entityType: AuditEntityTypes.User,
                entityId: userId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { role = user!.Role },
                cancellationToken: cancellationToken);

            return Results.Ok(user);
        }).WithName("AdminUsersSetRole").RequirePermission(Permissions.AdminUsersManage);

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
        }).WithName("AdminUsersSetDisabled").RequirePermission(Permissions.AdminUsersManage);

        return app;
    }
}

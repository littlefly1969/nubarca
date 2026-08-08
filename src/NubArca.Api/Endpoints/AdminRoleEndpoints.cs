using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Roles as first-class objects.
//
// READS are open to either administrative editor — the Users page has to be
// able to explain what a role means before an operator assigns it — while every
// MUTATION requires `admin.roles.manage`, which the catalogue makes
// Administrator-only. A user manager can therefore see exactly what a role
// contains and can never edit it, and cannot mint one that would grant
// themselves more.
//
// An update carries the role's whole permission set and is applied in one
// transaction. That is deliberate: a request per checkbox would leave a role
// half-edited, live, for every user assigned to it.
public static class AdminRoleEndpoints
{
    public static IEndpointRouteBuilder MapAdminRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/roles", async (
            [FromServices] IRoleService roles,
            CancellationToken cancellationToken) =>
            Results.Ok(new ListRolesResponse(await roles.ListAsync(cancellationToken))))
            .WithName("AdminRolesList").RequireRolesRead();

        app.MapGet("/api/admin/roles/{roleKey}", async (
            string roleKey,
            [FromServices] IRoleService roles,
            CancellationToken cancellationToken) =>
        {
            var role = await roles.GetAsync(roleKey, cancellationToken);
            return role is null ? Results.NotFound() : Results.Ok(role);
        }).WithName("AdminRolesGet").RequireRolesRead();

        app.MapPost("/api/admin/roles", async (
            CreateRoleRequest? body,
            HttpContext httpContext,
            [FromServices] IRoleService roles,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var (result, role) = await roles.CreateAsync(
                body ?? new CreateRoleRequest(null, null, null), cancellationToken);
            if (result != RoleMutationResult.Ok)
            {
                return Problem(result);
            }

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminRoleCreate,
                entityType: AuditEntityTypes.Role,
                entityId: null,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { role = role!.Key, permissions = role.Permissions.Count },
                cancellationToken: cancellationToken);

            return Results.Created($"/api/admin/roles/{role.Key}", role);
        }).WithName("AdminRolesCreate").RequirePermission(Permissions.AdminRolesManage);

        app.MapPut("/api/admin/roles/{roleKey}", async (
            string roleKey,
            UpdateRoleRequest? body,
            HttpContext httpContext,
            [FromServices] IRoleService roles,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var (result, role) = await roles.UpdateAsync(
                roleKey, body ?? new UpdateRoleRequest(null, null, null, null), cancellationToken);
            if (result != RoleMutationResult.Ok)
            {
                return Problem(result);
            }

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminRoleUpdate,
                entityType: AuditEntityTypes.Role,
                entityId: null,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                // Counts, never the key list: an audit trail records that access
                // changed and for how many accounts, not a copy of the policy.
                metadata: new { role = role!.Key, permissions = role.Permissions.Count, users = role.UserCount },
                cancellationToken: cancellationToken);

            return Results.Ok(role);
        }).WithName("AdminRolesUpdate").RequirePermission(Permissions.AdminRolesManage);

        app.MapDelete("/api/admin/roles/{roleKey}", async (
            string roleKey,
            HttpContext httpContext,
            [FromServices] IRoleService roles,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var result = await roles.DeleteAsync(roleKey, cancellationToken);
            if (result != RoleMutationResult.Ok)
            {
                return Problem(result);
            }

            await audit.LogAsync(
                userId: httpContext.GetCurrentUserId(),
                action: AuditActions.AdminRoleDelete,
                entityType: AuditEntityTypes.Role,
                entityId: null,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { role = roleKey },
                cancellationToken: cancellationToken);

            return Results.NoContent();
        }).WithName("AdminRolesDelete").RequirePermission(Permissions.AdminRolesManage);

        return app;
    }

    // One mapping from service outcome to HTTP, so the four handlers cannot
    // answer the same refusal differently.
    private static IResult Problem(RoleMutationResult result) => result switch
    {
        RoleMutationResult.NotFound => Results.NotFound(),
        RoleMutationResult.InvalidName => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["name"] = ["A role needs a name of at most 64 characters."],
        }),
        RoleMutationResult.UnknownPermission => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            // The catalogue is authoritative: an unknown key is rejected rather
            // than persisted, so a crafted request cannot store an arbitrary
            // permission string.
            ["permissions"] = ["Unknown permission."],
        }),
        RoleMutationResult.AdministratorOnlyPermission => Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["permissions"] = ["Role management can only be held through the Administrator role."],
            }),
        RoleMutationResult.MissingParentPermission => Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["permissions"] = ["A Laboratory section also requires Laboratory access."],
            }),
        RoleMutationResult.SystemRoleProtected => Results.Conflict(new
        {
            error = "This is a system role and cannot be edited or deleted.",
        }),
        RoleMutationResult.RoleInUse => Results.Conflict(new
        {
            error = "Reassign the users on this role before deleting it.",
        }),
        RoleMutationResult.VersionConflict => Results.Conflict(new
        {
            error = "This role was changed by somebody else. Reload and try again.",
        }),
        _ => Results.Problem("Unexpected role mutation result."),
    };
}

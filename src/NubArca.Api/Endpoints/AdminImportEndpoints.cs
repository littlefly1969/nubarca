using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Admin;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, admin authorization, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Slice 81: admin-only server-side directory import. All endpoints are
// admin-gated (401 unauth / 403 non-admin). The feature is OFF by default;
// action endpoints reject with 409 when disabled/unconfigured. No absolute
// physical paths, storage keys, or internals cross the boundary.
public static class AdminImportEndpoints
{
    public static IEndpointRouteBuilder MapAdminImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/import/roots", (
            [FromServices] IAdminImportService import) =>
        {
            return Results.Ok(import.GetRoots());
        }).WithName("AdminImportRoots").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/import/browse", async (
            [FromQuery] string rootId,
            [FromQuery] string? relativePath,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await import.BrowseAsync(rootId, relativePath, cancellationToken);
                return Results.Ok(result);
            }
            catch (AdminImportUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (AdminImportValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("AdminImportBrowse").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/import/users", async (
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            var users = await import.GetSelectableUsersAsync(cancellationToken);
            return Results.Ok(users);
        }).WithName("AdminImportUsers").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/import/destination-folders", async (
            [FromQuery] Guid userId,
            [FromQuery] Guid? parentFolderId,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await import.GetDestinationFoldersAsync(userId, parentFolderId, cancellationToken);
                return Results.Ok(result);
            }
            catch (AdminImportValidationException ex) { return Results.NotFound(new { error = ex.Message }); }
        }).WithName("AdminImportDestinationFolders").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/import/preview", async (
            [FromBody] AdminImportPreviewRequest request,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await import.PreviewAsync(request, cancellationToken);
                return Results.Ok(result);
            }
            catch (AdminImportUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (AdminImportValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("AdminImportPreview").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/import/run", async (
            [FromBody] AdminImportRunRequest request,
            HttpContext httpContext,
            [FromServices] IAdminImportService import,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var adminUserId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await import.StartRunAsync(adminUserId, request, cancellationToken);
                await audit.LogAsync(
                    adminUserId,
                    AuditActions.AdminImportStart,
                    AuditEntityTypes.AdminImport,
                    result.ImportRunId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { result.JobId, request.TargetUserId },
                    cancellationToken);
                return Results.Ok(result);
            }
            catch (AdminImportUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (AdminImportValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("AdminImportRun").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/import/runs", async (
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            var result = await import.ListRunsAsync(limit ?? 25, offset ?? 0, cancellationToken);
            return Results.Ok(result);
        }).WithName("AdminImportRuns").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/import/runs/{importRunId:guid}", async (
            Guid importRunId,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            var status = await import.GetRunStatusAsync(importRunId, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("AdminImportRunStatus").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/import/runs/{importRunId:guid}/cancel", async (
            Guid importRunId,
            HttpContext httpContext,
            [FromServices] IAdminImportService import,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var result = await import.RequestCancelAsync(importRunId, cancellationToken);
            if (result is null) return Results.NotFound();
            if (result.CancellationRequested)
            {
                await audit.LogAsync(
                    httpContext.GetCurrentUserId()!.Value,
                    AuditActions.AdminImportCancel,
                    AuditEntityTypes.AdminImport,
                    importRunId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    null,
                    cancellationToken);
            }
            return Results.Ok(result);
        }).WithName("AdminImportRunCancel").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Slice 92: paginated, safe view over a run's persisted import items (the
        // manifest). Filterable by item status; bounded page size; relative paths +
        // stable categories only — never FileItemId, absolute paths, or internals.
        app.MapGet("/api/admin/import/runs/{importRunId:guid}/items", async (
            Guid importRunId,
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] IAdminImportService import,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await import.GetRunItemsAsync(
                    importRunId, status, page ?? 1, pageSize ?? 50, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (AdminImportValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("AdminImportRunItems").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Slice 92: enqueue the idempotent media-derivatives backfill job so a
        // completed/partial run's missing thumbnails/previews/posters are generated
        // in the background (the same job the run enqueues automatically at the end).
        app.MapPost("/api/admin/import/runs/{importRunId:guid}/enqueue-derivatives", async (
            Guid importRunId,
            HttpContext httpContext,
            [FromServices] IAdminImportService import,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var result = await import.EnqueueMissingDerivativesAsync(importRunId, cancellationToken);
            if (result is null) return Results.NotFound();
            await audit.LogAsync(
                httpContext.GetCurrentUserId()!.Value,
                AuditActions.AdminImportEnqueueDerivatives,
                AuditEntityTypes.AdminImport,
                importRunId,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                new { result.JobId },
                cancellationToken);
            return Results.Ok(result);
        }).WithName("AdminImportRunEnqueueDerivatives").RequireAuthorization(CookieSessionValidator.AdminRole);

        return app;
    }
}

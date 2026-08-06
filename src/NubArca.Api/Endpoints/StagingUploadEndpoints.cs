using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Http;
using NubArca.Api.Uploads;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization, status codes, DTOs, and
// audit behavior are unchanged from the original inline mappings.
//
// Slice 93: web remote-staging upload. Resumable browser chunk uploads into
// temporary per-session staging directories, verified against the persisted
// manifest/chunk state, then handed off to the existing admin-import
// pipeline. Staging is NOT NubArca storage: nothing becomes a visible file
// until the import succeeds. All endpoints are authenticated and
// owner-scoped (missing/foreign → 404); only admins may target another user.
// No absolute path, storage key, hash, or payload ever crosses this boundary.
public static class StagingUploadEndpoints
{
    public static IEndpointRouteBuilder MapStagingUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/uploads/staging/config", (
            [FromServices] IStagingUploadService staging) =>
        {
            return Results.Ok(staging.GetConfig());
        }).WithName("StagingConfig").RequireAuthorization();

        app.MapPost("/api/uploads/staging/sessions", async (
            [FromBody] StagingSessionCreateRequest request,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var isAdmin = httpContext.User.IsInRole(CookieSessionValidator.AdminRole);
            try
            {
                var session = await staging.CreateSessionAsync(userId, isAdmin, request, cancellationToken);
                await audit.LogAsync(
                    userId, AuditActions.StagingSessionCreate, AuditEntityTypes.StagingSession,
                    session.SessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { session.TargetUserId }, cancellationToken);
                return Results.Ok(session);
            }
            catch (StagingUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (StagingValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (StagingForbiddenException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
        }).WithName("StagingSessionCreate").RequireAuthorization();

        app.MapGet("/api/uploads/staging/sessions", async (
            [FromQuery] int? limit,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var sessions = await staging.ListSessionsAsync(userId, limit ?? 25, cancellationToken);
            return Results.Ok(sessions);
        }).WithName("StagingSessionList").RequireAuthorization();

        app.MapGet("/api/uploads/staging/sessions/{sessionId:guid}", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var session = await staging.GetSessionAsync(userId, sessionId, cancellationToken);
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).WithName("StagingSessionDetail").RequireAuthorization();

        app.MapPost("/api/uploads/staging/sessions/{sessionId:guid}/manifest", async (
            Guid sessionId,
            [FromBody] StagingManifestRequest request,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.SubmitManifestAsync(userId, sessionId, request, cancellationToken);
                if (result is null) return Results.NotFound();
                await audit.LogAsync(
                    userId, AuditActions.StagingManifestAccept, AuditEntityTypes.StagingSession,
                    sessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { result.TotalFiles, result.TotalBytes }, cancellationToken);
                return Results.Ok(result);
            }
            catch (StagingUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (StagingValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (StagingConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithName("StagingManifest").RequireAuthorization();

        app.MapGet("/api/uploads/staging/sessions/{sessionId:guid}/items", async (
            Guid sessionId,
            [FromQuery] string? status,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.GetItemsAsync(
                    userId, sessionId, status, page ?? 1, pageSize ?? 50, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (StagingValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("StagingItems").RequireAuthorization();

        // Resume protocol: a keyset page of incomplete items + their missing chunk
        // indices. The browser uploads exactly what this reports.
        app.MapGet("/api/uploads/staging/sessions/{sessionId:guid}/missing", async (
            Guid sessionId,
            [FromQuery] int? afterOrdinal,
            [FromQuery] int? limit,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var result = await staging.GetMissingAsync(
                userId, sessionId, afterOrdinal ?? 0, limit ?? 100, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("StagingMissing").RequireAuthorization();

        app.MapPut("/api/uploads/staging/sessions/{sessionId:guid}/items/{itemId:guid}/chunks/{chunkIndex:int}", async (
            Guid sessionId,
            Guid itemId,
            int chunkIndex,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.ReceiveChunkAsync(
                    userId, sessionId, itemId, chunkIndex,
                    httpContext.Request.Body, httpContext.Request.ContentLength, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (StagingUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (StagingValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (StagingConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithName("StagingChunkUpload").RequireAuthorization();

        app.MapPost("/api/uploads/staging/sessions/{sessionId:guid}/verify", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.VerifyAsync(userId, sessionId, cancellationToken);
                if (result is null) return Results.NotFound();
                await audit.LogAsync(
                    userId, AuditActions.StagingVerifyComplete, AuditEntityTypes.StagingSession,
                    sessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { result.VerifiedFiles, result.IncompleteFiles, result.CorruptFiles }, cancellationToken);
                return Results.Ok(result);
            }
            catch (StagingUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (StagingConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithName("StagingVerify").RequireAuthorization();

        app.MapPost("/api/uploads/staging/sessions/{sessionId:guid}/import", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.StartImportAsync(userId, sessionId, cancellationToken);
                if (result is null) return Results.NotFound();
                await audit.LogAsync(
                    userId, AuditActions.StagingImportStart, AuditEntityTypes.StagingSession,
                    sessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { result.AdminImportRunId, result.JobId }, cancellationToken);
                return Results.Ok(result);
            }
            catch (StagingUnavailableException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (StagingValidationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (StagingConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithName("StagingImport").RequireAuthorization();

        app.MapPost("/api/uploads/staging/sessions/{sessionId:guid}/cancel", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await staging.CancelAsync(userId, sessionId, cancellationToken);
                if (result is null) return Results.NotFound();
                await audit.LogAsync(
                    userId, AuditActions.StagingSessionCancel, AuditEntityTypes.StagingSession,
                    sessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                    null, cancellationToken);
                return Results.Ok(result);
            }
            catch (StagingConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithName("StagingCancel").RequireAuthorization();

        app.MapDelete("/api/uploads/staging/sessions/{sessionId:guid}", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] IStagingUploadService staging,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var result = await staging.DeleteAsync(userId, sessionId, cancellationToken);
            if (result is null) return Results.NotFound();
            if (result == false)
            {
                return Results.Conflict(new { error = "Cancel the running import before deleting this session." });
            }
            await audit.LogAsync(
                userId, AuditActions.StagingSessionDelete, AuditEntityTypes.StagingSession,
                sessionId, httpContext.Connection.RemoteIpAddress?.ToString(),
                null, cancellationToken);
            return Results.NoContent();
        }).WithName("StagingDelete").RequireAuthorization();

        return app;
    }
}

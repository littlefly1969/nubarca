using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.PhotoExport;
using NubArca.Api.Security;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Photo archive export (Cloud Functions) — read-only, owner-private. A
// session snapshots the owner's exportable photos (PhotoExportEligibility —
// normal visible image library only) into a stable manifest built by a
// background job, then serves per-file ORIGINAL content. No ZIP, no
// server-side archive copy. The token (returned once at creation, stored
// hashed) authorizes the manifest + file endpoints via an
// `Authorization: Bearer <token>` header so it stays out of URLs/logs; the
// owner cookie also works from the browser. Foreign/invalid → 404. The
// manifest and per-file download endpoints are deliberately NOT
// `.RequireAuthorization()` — token-only clients (rclone/PowerShell) must
// work; access is enforced inside the handler via
// `ResolveUsableSessionAsync`.
public static class PhotoExportEndpoints
{
    private const string ExportCreateRateLimitPolicy = "export-create";

    public static IEndpointRouteBuilder MapPhotoExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/photo-exports", async (
            HttpContext httpContext,
            [FromServices] PhotoExportService exports,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var created = await exports.CreateAsync(ownerUserId, cancellationToken);

            // Audit the session lifecycle (aggregate only — NEVER the token).
            await audit.LogAsync(
                ownerUserId, AuditActions.PhotoExportCreate, AuditEntityTypes.PhotoExportSession,
                created.SessionId, ip, new { expiresAt = created.ExpiresAt }, cancellationToken);

            return Results.Created($"/api/photo-exports/{created.SessionId}", created);
        }).WithName("CreatePhotoExport").RequireAuthorization().RequireRateLimiting(ExportCreateRateLimitPolicy);

        app.MapGet("/api/photo-exports/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] PhotoExportService exports,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await exports.GetStatusForOwnerAsync(id, ownerUserId, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("GetPhotoExport").RequireAuthorization();

        app.MapDelete("/api/photo-exports/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] PhotoExportService exports,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var revoked = await exports.RevokeAsync(id, ownerUserId, cancellationToken);
            if (!revoked)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(
                ownerUserId, AuditActions.PhotoExportRevoke, AuditEntityTypes.PhotoExportSession,
                id, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("RevokePhotoExport").RequireAuthorization();

        // Manifest: JSON Lines, streamed page-by-page from the persisted snapshot (never
        // the live tree). Cookie owner OR Bearer token. No auth attribute — token-only
        // clients (rclone/PowerShell) must work; access is enforced in the handler.
        app.MapGet("/api/photo-exports/{id:guid}/manifest", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] PhotoExportService exports,
            CancellationToken cancellationToken) =>
        {
            var session = await exports.ResolveUsableSessionAsync(
                id, httpContext.GetCurrentUserId(), ExportTokenFrom(httpContext), cancellationToken);
            if (session is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            httpContext.Response.ContentType = "application/x-ndjson";
            var basePath = $"/api/photo-exports/{id:N}/files";
            var newline = new byte[] { (byte)'\n' };
            Guid? after = null;
            while (true)
            {
                var entries = await exports.GetManifestPageAsync(
                    id, after, PhotoExportService.ManifestPageSize, basePath, cancellationToken);
                if (entries.Count == 0)
                {
                    break;
                }
                foreach (var entry in entries)
                {
                    await System.Text.Json.JsonSerializer.SerializeAsync(
                        httpContext.Response.Body, entry, cancellationToken: cancellationToken);
                    await httpContext.Response.Body.WriteAsync(newline, cancellationToken);
                }
                await httpContext.Response.Body.FlushAsync(cancellationToken);
                after = Guid.ParseExact(entries[^1].entryId, "N");
                if (entries.Count < PhotoExportService.ManifestPageSize)
                {
                    break;
                }
            }
        }).WithName("GetPhotoExportManifest");

        // Stream one entry's ORIGINAL content. Cookie owner OR Bearer token. The entry
        // must belong to the session; content is read owner-scoped via the same safe
        // path as the authenticated download (safe MIME, attachment disposition).
        app.MapGet("/api/photo-exports/{id:guid}/files/{entryId:guid}", async (
            Guid id,
            Guid entryId,
            HttpContext httpContext,
            [FromServices] PhotoExportService exports,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var session = await exports.ResolveUsableSessionAsync(
                id, httpContext.GetCurrentUserId(), ExportTokenFrom(httpContext), cancellationToken);
            if (session is null)
            {
                return Results.NotFound();
            }

            var fileItemId = await exports.ResolveEntryFileItemAsync(id, entryId, cancellationToken);
            if (fileItemId is null)
            {
                return Results.NotFound();
            }

            var content = await files.OpenContentAsync(fileItemId.Value, session.OwnerUserId, cancellationToken);
            if (content is null)
            {
                return Results.NotFound(); // file soft-deleted since the snapshot
            }

            return Results.File(
                content.Content, SafeContentType.ForServing(content.DetectedContentType), content.FileName);
        }).WithName("DownloadPhotoExportFile");

        return app;
    }

    // Extracts the export token from a Bearer header or X-Export-Token (never a URL).
    private static string? ExportTokenFrom(HttpContext httpContext)
    {
        var auth = httpContext.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }
        var x = httpContext.Request.Headers["X-Export-Token"].ToString();
        return string.IsNullOrWhiteSpace(x) ? null : x;
    }
}

using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Organizer;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Media Library (gallery membership rules + per-file exclusion) and Photo
// Organizer (owner-scoped date-taken reorganization: dry-run/run/status).
// Every endpoint is owner-scoped; missing/foreign folders, rules, or runs
// return a generic 404. Media Library rules affect only media surfaces
// (galleries, batch media jobs, organizer) — never the file browser,
// downloads, sharing, quota, or cleanup.
public static class PhotoOrganizerEndpoints
{
    public static IEndpointRouteBuilder MapPhotoOrganizerEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Slice 94: media-library rules (gallery membership) ────────────────────
        // Owner-scoped, opt-out configuration: every supported photo/video is in the
        // media library unless a folder rule excludes it. Rules affect ONLY media
        // surfaces (galleries, batch media jobs, future map/organizer) — never the
        // file browser, downloads, sharing, quota, or cleanup. Missing/foreign
        // folders and rules return 404; DTOs carry the owner's own folder ids/names
        // and rule fields only.

        app.MapGet("/api/media-library/rules", async (
            HttpContext httpContext,
            [FromServices] IMediaLibraryService mediaLibrary,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await mediaLibrary.ListRulesAsync(userId, cancellationToken));
        }).WithName("MediaLibraryRules").RequireAuthorization();

        app.MapPut("/api/media-library/rules", async (
            [FromBody] MediaLibraryRuleRequest request,
            HttpContext httpContext,
            [FromServices] IMediaLibraryService mediaLibrary,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var rule = await mediaLibrary.SetRuleAsync(userId, request, cancellationToken);
                return rule is null ? Results.NotFound() : Results.Ok(rule);
            }
            catch (MediaLibraryValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("MediaLibrarySetRule").RequireAuthorization();

        app.MapDelete("/api/media-library/rules/{ruleId:guid}", async (
            Guid ruleId,
            HttpContext httpContext,
            [FromServices] IMediaLibraryService mediaLibrary,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var deleted = await mediaLibrary.DeleteRuleAsync(userId, ruleId, cancellationToken);
            return deleted is null ? Results.NotFound() : Results.NoContent();
        }).WithName("MediaLibraryDeleteRule").RequireAuthorization();

        // Effective (inherited/explicit/default) state of one folder, for the UI badge.
        app.MapGet("/api/media-library/effective", async (
            [FromQuery] Guid folderId,
            HttpContext httpContext,
            [FromServices] IMediaLibraryService mediaLibrary,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var effective = await mediaLibrary.GetEffectiveAsync(userId, folderId, cancellationToken);
            return effective is null ? Results.NotFound() : Results.Ok(effective);
        }).WithName("MediaLibraryEffective").RequireAuthorization();

        // Owner-scoped diagnostics: eligibility + metadata-extraction coverage counts.
        app.MapGet("/api/media-library/stats", async (
            HttpContext httpContext,
            [FromServices] IMediaLibraryService mediaLibrary,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await mediaLibrary.GetStatsAsync(userId, cancellationToken));
        }).WithName("MediaLibraryStats").RequireAuthorization();

        // ── Media-library per-file exclusion (Slice 3) ───────────────────────────────
        // Owner-scoped bulk toggle of the per-file media-library membership. Excluded
        // files stay normal, browsable files; they are only suppressed from the media
        // surfaces and made non-eligible for NEW AI work. Photos and videos share the
        // same endpoints (the ids carry their own kind). No folders. No AI jobs.
        const int MaxMediaLibraryBulkItems = 1000;

        app.MapPost("/api/media-library/exclude", async (
            [FromBody] MediaLibraryBulkRequest? body,
            HttpContext httpContext,
            [FromServices] IMediaLibraryExclusionService exclusion,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            if (body?.FileIds is null)
                return Results.BadRequest(new { error = "Missing 'fileIds'." });
            if (body.FileIds.Count > MaxMediaLibraryBulkItems)
                return Results.BadRequest(new { error = $"At most {MaxMediaLibraryBulkItems} items per request." });

            var result = await exclusion.ExcludeAsync(userId, body.FileIds, cancellationToken);
            await audit.LogAsync(userId, AuditActions.MediaLibraryExclude, AuditEntityTypes.MediaLibrary,
                null, ip, new { result.Requested, result.Changed }, cancellationToken);
            return Results.Ok(result);
        }).WithName("MediaLibraryExclude").RequireAuthorization();

        app.MapPost("/api/media-library/restore", async (
            [FromBody] MediaLibraryBulkRequest? body,
            HttpContext httpContext,
            [FromServices] IMediaLibraryExclusionService exclusion,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            if (body?.FileIds is null)
                return Results.BadRequest(new { error = "Missing 'fileIds'." });
            if (body.FileIds.Count > MaxMediaLibraryBulkItems)
                return Results.BadRequest(new { error = $"At most {MaxMediaLibraryBulkItems} items per request." });

            var result = await exclusion.RestoreAsync(userId, body.FileIds, cancellationToken);
            await audit.LogAsync(userId, AuditActions.MediaLibraryRestore, AuditEntityTypes.MediaLibrary,
                null, ip, new { result.Requested, result.Changed }, cancellationToken);
            return Results.Ok(result);
        }).WithName("MediaLibraryRestore").RequireAuthorization();

        // ── Phase 2: Organize photos by date (owner-scoped) ──────────────────────
        // Dry-run is read-only; run enqueues a cooperative background job. All three
        // are owner-scoped: a user only ever organizes / sees their own runs. Responses
        // carry logical paths + counts only — never storage internals.

        // POST /api/photo-organizer/date-taken/dry-run — preview, no mutation.
        app.MapPost("/api/photo-organizer/date-taken/dry-run", async (
            HttpContext httpContext,
            [FromServices] PhotoDateTakenOrganizerService organizer,
            [FromBody] PhotoOrganizerRequest request,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (!OrganizerOptions.TryParse(request, out var options, out var error))
            {
                return Results.BadRequest(new { error });
            }
            var result = await organizer.DryRunAsync(ownerUserId, options, cancellationToken);
            return Results.Ok(result);
        }).WithName("PhotoOrganizerDryRun").RequireAuthorization();

        // POST /api/photo-organizer/date-taken/run — create a run + enqueue the job.
        app.MapPost("/api/photo-organizer/date-taken/run", async (
            HttpContext httpContext,
            [FromServices] PhotoDateTakenOrganizerService organizer,
            [FromBody] PhotoOrganizerRequest request,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (!OrganizerOptions.TryParse(request, out var options, out var error))
            {
                return Results.BadRequest(new { error });
            }
            var result = await organizer.StartRunAsync(ownerUserId, options, cancellationToken);
            return Results.Ok(result);
        }).WithName("PhotoOrganizerRun").RequireAuthorization();

        // GET /api/photo-organizer/date-taken/runs/{id} — owner-scoped run status.
        app.MapGet("/api/photo-organizer/date-taken/runs/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] PhotoDateTakenOrganizerService organizer,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await organizer.GetRunStatusAsync(ownerUserId, id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("PhotoOrganizerRunStatus").RequireAuthorization();

        return app;
    }
}

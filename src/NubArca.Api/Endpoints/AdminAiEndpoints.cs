using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Jobs;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, admin authorization, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Admin-only operational surface: medium-preview derivative rebuild
// status/trigger, and AI substrate status/diagnostics/face-settings.
// Aggregate/status data only — no raw vectors, blob ids, SHA, storage keys,
// physical paths, raw payloads, stack traces, extracted text, face
// identity, or any owner-private AI data.
public static class AdminAiEndpoints
{
    public static IEndpointRouteBuilder MapAdminAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/media/previews/medium/status", async (
            [FromServices] AppDbContext db,
            [FromServices] IOptions<MediaDerivativesOptions> options,
            CancellationToken cancellationToken) =>
        {
            var job = await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Type == JobTypes.MediumPreviewRegenerate)
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new
                {
                    jobId = j.Id,
                    status = j.Status,
                    progressCurrent = j.ProgressCurrent,
                    progressTotal = j.ProgressTotal,
                    progressMessage = j.ProgressMessage,
                    createdAt = j.CreatedAt,
                    updatedAt = j.UpdatedAt,
                    completedAt = j.CompletedAt,
                })
                .FirstOrDefaultAsync(cancellationToken);

            return Results.Ok(new
            {
                mediumPreviewMaxEdge = options.Value.EdgeFor(ThumbnailSizes.Medium),
                job,
            });
        }).WithName("AdminMediumPreviewStatus").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/media/previews/medium/rebuild", async (
            HttpContext httpContext,
            [FromServices] IJobQueue queue,
            [FromServices] IOptions<MediaDerivativesOptions> options,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var job = await queue.EnqueueAsync(
                JobTypes.MediumPreviewRegenerate,
                new MediumPreviewRegenerationJobPayload(),
                idempotencyKey: "admin:media:previews:medium:rebuild",
                cancellationToken: cancellationToken);

            await audit.LogAsync(
                httpContext.GetCurrentUserId()!.Value,
                AuditActions.AdminMediumPreviewRebuild,
                AuditEntityTypes.BackgroundJob,
                job.Id,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                new { job.Status, MediumPreviewMaxEdge = options.Value.EdgeFor(ThumbnailSizes.Medium) },
                cancellationToken);

            return Results.Ok(new
            {
                jobId = job.Id,
                status = job.Status,
                mediumPreviewMaxEdge = options.Value.EdgeFor(ThumbnailSizes.Medium),
            });
        }).WithName("AdminMediumPreviewRebuild").RequireAuthorization(CookieSessionValidator.AdminRole);

        // ── AI substrate (Phase 0C): admin-only operational status + aggregate
        // diagnostics. Aggregate/status data only — no raw vectors, blob ids, SHA,
        // storage keys, physical paths, raw payloads, stack traces, extracted text,
        // face identity, or any owner-private AI data. Mirrors the storage-stats /
        // admin-jobs admin endpoint pattern.
        app.MapGet("/api/admin/ai/status", async (
            [FromServices] IAiStatusService status,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await status.GetStatusAsync(cancellationToken);
            return Results.Ok(snapshot);
        }).WithName("AiStatus").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/ai/diagnostics", async (
            [FromServices] AiDiagnosticsAggregator aggregator,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await aggregator.AggregateAsync(cancellationToken);
            return Results.Ok(snapshot);
        }).WithName("AiDiagnostics").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Face Substrate v0: admin-only face settings + diagnostics. Returns the ACTIVE
        // similarity thresholds + safety caps (the admin-editable extension point),
        // enabled flags, active profile key, and per-package model-file presence.
        // Booleans/keys/numbers only — no model directory/file paths, raw vectors, or
        // storage identifiers. A future Admin UI edits these values here.
        app.MapGet("/api/admin/ai/face-settings", async (
            [FromServices] NubArca.Api.Ai.Faces.FaceDiagnosticsService faces,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await faces.GetAsync(cancellationToken));
        }).WithName("AiFaceSettings").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Admin write of the face similarity thresholds (People v0). Validated ranges;
        // persisted as non-secret overrides in ai_settings (layered over config). Returns
        // the refreshed diagnostics. Admin-only.
        app.MapPut("/api/admin/ai/face-settings", async (
            [FromBody] FaceSettingsUpdateRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.FaceSettingsService settings,
            [FromServices] NubArca.Api.Ai.Faces.FaceDiagnosticsService faces,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing body." });
            }

            var adminUserId = httpContext.GetCurrentUserId()!.Value;
            var update = new NubArca.Api.Ai.Faces.FaceSettingsUpdate(
                body.ClusterSimilarityThreshold,
                body.CandidateSimilarityThreshold,
                body.SearchDefaultSimilarityThreshold,
                body.SearchMinSimilarity,
                body.SearchMaxSimilarity,
                body.MaxFacesPerImage,
                body.KnnLouvainResolution);
            try
            {
                await settings.UpdateAsync(update, adminUserId, cancellationToken);
                await audit.LogAsync(
                    adminUserId, "ai.face-settings.update", "AiFaceSettings", adminUserId,
                    httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
                return Results.Ok(await faces.GetAsync(cancellationToken));
            }
            catch (NubArca.Api.Ai.Faces.FaceSettingsValidationException ex)
            {
                return Results.BadRequest(new { error = "Invalid settings.", details = ex.Errors });
            }
        }).WithName("AiFaceSettingsUpdate").RequireAuthorization(CookieSessionValidator.AdminRole);

        // (The bounded face-job triggers formerly here — POST /api/admin/ai/faces/jobs/
        // {kind} — were superseded by the unified admin jobs console:
        // POST /api/admin/jobs/enqueue with the ai-faces-detect/embeddings/cluster
        // commands. See src/NubArca.Api/Admin/AdminJobCommands.cs.)

        return app;
    }
}

// Admin face-settings write. Moved from Program.cs's top-level record.
public sealed record FaceSettingsUpdateRequest(
    double? ClusterSimilarityThreshold = null,
    double? CandidateSimilarityThreshold = null,
    double? SearchDefaultSimilarityThreshold = null,
    double? SearchMinSimilarity = null,
    double? SearchMaxSimilarity = null,
    int? MaxFacesPerImage = null,
    double? KnnLouvainResolution = null);

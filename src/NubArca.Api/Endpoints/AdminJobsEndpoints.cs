using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Http;
using NubArca.Api.Jobs;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, admin authorization, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Slice 90: admin background-jobs dashboard (visibility + control).
// Admin-gated (401 unauth / 403 non-admin). Returns ONLY safe summary fields
// (no PayloadJson, no LockOwner, no storage internals); error messages are the
// engine's already-sanitized type+truncated-message. This is visibility +
// cooperative cancellation, NOT a force-kill: a running job stops at its next
// cooperative checkpoint via the engine's cancellation flag/heartbeat path.
public static class AdminJobsEndpoints
{
    public static IEndpointRouteBuilder MapAdminJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/jobs", async (
            [FromQuery] string? status,
            [FromQuery] string? type,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] IJobQueue jobs,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(status) && !JobStatuses.IsKnown(status))
            {
                return Results.BadRequest(new { error = "Unknown status filter." });
            }
            var result = await jobs.ListAdminJobsAsync(
                new AdminJobFilter(status, type), page ?? 1, pageSize ?? 20, cancellationToken);
            return Results.Ok(result);
        }).WithName("AdminJobsList").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapGet("/api/admin/jobs/{id:guid}", async (
            Guid id,
            [FromServices] IJobQueue jobs,
            CancellationToken cancellationToken) =>
        {
            var job = await jobs.GetAdminJobAsync(id, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("AdminJobDetail").RequireAuthorization(CookieSessionValidator.AdminRole);

        app.MapPost("/api/admin/jobs/{id:guid}/cancel", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IJobQueue jobs,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var job = await jobs.GetAdminJobAsync(id, cancellationToken);
            if (job is null)
            {
                return Results.NotFound();
            }
            if (JobStatuses.IsTerminal(job.Status))
            {
                return Results.Conflict(new { error = "Job is already in a terminal state and cannot be cancelled." });
            }

            // Idempotent for queued/running jobs (re-requesting just re-sets the flag).
            var requested = await jobs.RequestCancellationAsync(id, cancellationToken);
            if (!requested)
            {
                // Raced into a terminal state between the read and the update.
                return Results.Conflict(new { error = "Job is already in a terminal state and cannot be cancelled." });
            }

            await audit.LogAsync(
                httpContext.GetCurrentUserId()!.Value,
                AuditActions.AdminJobCancel,
                AuditEntityTypes.BackgroundJob,
                id,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                null,
                cancellationToken);

            var updated = await jobs.GetAdminJobAsync(id, cancellationToken);
            return Results.Ok(updated);
        }).WithName("AdminJobCancel").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Admin console: the catalog of jobs an admin can launch from the UI, with
        // their parameter specs. Server-driven — the frontend renders one form per
        // command from this response, so new commands need no UI code. Safe-only:
        // no payloads, keys, paths, or secrets.
        app.MapGet("/api/admin/jobs/catalog", async (
            [FromServices] NubArca.Api.Admin.AdminJobCatalogService catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.BuildAsync(cancellationToken)))
            .WithName("AdminJobsCatalog").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Admin console: how many items each command would process right now. Split
        // from the catalog so the page renders instantly and these (wider) counts load
        // after it; briefly cached server-side.
        app.MapGet("/api/admin/jobs/pending", async (
            [FromServices] NubArca.Api.Admin.AdminJobCatalogService catalog,
            CancellationToken cancellationToken) =>
            Results.Ok(await catalog.PendingCountsAsync(cancellationToken)))
            .WithName("AdminJobsPending").RequireAuthorization(CookieSessionValidator.AdminRole);

        // Admin console: enqueue a catalogued job with validated parameters. Mirrors
        // `jobs enqueue` (same job types + payloads + idempotency keys). The submitted
        // parameters are validated against the command descriptor; the audit row keeps
        // ONLY the command key (never the parameter values).
        app.MapPost("/api/admin/jobs/enqueue", async (
            [FromBody] NubArca.Api.Admin.AdminJobEnqueueRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Jobs.IJobQueue jobs,
            [FromServices] NubArca.Api.Admin.AdminJobCatalogService catalog,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var command = NubArca.Api.Admin.AdminJobCommands.Find(body?.Command);
            if (command is null)
            {
                return Results.BadRequest(new { error = "Unknown job command." });
            }

            var values = NubArca.Api.Admin.AdminJobCommandBinder.Bind(command, body?.Params, out var bindError);
            if (values is null)
            {
                return Results.BadRequest(new { error = bindError ?? "Invalid parameters." });
            }

            // A `choice` value must be one of the options the catalog actually offered
            // (an unknown profile key would otherwise silently no-op in the handler).
            var choiceError = await catalog.ValidateChoicesAsync(command, body?.Params, cancellationToken);
            if (choiceError is not null)
            {
                return Results.BadRequest(new { error = choiceError });
            }

            var spec = command.Build(values);
            // Was an identical run already waiting? EnqueueAsync collapses onto it, and
            // the UI should say "already queued" rather than a misleading "queued ✓"
            // (that ambiguity produced four identical poster-regeneration rows).
            var alreadyQueued = spec.IdempotencyKey is not null
                && await db.BackgroundJobs.AsNoTracking().AnyAsync(
                    j => j.IdempotencyKey == spec.IdempotencyKey
                        && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running),
                    cancellationToken);
            var job = await jobs.EnqueueAsync(
                spec.JobType, spec.Payload, idempotencyKey: spec.IdempotencyKey,
                cancellationToken: cancellationToken);

            await audit.LogAsync(
                httpContext.GetCurrentUserId()!.Value,
                AuditActions.AdminJobEnqueue,
                AuditEntityTypes.BackgroundJob,
                job.Id,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                new { command = command.Key },
                cancellationToken);

            return Results.Ok(new NubArca.Api.Admin.AdminJobEnqueueResponse(
                job.Id, spec.JobType, alreadyQueued));
        }).WithName("AdminJobsEnqueue").RequireAuthorization(CookieSessionValidator.AdminRole);

        return app;
    }
}

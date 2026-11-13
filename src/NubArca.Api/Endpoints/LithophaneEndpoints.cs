using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Lithophanes;
using NubArca.Api.Security;

namespace NubArca.Api.Endpoints;

public static class LithophaneEndpoints
{
    public static IEndpointRouteBuilder MapLithophaneEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lithophanes").RequireAuthorization(PermissionPolicies.LaboratoryLithophanes);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { return await next(context); }
            catch (LithophaneException ex) { return Results.Json(new { error = ex.Code }, statusCode: ex.StatusCode); }
        });

        group.MapGet("", async (Guid? cursor, HttpContext http, LithophaneService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(Owner(http), cursor, ct))).WithName("ListLithophanes");

        group.MapPost("/statuses", async ([FromBody] LithophaneStatusRequest request, HttpContext http,
            LithophaneService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatusesAsync(Owner(http), request.Ids, ct))).WithName("GetLithophaneStatuses");

        group.MapPost("", async ([FromBody] LithophaneCreateRequest request, HttpContext http,
            LithophaneService service, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(Owner(http), request, ct);
            await AuditAsync(audit, http, AuditActions.LithophaneCreate, result.Id, ct);
            return Results.Created($"/api/lithophanes/{result.Id}", result);
        }).WithName("CreateLithophane").RequireRateLimiting("export-create");

        group.MapPost("/preview", async ([FromBody] LithophanePreviewRequest request, HttpContext http,
            LithophaneService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(Owner(http), request, ct)))
            .WithName("PreviewLithophane").RequireRateLimiting("export-create");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, LithophaneService service, CancellationToken ct) =>
        {
            var result = await service.GetAsync(Owner(http), id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("GetLithophane");

        group.MapGet("/{id:guid}/preview", async (Guid id, HttpContext http, LithophaneService service, CancellationToken ct) =>
        {
            var result = await service.SavedPreviewAsync(Owner(http), id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("GetLithophanePreview");

        group.MapGet("/{id:guid}/download", async (Guid id, HttpContext http, LithophaneService service,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var stream = await service.OpenDownloadAsync(Owner(http), id, ct);
            if (stream is null) return Results.NotFound();
            try { await AuditAsync(audit, http, AuditActions.LithophaneDownload, id, ct); }
            catch { await stream.DisposeAsync(); throw; }
            return Results.File(stream, "model/stl", $"nubarca-lithophane-{id:N}.stl", enableRangeProcessing: true);
        }).WithName("DownloadLithophane");

        group.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, LithophaneService service,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await service.CancelAsync(Owner(http), id, ct)) return Results.NotFound();
            await AuditAsync(audit, http, AuditActions.LithophaneCancel, id, ct);
            return Results.NoContent();
        }).WithName("CancelLithophane");

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, LithophaneService service,
            IAuditLogger audit, CancellationToken ct) =>
        {
            if (!await service.DeleteAsync(Owner(http), id, ct)) return Results.NotFound();
            await AuditAsync(audit, http, AuditActions.LithophaneDelete, id, ct);
            return Results.NoContent();
        }).WithName("DeleteLithophane");
        return app;
    }

    private static Guid Owner(HttpContext http) => http.GetCurrentUserId()!.Value;
    private static Task AuditAsync(IAuditLogger audit, HttpContext http, string action, Guid id, CancellationToken ct) =>
        audit.LogAsync(Owner(http), action, "lithophane", id, http.Connection.RemoteIpAddress?.ToString(), null, ct);
}

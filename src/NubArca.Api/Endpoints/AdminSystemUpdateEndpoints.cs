using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NubArca.Api.Access;
using NubArca.Api.Admin;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Updates page: this installation's release, the host updater's state, and
/// the two things an administrator can ask of it — check, and install the
/// release the last check found. The api only drops a request in the updater's
/// mailbox; the updater, on the host, runs the guided update with every gate.
/// Print Boxes are updated from here too, after the server, each only when asked.
/// </summary>
public static class AdminSystemUpdateEndpoints
{
    public static IEndpointRouteBuilder MapAdminSystemUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/system-update").RequirePermission(Permissions.AdminDashboard);

        admin.MapGet("", ([FromServices] SystemUpdateService service) => Results.Ok(service.Describe()))
            .WithName("DescribeSystemUpdate");

        admin.MapPost("/check", async (HttpContext context, [FromServices] SystemUpdateService service,
            [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var error = service.RequestCheck(out var requestId);
            if (error != SystemUpdateRequestError.None) return Refused(error);
            await audit.LogAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.SystemUpdateCheck,
                AuditEntityTypes.SystemUpdate, requestId,
                context.Connection.RemoteIpAddress?.ToString(), null, ct);
            return Results.Accepted(value: service.Describe());
        }).WithName("RequestSystemUpdateCheck");

        admin.MapPost("/apply", async ([FromBody] SystemUpdateApplyRequest request, HttpContext context,
            [FromServices] SystemUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var error = service.RequestApply(request, out var requestId);
            if (error != SystemUpdateRequestError.None) return Refused(error);
            await audit.LogAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.SystemUpdateApply,
                AuditEntityTypes.SystemUpdate, requestId,
                context.Connection.RemoteIpAddress?.ToString(),
                new { sha = request.Sha, confirmMigrations = request.ConfirmMigrations }, ct);
            return Results.Accepted(value: service.Describe());
        }).WithName("RequestSystemUpdateApply");

        admin.MapGet("/print-boxes", async ([FromServices] PrintAgentUpdateService service,
            [FromServices] IOptions<PrintOptions> options, CancellationToken ct) =>
            Results.Ok(await service.FleetAsync(options.Value.HeartbeatOnlineSeconds,
                options.Value.HeartbeatOfflineSeconds, ct)))
            .WithName("ListPrintBoxUpdates");

        admin.MapPost("/print-boxes", async (HttpContext context, [FromServices] PrintAgentUpdateService service,
            [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var (requested, version) = await service.RequestAllAsync(ct);
            if (requested > 0)
                await audit.LogAsync(AuditActor.User(context.GetCurrentUserId()!.Value),
                    AuditActions.PrintAgentUpdateRequest, AuditEntityTypes.PrintStation, null,
                    context.Connection.RemoteIpAddress?.ToString(), new { stations = requested, version }, ct);
            return Results.Ok(new { requested, version });
        }).WithName("RequestAllPrintBoxUpdates");

        admin.MapPost("/print-boxes/{stationId:guid}", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
            await RequestOneAsync(null, stationId, context, service, audit, ct))
            .WithName("RequestPrintBoxUpdateAsAdministrator");

        admin.MapDelete("/print-boxes/{stationId:guid}", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
            await CancelOneAsync(null, stationId, context, service, audit, ct))
            .WithName("CancelPrintBoxUpdateAsAdministrator");

        // The station's owner asks the same for their own box, from the printers page.
        var owner = app.MapGroup("/api/print/stations").RequireAuthorization();
        owner.MapPost("/{stationId:guid}/agent-update", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
            await RequestOneAsync(context.GetCurrentUserId()!.Value, stationId, context, service, audit, ct))
            .WithName("RequestPrintBoxUpdate");
        owner.MapDelete("/{stationId:guid}/agent-update", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
            await CancelOneAsync(context.GetCurrentUserId()!.Value, stationId, context, service, audit, ct))
            .WithName("CancelPrintBoxUpdate");

        return app;
    }

    private static async Task<IResult> RequestOneAsync(Guid? ownerId, Guid stationId, HttpContext context,
        PrintAgentUpdateService service, IAuditLogger audit, CancellationToken ct)
    {
        var (result, version) = await service.RequestAsync(ownerId, stationId, ct);
        switch (result)
        {
            case PrintAgentUpdateRequestResult.NotFound: return Results.NotFound();
            case PrintAgentUpdateRequestResult.NoBundle: return Results.Conflict(new { error = "no_bundle" });
            case PrintAgentUpdateRequestResult.UpToDate: return Results.Conflict(new { error = "up_to_date" });
            case PrintAgentUpdateRequestResult.NotSelfUpdating: return Results.Conflict(new { error = "manual_update" });
        }
        await audit.LogAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.PrintAgentUpdateRequest,
            AuditEntityTypes.PrintStation, stationId,
            context.Connection.RemoteIpAddress?.ToString(), new { version }, ct);
        return Results.Ok(new { requested = 1, version });
    }

    private static async Task<IResult> CancelOneAsync(Guid? ownerId, Guid stationId, HttpContext context,
        PrintAgentUpdateService service, IAuditLogger audit, CancellationToken ct)
    {
        if (!await service.CancelAsync(ownerId, stationId, ct)) return Results.NotFound();
        await audit.LogAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.PrintAgentUpdateCancel,
            AuditEntityTypes.PrintStation, stationId,
            context.Connection.RemoteIpAddress?.ToString(), null, ct);
        return Results.NoContent();
    }

    private static IResult Refused(SystemUpdateRequestError error) => error switch
    {
        SystemUpdateRequestError.UpdaterUnavailable => Results.Conflict(new { error = "updater_unavailable" }),
        SystemUpdateRequestError.Busy => Results.Conflict(new { error = "update_busy" }),
        SystemUpdateRequestError.NotDeployable => Results.Conflict(new { error = "not_deployable" }),
        _ => Results.Conflict(new { error = "check_again" }),
    };
}

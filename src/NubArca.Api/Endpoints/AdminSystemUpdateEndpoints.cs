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

        // The audit record is written first and must land: a request is handed
        // to the updater only once it is on record (WriteAsync, not best effort).
        admin.MapPost("/check", async (HttpContext context, [FromServices] SystemUpdateService service,
            [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var error = service.PrepareCheck(out var request);
            if (error != SystemUpdateRequestError.None) return Refused(error);
            await audit.WriteAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.SystemUpdateCheck,
                AuditEntityTypes.SystemUpdate, request!.Id,
                context.Connection.RemoteIpAddress?.ToString(), null, ct);
            error = service.Submit(request);
            return error == SystemUpdateRequestError.None ? Results.Accepted(value: service.Describe()) : Refused(error);
        }).WithName("RequestSystemUpdateCheck");

        admin.MapPost("/apply", async ([FromBody] SystemUpdateApplyRequest body, HttpContext context,
            [FromServices] SystemUpdateService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var error = service.PrepareApply(body, out var request);
            if (error != SystemUpdateRequestError.None) return Refused(error);
            await audit.WriteAsync(AuditActor.User(context.GetCurrentUserId()!.Value), AuditActions.SystemUpdateApply,
                AuditEntityTypes.SystemUpdate, request!.Id,
                context.Connection.RemoteIpAddress?.ToString(),
                new { sha = request.Sha, confirmMigrations = request.ConfirmMigrations }, ct);
            error = service.Submit(request);
            return error == SystemUpdateRequestError.None ? Results.Accepted(value: service.Describe()) : Refused(error);
        }).WithName("RequestSystemUpdateApply");

        admin.MapGet("/print-boxes", async ([FromServices] PrintAgentUpdateService service,
            [FromServices] IOptions<PrintOptions> options, CancellationToken ct) =>
            Results.Ok(await service.FleetAsync(options.Value.HeartbeatOnlineSeconds,
                options.Value.HeartbeatOfflineSeconds, ct)))
            .WithName("ListPrintBoxUpdates");

        admin.MapPost("/print-boxes", async (HttpContext context, [FromServices] PrintAgentUpdateService service,
            CancellationToken ct) =>
        {
            var (requested, version) = await service.RequestAllAsync(AuditActor.User(context.GetCurrentUserId()!.Value),
                context.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Ok(new { requested, version });
        }).WithName("RequestAllPrintBoxUpdates");

        admin.MapPost("/print-boxes/{stationId:guid}", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, CancellationToken ct) =>
            await RequestOneAsync(null, stationId, context, service, ct))
            .WithName("RequestPrintBoxUpdateAsAdministrator");

        admin.MapDelete("/print-boxes/{stationId:guid}", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, CancellationToken ct) =>
            await CancelOneAsync(null, stationId, context, service, ct))
            .WithName("CancelPrintBoxUpdateAsAdministrator");

        // The station's owner asks the same for their own box, from the printers page.
        var owner = app.MapGroup("/api/print/stations").RequireAuthorization();
        owner.MapPost("/{stationId:guid}/agent-update", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, CancellationToken ct) =>
            await RequestOneAsync(context.GetCurrentUserId()!.Value, stationId, context, service, ct))
            .WithName("RequestPrintBoxUpdate");
        owner.MapDelete("/{stationId:guid}/agent-update", async (Guid stationId, HttpContext context,
            [FromServices] PrintAgentUpdateService service, CancellationToken ct) =>
            await CancelOneAsync(context.GetCurrentUserId()!.Value, stationId, context, service, ct))
            .WithName("CancelPrintBoxUpdate");

        return app;
    }

    private static async Task<IResult> RequestOneAsync(Guid? ownerId, Guid stationId, HttpContext context,
        PrintAgentUpdateService service, CancellationToken ct)
    {
        var (result, version) = await service.RequestAsync(ownerId, stationId,
            AuditActor.User(context.GetCurrentUserId()!.Value), context.Connection.RemoteIpAddress?.ToString(), ct);
        return result switch
        {
            PrintAgentUpdateRequestResult.NotFound => Results.NotFound(),
            PrintAgentUpdateRequestResult.NoBundle => Results.Conflict(new { error = "no_bundle" }),
            PrintAgentUpdateRequestResult.UpToDate => Results.Conflict(new { error = "up_to_date" }),
            PrintAgentUpdateRequestResult.NotSelfUpdating => Results.Conflict(new { error = "manual_update" }),
            _ => Results.Ok(new { requested = 1, version }),
        };
    }

    private static async Task<IResult> CancelOneAsync(Guid? ownerId, Guid stationId, HttpContext context,
        PrintAgentUpdateService service, CancellationToken ct) =>
        await service.CancelAsync(ownerId, stationId, AuditActor.User(context.GetCurrentUserId()!.Value),
            context.Connection.RemoteIpAddress?.ToString(), ct)
            ? Results.NoContent() : Results.NotFound();

    private static IResult Refused(SystemUpdateRequestError error) => error switch
    {
        SystemUpdateRequestError.UpdaterUnavailable => Results.Conflict(new { error = "updater_unavailable" }),
        SystemUpdateRequestError.Busy => Results.Conflict(new { error = "update_busy" }),
        SystemUpdateRequestError.NotDeployable => Results.Conflict(new { error = "not_deployable" }),
        _ => Results.Conflict(new { error = "check_again" }),
    };
}

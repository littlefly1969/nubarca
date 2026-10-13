using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Print Agent bundle this installation hands out: described to any
/// signed-in user (the printers page and its setup guide), switched by an
/// administrator, and downloadable by the box itself — without an account,
/// only while it is switched on, and rate-limited.
/// </summary>
public static class PrintAgentDistributionEndpoints
{
    public const string DownloadRateLimitPolicy = "print-agent-download";

    public static IEndpointRouteBuilder MapPrintAgentDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/print/agent", async (HttpContext context,
            [FromServices] PrintAgentDistributionService service,
            [FromServices] IUserPermissionService permissions, CancellationToken ct) =>
        {
            var effective = await permissions.GetEffectiveAsync(context.GetCurrentUserId()!.Value, ct);
            return Results.Ok(await service.DescribeAsync(effective.Has(Permissions.AdminDashboard), ct));
        }).RequireAuthorization().WithName("DescribePrintAgentBundle");

        app.MapPut("/api/print/agent/sharing", async ([FromBody] SetPrintAgentSharingRequest request,
            HttpContext context, [FromServices] PrintAgentDistributionService service,
            [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = context.GetCurrentUserId()!.Value;
            await service.SetSharingAsync(userId, request.Enabled, ct);
            await audit.LogAsync(AuditActor.User(userId), AuditActions.PrintAgentSharingSet,
                AuditEntityTypes.PrintAgentDistribution, null,
                context.Connection.RemoteIpAddress?.ToString(), new { enabled = request.Enabled }, ct);
            return Results.Ok(await service.DescribeAsync(canManage: true, ct));
        }).RequirePermission(Permissions.AdminDashboard).WithName("SetPrintAgentSharing");

        app.MapGet($"{PrintAgentDistributionService.DownloadRoute}/{{fileName}}", async (string fileName,
            [FromServices] PrintAgentDistributionService service, CancellationToken ct) =>
        {
            var bundle = await service.DownloadableAsync(fileName, ct);
            return bundle is null
                ? Results.NotFound()
                : Results.File(bundle.FilePath, "application/zip", bundle.FileName, enableRangeProcessing: true);
        }).AllowAnonymous().RequireRateLimiting(DownloadRateLimitPolicy).WithName("DownloadPrintAgentBundle");

        return app;
    }
}

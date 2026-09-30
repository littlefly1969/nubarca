using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

public static class PrintEndpoints
{
    public const string ClaimHeader = "X-NubArca-Print-Claim";
    public const string EnrollmentRateLimitPolicy = "print-enrollment";

    public static IEndpointRouteBuilder MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/print").RequireAuthorization();
        owner.MapGet("/stations", async (HttpContext context, [FromServices] PrintStationService service,
            CancellationToken ct) => Results.Ok(await service.ListAsync(context.GetCurrentUserId()!.Value, ct)))
            .WithName("ListPrintStations");
        owner.MapPost("/stations", async ([FromBody] CreatePrintStationRequest request,
            HttpContext context, [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.CreateAsync(context.GetCurrentUserId()!.Value,
                    request.Name ?? string.Empty, ct);
                return Results.Created($"/api/print/stations/{result.Id:D}", result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("CreatePrintStation");
        owner.MapPost("/stations/{stationId:guid}/enrollment", async (Guid stationId,
            HttpContext context, [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            var result = await service.RenewEnrollmentAsync(context.GetCurrentUserId()!.Value, stationId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("RenewPrintStationEnrollment");
        owner.MapPut("/stations/{stationId:guid}/desired-state", async (Guid stationId,
            [FromBody] SetPrintStationStateRequest request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SetDesiredStateAsync(context.GetCurrentUserId()!.Value,
                    stationId, request.DesiredState, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("SetPrintStationDesiredState");
        owner.MapDelete("/stations/{stationId:guid}", async (Guid stationId,
            HttpContext context, [FromServices] PrintStationService service, CancellationToken ct) =>
            await service.RevokeAsync(context.GetCurrentUserId()!.Value, stationId, ct)
                ? Results.NoContent() : Results.NotFound()).WithName("RevokePrintStation");
        owner.MapPut("/stations/{stationId:guid}/devices/{deviceId:guid}/calibration", async (Guid stationId,
            Guid deviceId, [FromBody] PrintCalibrationDto? request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "invalid_calibration" });
            try
            {
                var device = await service.SetCalibrationAsync(context.GetCurrentUserId()!.Value,
                    stationId, deviceId, request, ct);
                return device is null ? Results.NotFound() : Results.Ok(device);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("SetPrinterCalibration");
        // The paper and the test page: the owner's, and the person's the
        // printer is lent to — whoever is standing at it.
        owner.MapPut("/stations/{stationId:guid}/devices/{deviceId:guid}/paper", async (Guid stationId,
            Guid deviceId, [FromBody] SetPrinterPaperRequest? request, HttpContext context,
            [FromServices] PrintStationService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "invalid_paper" });
            try
            {
                var userId = context.GetCurrentUserId()!.Value;
                var device = await service.SetLoadedPaperAsync(userId, stationId, deviceId, request.PaperSize, ct);
                if (device is null) return Results.NotFound();
                await audit.LogAsync(AuditActor.User(userId), AuditActions.PrinterPaperSet,
                    AuditEntityTypes.PrinterDevice, deviceId, Ip(context),
                    new { stationId, deviceId, paperSize = device.LoadedPaperSize }, ct);
                return Results.Ok(device);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("SetPrinterPaper");
        owner.MapPost("/stations/{stationId:guid}/test-jobs", async (Guid stationId,
            [FromBody] CreateTestPrintRequest request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            var (job, error) = await service.CreateTestPrintAsync(context.GetCurrentUserId()!.Value,
                stationId, request.PrinterDeviceId, ct);
            if (error is not null) return Results.Conflict(new { error });
            return job is null ? Results.NotFound() : Results.Accepted(value: job);
        }).WithName("CreatePrintTestJob");

        // Lending a printer: its owner shares it, sets a ceiling, ends it.
        owner.MapGet("/shared-printers", async (HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
            Results.Ok(await service.ListSharedAsync(context.GetCurrentUserId()!.Value, ct)))
            .WithName("ListSharedPrinters");
        owner.MapPost("/stations/{stationId:guid}/devices/{deviceId:guid}/shares", async (Guid stationId,
            Guid deviceId, [FromBody] SharePrinterRequest? request, HttpContext context,
            [FromServices] PrintStationService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "recipient_not_found" });
            var userId = context.GetCurrentUserId()!.Value;
            var (share, error) = await service.ShareAsync(userId, stationId, deviceId,
                request.Email, request.MaxSheets, ct);
            return error switch
            {
                null => await Logged(Results.Created($"/api/print/shares/{share!.Id:D}", share)),
                "not_found" => Results.NotFound(),
                "already_shared" => Results.Conflict(new { error }),
                _ => Results.BadRequest(new { error }),
            };

            async Task<IResult> Logged(IResult result)
            {
                await audit.LogAsync(AuditActor.User(userId), AuditActions.PrinterShareCreate,
                    AuditEntityTypes.PrinterDevice, deviceId, Ip(context),
                    new { stationId, deviceId, shareId = share!.Id, maxSheets = share.MaxSheets }, ct);
                return result;
            }
        }).WithName("SharePrinter");
        owner.MapPut("/shares/{shareId:guid}", async (Guid shareId,
            [FromBody] UpdatePrinterShareRequest? request, HttpContext context,
            [FromServices] PrintStationService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = context.GetCurrentUserId()!.Value;
            var (share, error) = await service.UpdateShareAsync(userId, shareId, request?.MaxSheets, ct);
            if (error == "not_found") return Results.NotFound();
            if (error is not null) return Results.BadRequest(new { error });
            await audit.LogAsync(AuditActor.User(userId), AuditActions.PrinterShareUpdate,
                AuditEntityTypes.PrinterDevice, null, Ip(context),
                new { shareId, maxSheets = share!.MaxSheets }, ct);
            return Results.Ok(share);
        }).WithName("UpdatePrinterShare");
        owner.MapDelete("/shares/{shareId:guid}", async (Guid shareId, HttpContext context,
            [FromServices] PrintStationService service, [FromServices] IAuditLogger audit, CancellationToken ct) =>
        {
            var userId = context.GetCurrentUserId()!.Value;
            var share = await service.RevokeShareAsync(userId, shareId, ct);
            if (share is null) return Results.NotFound();
            await audit.LogAsync(AuditActor.User(userId), AuditActions.PrinterShareRevoke,
                AuditEntityTypes.PrinterDevice, share.PrinterDeviceId, Ip(context),
                new { shareId, deviceId = share.PrinterDeviceId, usedSheets = share.UsedSheets }, ct);
            return Results.NoContent();
        }).WithName("RevokePrinterShare");
        owner.MapPost("/jobs/{jobId:guid}/cancel", async (Guid jobId, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
            await service.CancelAsync(context.GetCurrentUserId()!.Value, jobId, ct)
                ? Results.NoContent() : Results.Conflict(new { error = "job_not_cancellable" }))
            .WithName("CancelPrintJob");
        owner.MapPost("/jobs/{jobId:guid}/retry", async (Guid jobId, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
            await service.RetryAsync(context.GetCurrentUserId()!.Value, jobId, ct)
                ? Results.NoContent() : Results.Conflict(new { error = "job_not_retryable" }))
            .WithName("RetryPrintJob");

        app.MapPost("/api/print-agent/enroll", async ([FromBody] PrintEnrollmentRequest request,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            var result = await service.EnrollAsync(request, ct);
            return result is null ? Results.Unauthorized() : Results.Ok(result);
        }).WithName("EnrollPrintStation").AllowAnonymous()
            .RequireRateLimiting(EnrollmentRateLimitPolicy);

        var station = app.MapGroup("/api/print-agent")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = PrintStationAuthentication.Scheme });
        station.MapPost("/heartbeat", async ([FromBody] PrintHeartbeatRequest request,
            HttpContext context, [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.HeartbeatAsync(StationId(context), request, ct);
                return result is null ? Results.Unauthorized() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithName("PrintStationHeartbeat");
        station.MapPost("/jobs/claim", async ([FromBody] PrintClaimRequest request,
            HttpContext context, [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            var result = await service.ClaimAsync(StationId(context), request.AdapterKind, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).WithName("ClaimPrintJob");
        station.MapGet("/jobs/{jobId:guid}/artifact", async (Guid jobId,
            [FromHeader(Name = ClaimHeader)] string? claimToken, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(claimToken)) return Results.NotFound();
            var artifact = await service.OpenArtifactAsync(StationId(context), jobId, claimToken, ct);
            return artifact is null ? Results.NotFound()
                : Results.Stream(artifact.Content, artifact.ContentType);
        }).WithName("DownloadPrintArtifact");
        station.MapPost("/jobs/{jobId:guid}/submitting", async (Guid jobId,
            [FromBody] PrintSubmittingRequest request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
            await service.MarkSubmittingAsync(StationId(context), jobId, request.ClaimToken, ct)
                ? Results.NoContent() : Results.NotFound()).WithName("MarkPrintJobSubmitting");
        station.MapPost("/jobs/{jobId:guid}/result", async (Guid jobId,
            [FromBody] PrintResultRequest request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            try
            {
                return await service.ReportResultAsync(StationId(context), jobId, request, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "invalid_transition" }); }
        }).WithName("ReportPrintJobResult");
        return app;
    }

    private static Guid StationId(HttpContext context) =>
        Guid.Parse(context.User.FindFirstValue(PrintStationAuthentication.StationIdClaim)!);

    private static string? Ip(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();
}

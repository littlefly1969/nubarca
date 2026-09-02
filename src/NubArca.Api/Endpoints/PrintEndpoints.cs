using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        owner.MapPost("/stations/{stationId:guid}/test-jobs", async (Guid stationId,
            [FromBody] CreateTestPrintRequest request, HttpContext context,
            [FromServices] PrintStationService service, CancellationToken ct) =>
        {
            var result = await service.CreateTestPrintAsync(context.GetCurrentUserId()!.Value,
                stationId, request.PrinterDeviceId, ct);
            return result is null ? Results.NotFound() : Results.Accepted(value: result);
        }).WithName("CreatePrintTestJob");
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
}

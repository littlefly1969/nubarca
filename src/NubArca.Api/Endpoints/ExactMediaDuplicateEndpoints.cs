using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Files;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

public static class ExactMediaDuplicateEndpoints
{
    public static IEndpointRouteBuilder MapExactMediaDuplicateEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cloud-functions/media-duplicates/exact/runs", async (
            HttpContext httpContext,
            [FromServices] ExactMediaDuplicateCleanupService cleanup,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await cleanup.StartAsync(ownerUserId, cancellationToken);
            return Results.Accepted(
                $"/api/cloud-functions/media-duplicates/exact/runs/{result.RunId}",
                result);
        })
            .WithName("ExactMediaDuplicateCleanupStart")
            .RequirePermission(Permissions.CloudFunctionsAccess);

        app.MapGet("/api/cloud-functions/media-duplicates/exact/runs/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ExactMediaDuplicateCleanupService cleanup,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await cleanup.GetStatusAsync(ownerUserId, id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
            .WithName("ExactMediaDuplicateCleanupStatus")
            .RequirePermission(Permissions.CloudFunctionsAccess);

        return app;
    }
}

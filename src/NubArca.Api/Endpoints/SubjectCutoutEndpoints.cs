using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Ai.SubjectCutout;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Uploads;

namespace NubArca.Api.Endpoints;

// Owner-private "copy subject": lift the subject of one of the caller's own
// photographs off its background. Every route is owner-scoped; a foreign,
// missing, trashed, vaulted or non-photo file is a generic 404. The cutout is
// a transparent PNG computed on request from the medium preview — never from
// or instead of the original — and nothing is stored unless the owner saves it
// as a new file. No public, shared-album or party surface reaches these routes.
public static class SubjectCutoutEndpoints
{
    public static IEndpointRouteBuilder MapSubjectCutoutEndpoints(this IEndpointRouteBuilder app)
    {
        // Whether the action should be offered at all on this installation.
        app.MapGet("/api/subject-cutout", (
            [FromServices] SubjectCutoutService cutouts) =>
            Results.Ok(new SubjectCutoutAvailability(cutouts.IsAvailable())))
            .WithName("GetSubjectCutoutAvailability").RequireAuthorization();

        app.MapPost("/api/files/{id:guid}/subject-cutout", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] SubjectCutoutService cutouts,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await cutouts.CreateAsync(id, ownerUserId, cancellationToken);
            if (result.Status != SubjectCutoutStatus.Ok)
            {
                return Refusal(httpContext, result.Status);
            }

            var image = result.Image!;
            await audit.LogAsync(
                actor: ownerUserId,
                action: AuditActions.FileSubjectCutout,
                entityType: AuditEntityTypes.File,
                entityId: id,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { width = image.Width, height = image.Height },
                cancellationToken: cancellationToken);

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.File(image.Png, "image/png");
        }).WithName("CreateSubjectCutout").RequireAuthorization();

        app.MapPost("/api/files/{id:guid}/subject-cutout/save", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] SubjectCutoutService cutouts,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var saved = await cutouts.SaveAsync(
                    id, ownerUserId, httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
                if (saved.Status != SubjectCutoutStatus.Ok)
                {
                    return Refusal(httpContext, saved.Status);
                }

                var file = saved.File!;
                return Results.Created(
                    $"/api/files/{file.Id}/content",
                    new FileSummary(file.Id, file.Name, file.MimeType, file.SizeBytes, file.CreatedAt,
                        file.Width, file.Height));
            }
            catch (Exception ex) when (OwnerUploadHttp.TryMapFailure(ex, out var mapped))
            {
                // Quota, size and the like: the same answers an upload gives.
                return mapped;
            }
        }).WithName("SaveSubjectCutout").RequireAuthorization();

        return app;
    }

    private static IResult Refusal(HttpContext httpContext, SubjectCutoutStatus status)
    {
        switch (status)
        {
            case SubjectCutoutStatus.NotFound:
                return Results.NotFound();
            case SubjectCutoutStatus.NoSubject:
                return Results.Json(
                    new { error = "No subject was found in this photo.", code = "no_subject" },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            case SubjectCutoutStatus.Busy:
                httpContext.Response.Headers.RetryAfter = "5";
                return Results.Json(
                    new { error = "Another cutout is in progress. Try again shortly.", code = "busy" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                return Results.Json(
                    new { error = "Subject cutout is not available.", code = "unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

public sealed record SubjectCutoutAvailability(bool Available);

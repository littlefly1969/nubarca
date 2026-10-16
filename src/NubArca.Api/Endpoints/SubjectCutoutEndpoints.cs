using System.Globalization;
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
// An optional JSON body {"point":{"x":…,"y":…}} keeps only the touched subject.
public static class SubjectCutoutEndpoints
{
    public const string RegionHeader = "X-Subject-Cutout-Region";

    public static IEndpointRouteBuilder MapSubjectCutoutEndpoints(this IEndpointRouteBuilder app)
    {
        // Whether the action should be offered at all on this installation.
        app.MapGet("/api/subject-cutout", (
            [FromServices] SubjectCutoutService cutouts) =>
            Results.Ok(new SubjectCutoutAvailability(cutouts.IsAvailable(), cutouts.IsPointSelectionAvailable())))
            .WithName("GetSubjectCutoutAvailability").RequireAuthorization();

        app.MapPost("/api/files/{id:guid}/subject-cutout", async (
            Guid id,
            HttpContext httpContext,
            [FromBody] SubjectCutoutRequest? request,
            [FromServices] SubjectCutoutService cutouts,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadPoint(request, out var point)) return InvalidPoint();
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await cutouts.CreateAsync(id, ownerUserId, point, cancellationToken);
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
                metadata: new { width = image.Width, height = image.Height, point = point is not null },
                cancellationToken: cancellationToken);

            httpContext.Response.Headers.CacheControl = "no-store";
            // Where the cutout sits in the photo ("x,y,width,height", 0..1), so a
            // tap on the cutout maps back to a point on the photo.
            var r = image.Region;
            httpContext.Response.Headers[RegionHeader] = string.Join(',',
                new[] { r.X, r.Y, r.Width, r.Height }.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));
            return Results.File(image.Png, "image/png");
        }).WithName("CreateSubjectCutout").RequireAuthorization();

        app.MapPost("/api/files/{id:guid}/subject-cutout/save", async (
            Guid id,
            HttpContext httpContext,
            [FromBody] SubjectCutoutRequest? request,
            [FromServices] SubjectCutoutService cutouts,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadPoint(request, out var point)) return InvalidPoint();
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var saved = await cutouts.SaveAsync(
                    id, ownerUserId, point, httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
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

    // No body, or no point: the whole subject. A point must lie on the photo.
    private static bool TryReadPoint(SubjectCutoutRequest? request, out SubjectPoint? point)
    {
        point = null;
        if (request?.Point is not { } p) return true;
        var candidate = new SubjectPoint(p.X, p.Y);
        if (!candidate.IsValid) return false;
        point = candidate;
        return true;
    }

    private static IResult InvalidPoint() =>
        Results.Json(
            new { error = "The point must lie on the photo (0 to 1).", code = "invalid_point" },
            statusCode: StatusCodes.Status400BadRequest);

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

// PointSelection: the owner can tap one subject to keep only that one.
public sealed record SubjectCutoutAvailability(bool Available, bool PointSelection);

// Optional body of the cutout routes; Point normalized to the photo (0..1).
public sealed record SubjectCutoutRequest(SubjectCutoutPointDto? Point);

public sealed record SubjectCutoutPointDto(float X, float Y);

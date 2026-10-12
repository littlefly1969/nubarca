using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Http;
using NubArca.Api.Uploads;

namespace NubArca.Api.Endpoints;

// POST /api/albums/{albumId}/uploads — a photo or video uploaded straight into
// an album by its owner or by a contributor. Same contract as the folder
// upload (multipart "file", optional Idempotency-Key), answering an
// AlbumUploadResult. Files above the single-request size go through the
// resumable path and complete with { albumId }.
public static class AlbumUploadEndpoints
{
    public static void MapAlbumUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/albums/{albumId:guid}/uploads", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumUploadIngestion albumUploads,
            [FromServices] IUploadIdempotencyService uploadOperations,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (idempotencyKey is not null && !UploadOperationKey.IsValid(idempotencyKey))
            {
                return OwnerUploadHttp.InvalidKey();
            }

            // Access first: a viewer or a stranger never gets as far as sending
            // bytes into storage.
            var (access, destination) = await albumUploads.ResolveAsync(albumId, actorUserId, cancellationToken);
            if (access == AlbumUploadAccess.NotFound) return Results.NotFound();
            if (access == AlbumUploadAccess.NotPermitted) return Results.Forbid();

            return await OwnerUploadHttp.RunAsync(
                actorUserId,
                idempotencyKey,
                uploadOperations,
                async claimToken =>
                {
                    if (!httpContext.Request.HasFormContentType)
                    {
                        return IngestOutcome.Failed(Results.BadRequest(new { error = "Expected multipart/form-data." }));
                    }
                    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
                    var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                    if (file is null)
                    {
                        return IngestOutcome.Failed(Results.BadRequest(new { error = "Missing file part." }));
                    }

                    await using var stream = file.OpenReadStream();
                    return await TryIngestAsync(() => albumUploads.IngestAsync(
                        destination!, actorUserId, file.FileName, file.ContentType, claimToken, ip, stream, cancellationToken));
                },
                cancellationToken,
                answer => albumUploads.AddToAlbumAsync(destination!, actorUserId, answer, ip, cancellationToken));
        }).WithName("UploadToAlbum").RequireAuthorization().DisableAntiforgery();
    }

    /// <summary>Album-specific refusals as answers; everything else to the shared upload mapping.</summary>
    internal static async Task<IngestOutcome> TryIngestAsync(Func<Task<IngestOutcome>> ingest)
    {
        try
        {
            return await ingest();
        }
        catch (AlbumUploadNotMediaException)
        {
            return IngestOutcome.Failed(AlbumUploadIngestion.NotMedia());
        }
    }
}

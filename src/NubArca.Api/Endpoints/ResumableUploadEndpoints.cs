using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Auth;
using NubArca.Api.Http;
using NubArca.Api.Uploads;

namespace NubArca.Api.Endpoints;

// Resumable owner uploads: the chunked path the web upload engine takes for
// large files. The same authority as POST /api/files (any signed-in owner,
// into their own library), the same ingest at completion, the same answers
// for a full quota, a too-large file or a taken name.
//
//   GET    /api/uploads/resumable/config
//   POST   /api/uploads/resumable                      { fileName, sizeBytes }
//   GET    /api/uploads/resumable/{id}                 which chunks arrived
//   PUT    /api/uploads/resumable/{id}/chunks/{index}  raw bytes
//   POST   /api/uploads/resumable/{id}/complete        { parentFolderId, relativePath, conflict }
//   DELETE /api/uploads/resumable/{id}
//
// Owner-scoped throughout: a foreign or expired id is a 404. No path, storage
// key or hash ever crosses this boundary.
public static class ResumableUploadEndpoints
{
    public static IEndpointRouteBuilder MapResumableUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/uploads/resumable/config", (
            [FromServices] IResumableUploadService uploads) => Results.Ok(uploads.GetConfig()))
            .WithName("ResumableUploadConfig").RequireAuthorization();

        app.MapPost("/api/uploads/resumable", async (
            [FromBody] ResumableUploadCreateRequest? request,
            HttpContext httpContext,
            [FromServices] IResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Missing body." });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var created = await uploads.CreateAsync(ownerUserId, request, cancellationToken);
                return Results.Created($"/api/uploads/resumable/{created.UploadId}", created);
            }
            catch (Exception ex) when (TryMap(ex, out var result))
            {
                return result;
            }
        }).WithName("ResumableUploadCreate").RequireAuthorization();

        app.MapGet("/api/uploads/resumable/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var upload = await uploads.GetAsync(ownerUserId, id, cancellationToken);
            return upload is null ? Results.NotFound() : Results.Ok(upload);
        }).WithName("ResumableUploadGet").RequireAuthorization();

        app.MapPut("/api/uploads/resumable/{id:guid}/chunks/{chunkIndex:int}", async (
            Guid id,
            int chunkIndex,
            HttpContext httpContext,
            [FromServices] IResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            try
            {
                var result = await uploads.ReceiveChunkAsync(
                    ownerUserId, id, chunkIndex, httpContext.Request.Body,
                    httpContext.Request.ContentLength, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex) when (TryMap(ex, out var result))
            {
                return result;
            }
        }).WithName("ResumableUploadChunk").RequireAuthorization();

        app.MapPost("/api/uploads/resumable/{id:guid}/complete", async (
            Guid id,
            [FromBody] ResumableUploadCompleteRequest? request,
            HttpContext httpContext,
            [FromServices] IResumableUploadService uploads,
            [FromServices] IOwnerUploadIngestion ingestion,
            [FromServices] IUploadIdempotencyService operations,
            [FromServices] IAlbumUploadIngestion albumUploads,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            if (!UploadNameConflictPolicies.TryParse(request?.Conflict, out var conflict))
            {
                return Results.BadRequest(new { error = "Unknown 'conflict' policy." });
            }

            AlbumUploadDestination? album = null;
            if (request?.AlbumId is Guid albumId)
            {
                if (request.ParentFolderId is not null || !string.IsNullOrWhiteSpace(request.RelativePath))
                {
                    return Results.BadRequest(new { error = "An album upload chooses its own folder." });
                }
                var (access, destination) = await albumUploads.ResolveAsync(albumId, ownerUserId, cancellationToken);
                if (access == AlbumUploadAccess.NotFound) return Results.NotFound();
                if (access == AlbumUploadAccess.NotPermitted) return Results.Forbid();
                album = destination;
            }

            Domain.ResumableUpload? upload;
            try
            {
                upload = await uploads.GetForCompletionAsync(ownerUserId, id, cancellationToken);
            }
            catch (Exception ex) when (TryMap(ex, out var mapped))
            {
                return mapped;
            }
            if (upload is null) return Results.NotFound();

            // The completion is one replayable operation keyed by the upload:
            // a retry after a lost response answers with the same file, and two
            // racing completions cannot ingest twice.
            return await OwnerUploadHttp.RunAsync(
                ownerUserId, IResumableUploadService.OperationKey(id), operations, async claimToken =>
                {
                    if (upload.Status == Domain.ResumableUploadStatuses.Completed)
                    {
                        // Past the replay record (or after it was released):
                        // the row still names the file it produced.
                        var existing = upload.FileItemId is Guid fileId
                            ? await db.FileItems.FindAsync([fileId], cancellationToken)
                            : null;
                        if (existing is null || existing.DeletedAt is not null)
                        {
                            return IngestOutcome.Failed(Results.Conflict(new { error = "This upload is already complete.", code = "upload_completed" }));
                        }
                        // Record the answer again, or the fresh claim would sit
                        // pending and turn every later retry into "in progress".
                        if (claimToken is Guid token)
                        {
                            await operations.CompleteWithExistingAsync(token, existing.Id, cancellationToken);
                        }
                        return IngestOutcome.Success(existing);
                    }

                    IngestOutcome outcome;
                    await using (var content = uploads.OpenAssembled(upload))
                    {
                        outcome = album is not null
                            ? await AlbumUploadEndpoints.TryIngestAsync(() => albumUploads.IngestAsync(
                                album, ownerUserId, upload.FileName, upload.ContentType, claimToken, ip, content, cancellationToken))
                            : IngestOutcome.Success(await ingestion.IngestAsync(
                                new OwnerUploadRequest(
                                    ownerUserId, request?.ParentFolderId, upload.FileName, request?.RelativePath,
                                    upload.ContentType, conflict, claimToken, ip),
                                content,
                                cancellationToken));
                    }
                    if (outcome.Created is not { } created)
                    {
                        return outcome;
                    }
                    // The file is durable and the replay record says so; the
                    // bookkeeping below is best-effort (the sweeper reclaims
                    // whatever it leaves).
                    try
                    {
                        await uploads.MarkCompletedAsync(upload, created.Id, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("ResumableUpload")
                            .LogWarning("resumable upload: completion bookkeeping failed ({Type}).", ex.GetType().Name);
                    }
                    return outcome;
                },
                cancellationToken,
                album is null ? null : answer => albumUploads.AddToAlbumAsync(album, ownerUserId, answer, ip, cancellationToken));
        }).WithName("ResumableUploadComplete").RequireAuthorization();

        app.MapDelete("/api/uploads/resumable/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return await uploads.DeleteAsync(ownerUserId, id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }).WithName("ResumableUploadDelete").RequireAuthorization();

        return app;
    }

    private static bool TryMap(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case ResumableUploadUnavailableException unavailable:
                result = Results.Json(
                    new { error = unavailable.Message, code = "resumable_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
                return true;
            case ResumableUploadValidationException validation:
                result = Results.BadRequest(new { error = validation.Message });
                return true;
            case ResumableUploadConflictException conflict:
                result = Results.Json(
                    new { error = conflict.Message, code = conflict.Code },
                    statusCode: StatusCodes.Status409Conflict);
                return true;
            default:
                return OwnerUploadHttp.TryMapFailure(ex, out result);
        }
    }
}

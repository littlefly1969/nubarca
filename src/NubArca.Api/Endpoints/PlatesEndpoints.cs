using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Plates;
using NubArca.Api.Storage;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Plates (Targhe) — owner-private, segregated image surface. Standalone
// owner-private domain for license-plate recognition. Plate images are
// NEVER FileItems: they never appear in Files/Gallery, never enter
// People/Party/TV/Private Vault, are never publicly shareable, and never
// expose blob/storage/path/hash internals. Every endpoint is owner-scoped; a
// foreign or missing id returns a generic 404. Media is served as
// owner-private derived thumbnails/previews (rendered on demand) and an
// explicit authenticated original endpoint.
public static class PlatesEndpoints
{
    public static IEndpointRouteBuilder MapPlatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/plates/images", async (
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected multipart/form-data." });
            }
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest(new { error = "Missing file part." });
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var created = await plates.CreateFromUploadAsync(
                    ownerUserId, file.FileName, file.ContentType, stream, cancellationToken);

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.PlateUpload,
                    entityType: AuditEntityTypes.Plate,
                    entityId: created.Id,
                    ipAddress: ip,
                    metadata: new { name = created.OriginalFileName, contentType = created.ContentType, sizeBytes = created.SizeBytes },
                    cancellationToken: cancellationToken);

                return Results.Created($"/api/plates/images/{created.Id}", created);
            }
            catch (PlateImageValidationException ex)
            {
                // Size caps → 413; format/dimension → 400. Codes are client-safe tokens.
                if (ex.Code is PlateImageValidationException.TooLarge)
                {
                    return Results.Json(new { error = ex.Code }, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                return Results.BadRequest(new { error = ex.Code });
            }
            catch (UploadTooLargeException)
            {
                return Results.Json(
                    new { error = PlateImageValidationException.TooLarge },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        }).WithName("UploadPlateImage").RequireAuthorization();

        // Add EXISTING owner gallery images into the hidden plates container by
        // fileItemId — no bytes are copied (each acquires an additional reference to the
        // existing content-addressed blob). Owner-scoped; idempotent on active
        // membership; bounded batch; never starts analysis; never mutates the gallery.
        // Partial result: { added: [...], skipped: [{ itemId, reason }] }.
        app.MapPost("/api/plates/images/from-gallery", async (
            HttpContext httpContext,
            [FromBody] PlateAddFromGalleryRequest body,
            [FromServices] IPlateImageService plates,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var ids = (body?.FileItemIds ?? new List<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            const int cap = 500; // bounded batch — no large bulk-job system in this slice
            if (ids.Count > cap)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = cap });
            }

            var added = new List<PlateImageListItem>();
            var skipped = new List<object>();
            foreach (var fileItemId in ids)
            {
                try
                {
                    added.Add(await plates.AddFromGalleryAsync(ownerUserId, fileItemId, cancellationToken));
                }
                catch (PlateImageValidationException ex)
                {
                    skipped.Add(new { itemId = fileItemId, reason = ex.Code });
                }
            }
            await audit.LogAsync(
                ownerUserId, AuditActions.PlateAddFromGallery, AuditEntityTypes.Plate, null, ip,
                new { source = "gallery", added = added.Count, skipped = skipped.Count }, cancellationToken);
            return Results.Ok(new { added, skipped });
        }).WithName("AddPlateImagesFromGallery").RequireAuthorization();

        app.MapGet("/api/plates/images", async (
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var items = await plates.ListAsync(ownerUserId, cancellationToken);
            return Results.Ok(items);
        }).WithName("ListPlateImages").RequireAuthorization();

        app.MapGet("/api/plates/images/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var detail = await plates.GetDetailAsync(ownerUserId, id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetPlateImage").RequireAuthorization();

        app.MapDelete("/api/plates/images/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var deleted = await plates.DeleteAsync(ownerUserId, id, cancellationToken);
            if (!deleted)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.PlateDelete, AuditEntityTypes.Plate, id, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("DeletePlateImage").RequireAuthorization();

        app.MapGet("/api/plates/images/{id:guid}/thumbnail", async (
            Guid id,
            string? size,
            bool? blurFaces,
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            [FromServices] NubArca.Api.Plates.Redaction.IPlateRedactedMediaService redacted,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (blurFaces == true)
            {
                return await ServeRedactedPlateMediaAsync(
                    redacted, httpContext, ownerUserId, id,
                    NubArca.Api.Plates.Redaction.PlateRedactionSourceKind.Thumbnail, cancellationToken);
            }
            var content = await plates.RenderDerivativeAsync(
                ownerUserId, id, string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetPlateImageThumbnail").RequireAuthorization();

        app.MapGet("/api/plates/images/{id:guid}/preview", async (
            Guid id,
            bool? blurFaces,
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            [FromServices] NubArca.Api.Plates.Redaction.IPlateRedactedMediaService redacted,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (blurFaces == true)
            {
                return await ServeRedactedPlateMediaAsync(
                    redacted, httpContext, ownerUserId, id,
                    NubArca.Api.Plates.Redaction.PlateRedactionSourceKind.Preview, cancellationToken);
            }
            var content = await plates.RenderDerivativeAsync(ownerUserId, id, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetPlateImagePreview").RequireAuthorization();

        app.MapGet("/api/plates/images/{id:guid}/original", async (
            Guid id,
            bool? blurFaces,
            HttpContext httpContext,
            [FromServices] IPlateImageService plates,
            [FromServices] NubArca.Api.Plates.Redaction.IPlateRedactedMediaService redacted,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            if (blurFaces == true)
            {
                var result = await ServeRedactedPlateMediaAsync(
                    redacted, httpContext, ownerUserId, id,
                    NubArca.Api.Plates.Redaction.PlateRedactionSourceKind.Original, cancellationToken);
                // Audit the redacted original serve like a download (aggregate only).
                await audit.LogAsync(ownerUserId, AuditActions.PlateDownload, AuditEntityTypes.Plate, id, ip, null, cancellationToken);
                return result;
            }
            var content = await plates.OpenOriginalAsync(ownerUserId, id, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.PlateDownload, AuditEntityTypes.Plate, id, ip, null, cancellationToken);
            return Results.File(content.Content, content.ContentType, content.FileName);
        }).WithName("GetPlateImageOriginal").RequireAuthorization();

        // Request owner-private ALPR analysis of a plate image. Creates/queues a job and
        // returns quickly — the detection + OCR run on the WORKER, never in this request
        // (JobTypes.PlatesAnalyze, Compute band). Idempotent while a job is active.
        app.MapPost("/api/plates/images/{id:guid}/analysis", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IPlateAnalysisService analysis,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var summary = await analysis.RequestAnalysisAsync(ownerUserId, id, cancellationToken);
            if (summary is null)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(
                ownerUserId, AuditActions.PlateAnalyzeRequest, AuditEntityTypes.Plate, id, ip,
                new { jobId = summary.Id, status = summary.Status }, cancellationToken);
            return Results.Accepted($"/api/plates/images/{id}/analysis/latest", summary);
        }).WithName("RequestPlateAnalysis").RequireAuthorization();

        // Latest analysis summary for polling (status + plate count). Owner-scoped.
        app.MapGet("/api/plates/images/{id:guid}/analysis/latest", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IPlateAnalysisService analysis,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var summary = await analysis.GetLatestSummaryAsync(ownerUserId, id, cancellationToken);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        }).WithName("GetPlateAnalysisLatest").RequireAuthorization();

        return app;
    }

    // Serves an owner-private, server-side face-redacted plate media rendition. It
    // NEVER silently returns the unredacted image: when redaction is disabled/
    // unavailable it responds 409 (face_redaction_not_configured); an oversized
    // source responds 413 (image_too_large_for_redaction). A foreign/missing image
    // is a generic 404. Errors carry only a stable client-safe code — no stack
    // trace, model path, or storage internal.
    private static async Task<IResult> ServeRedactedPlateMediaAsync(
        NubArca.Api.Plates.Redaction.IPlateRedactedMediaService redacted,
        HttpContext httpContext,
        Guid ownerUserId,
        Guid id,
        NubArca.Api.Plates.Redaction.PlateRedactionSourceKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await redacted.GetAsync(ownerUserId, id, kind, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }
        catch (NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException)
        {
            return Results.Json(
                new { error = NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException.Code },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (NubArca.Api.Plates.Redaction.PlateRedactionImageTooLargeException)
        {
            return Results.Json(
                new { error = NubArca.Api.Plates.Redaction.PlateRedactionImageTooLargeException.Code },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    }

    // Duplicated from Program.cs's local `SetNoStore` / `SetPrivateDerivativeCache`
    // helpers (used by dozens of other still-inline endpoints there, so they stay
    // put) — same logic.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static void SetPrivateDerivativeCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "private, max-age=86400";
    }
}

// Plates request body. Moved from Program.cs's top-level records — used
// exclusively by the AddPlateImagesFromGallery endpoint above.
public sealed record PlateAddFromGalleryRequest(List<Guid> FileItemIds);

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NubArca.Api.Aesthetics;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Storage;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Aesthetics Lab / Beauty Lab — owner-private, opt-in, experimental. Covers
// both the public TV "Beauty Lab" QR mobile upload (anonymous,
// capability-token scoped, creates lab items only — never a FileItem) and
// the owner-facing lab surface (list/upload-from-gallery/direct-upload/
// analyze). Lab items are NEVER FileItems: they never appear in
// Files/Gallery, never enter People/Party/TV/Private Vault, are never
// publicly shareable, and never expose blob/storage/path/hash internals or
// raw model output. Every owner-facing endpoint is owner-scoped; a foreign
// or missing id returns a generic 404. Media is served ONLY as owner-private
// derived thumbnails/previews — never the original bytes.
public static class AestheticsEndpoints
{
    private const string BeautyLabUploadRateLimitPolicy = "beauty-lab-upload";

    public static IEndpointRouteBuilder MapAestheticsEndpoints(this IEndpointRouteBuilder app)
    {
        // --- PUBLIC TV "Beauty Lab" QR mobile upload (anonymous, capability-token
        // scoped). The token is a PATH segment (never a query string) so it doesn't
        // leak via Referer/query logs; responses are no-store + no-referrer. The token
        // grants EXACTLY ONE authority: upload images into the token owner's Aesthetics
        // Lab via the SAME direct-upload service the web lab uses. It can never list,
        // read, analyze, or delete, and creates NO Gallery/Files FileItem. Bounded file
        // count + total bytes per session; the per-file limit + image-decode gate +
        // blob dedup come from IAestheticLabService.AddFromUploadAsync.

        // Lifecycle/progress the mobile page reads by token (no owner info). 404 when
        // the token is unknown — the page shows a generic "no longer available".
        app.MapGet("/api/beauty-lab-upload/{token}", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAestheticUploadSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            var state = await sessions.GetPublicStateByTokenAsync(token, cancellationToken);
            return state is null ? Results.NotFound() : Results.Ok(state);
        }).WithName("GetBeautyLabUploadState")
          .RequireRateLimiting(BeautyLabUploadRateLimitPolicy);

        app.MapPost("/api/beauty-lab-upload/{token}/files", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAestheticUploadSessionService sessions,
            [FromServices] IAestheticLabService lab,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

            var resolution = await sessions.ResolveActiveByTokenAsync(token, cancellationToken);
            if (resolution is null)
            {
                // Unknown / expired / revoked / full — one generic not-found. No detail
                // that could distinguish a wrong token from an expired one.
                return Results.NotFound();
            }

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart form upload." });
            }
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = "No files were uploaded." });
            }

            var results = new List<AestheticUploadFileResultDto>();
            var accepted = 0;
            var rejected = 0;
            var remainingFiles = resolution.RemainingFiles;
            var remainingBytes = resolution.RemainingBytes;

            foreach (var file in form.Files)
            {
                var displayName = SanitizeUploadDisplayName(file.FileName);

                // Enforce the session's bounded caps BEFORE touching bytes.
                if (remainingFiles <= 0)
                {
                    rejected++;
                    results.Add(new AestheticUploadFileResultDto(displayName, false, "session_full"));
                    await sessions.RecordResultAsync(resolution.SessionId, false, 0, cancellationToken);
                    continue;
                }
                if (file.Length > remainingBytes)
                {
                    rejected++;
                    results.Add(new AestheticUploadFileResultDto(displayName, false, "session_full"));
                    await sessions.RecordResultAsync(resolution.SessionId, false, 0, cancellationToken);
                    continue;
                }

                try
                {
                    await using var stream = file.OpenReadStream();
                    var created = await lab.AddFromUploadAsync(
                        resolution.OwnerUserId, file.FileName, file.ContentType, stream, cancellationToken);
                    accepted++;
                    remainingFiles--;
                    remainingBytes -= created.SizeBytes;
                    results.Add(new AestheticUploadFileResultDto(displayName, true, null));
                    await sessions.RecordResultAsync(resolution.SessionId, true, created.SizeBytes, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AestheticLabValidationException ex)
                {
                    rejected++;
                    results.Add(new AestheticUploadFileResultDto(displayName, false, ex.Code));
                    await sessions.RecordResultAsync(resolution.SessionId, false, 0, cancellationToken);
                }
                catch
                {
                    // Never surface storage/decoder internals or stack traces to an
                    // anonymous caller — collapse to a safe failure.
                    rejected++;
                    results.Add(new AestheticUploadFileResultDto(displayName, false, "failed"));
                    await sessions.RecordResultAsync(resolution.SessionId, false, 0, cancellationToken);
                }
            }

            // Aggregate-only audit (no token, no filename, no storage internals).
            await audit.LogAsync(
                userId: resolution.OwnerUserId,
                action: AuditActions.AestheticUploadSessionUpload,
                entityType: AuditEntityTypes.AestheticLabItem,
                entityId: resolution.SessionId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { accepted, rejected },
                cancellationToken: cancellationToken);

            // Report the post-upload session state so the phone can stop when it's full.
            var post = await sessions.GetPublicStateByTokenAsync(token, cancellationToken);
            var status = post?.Status ?? AestheticUploadSessionStates.Expired;
            return Results.Ok(new AestheticUploadResultDto(accepted, rejected, status, results));
        }).WithName("BeautyLabUpload")
          .RequireRateLimiting(BeautyLabUploadRateLimitPolicy)
          .DisableAntiforgery();
        // ── Aesthetics Lab (Laboratorio estetico) — owner-private, opt-in, experimental ─
        // Isolated space for local HumanAesExpert analysis. Lab items are NEVER
        // FileItems: they never appear in Files/Gallery, never enter People/Party/TV/
        // Private Vault, are never publicly shareable, and never expose blob/storage/
        // path/hash internals or raw model output. Every endpoint is owner-scoped; a
        // foreign or missing id returns a generic 404. Media is served ONLY as
        // owner-private derived thumbnails/previews — never the original bytes.

        // List an owner's lab items (cursor pagination). JSON is no-store.
        app.MapGet("/api/aesthetics-lab/items", async (
            HttpContext httpContext,
            string? cursor,
            int? limit,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var page = await lab.ListAsync(ownerUserId, cursor, limit ?? 50, cancellationToken);
            return Results.Ok(page);
        }).WithName("ListAestheticLabItems").RequireAuthorization();

        // Add selected gallery images to the lab (acquire blob refs, no byte copy).
        app.MapPost("/api/aesthetics-lab/items/from-gallery", async (
            HttpContext httpContext,
            [FromBody] AestheticAddFromGalleryRequest body,
            [FromServices] IAestheticLabService lab,
            [FromServices] IOptions<AestheticsOptions> options,
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
            // Bound the request by the same batch cap the analysis path uses.
            var cap = Math.Max(1, options.Value.MaximumBatchItems);
            if (ids.Count > cap)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = cap });
            }

            var added = new List<AestheticLabItemDto>();
            var skipped = new List<object>();
            foreach (var fileItemId in ids)
            {
                try
                {
                    added.Add(await lab.AddFromGalleryAsync(ownerUserId, fileItemId, cancellationToken));
                }
                catch (AestheticLabValidationException ex)
                {
                    skipped.Add(new { itemId = fileItemId, reason = ex.Code });
                }
            }
            await audit.LogAsync(
                ownerUserId, AuditActions.AestheticLabAdd, AuditEntityTypes.AestheticLabItem, null, ip,
                new { source = "gallery", added = added.Count, skipped = skipped.Count }, cancellationToken);
            return Results.Ok(new { added, skipped });
        }).WithName("AddAestheticLabFromGallery").RequireAuthorization();

        // Direct upload straight into the lab (never becomes a FileItem / Gallery item).
        app.MapPost("/api/aesthetics-lab/items/upload", async (
            HttpContext httpContext,
            [FromServices] IAestheticLabService lab,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
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
                var created = await lab.AddFromUploadAsync(ownerUserId, file.FileName, file.ContentType, stream, cancellationToken);
                await audit.LogAsync(
                    ownerUserId, AuditActions.AestheticLabAdd, AuditEntityTypes.AestheticLabItem, created.Id, ip,
                    new { source = "upload", sizeBytes = created.SizeBytes, contentType = created.ContentType }, cancellationToken);
                return Results.Created($"/api/aesthetics-lab/items/{created.Id}", created);
            }
            catch (AestheticLabValidationException ex)
            {
                if (ex.Code is AestheticLabValidationException.TooLarge)
                {
                    return Results.Json(new { error = ex.Code }, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                return Results.BadRequest(new { error = ex.Code });
            }
            catch (UploadTooLargeException)
            {
                return Results.Json(new { error = AestheticLabValidationException.TooLarge }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        }).WithName("UploadAestheticLabItem").RequireAuthorization();

        app.MapGet("/api/aesthetics-lab/items/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var detail = await lab.GetDetailAsync(ownerUserId, id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetAestheticLabItem").RequireAuthorization();

        app.MapDelete("/api/aesthetics-lab/items/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticLabService lab,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var removed = await lab.RemoveAsync(ownerUserId, id, cancellationToken);
            if (!removed)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticLabRemove, AuditEntityTypes.AestheticLabItem, id, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("RemoveAestheticLabItem").RequireAuthorization();

        app.MapGet("/api/aesthetics-lab/items/{id:guid}/thumbnail", async (
            Guid id,
            string? size,
            HttpContext httpContext,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var content = await lab.RenderDerivativeAsync(
                ownerUserId, id, string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetAestheticLabItemThumbnail").RequireAuthorization();

        app.MapGet("/api/aesthetics-lab/items/{id:guid}/preview", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var content = await lab.RenderDerivativeAsync(ownerUserId, id, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetAestheticLabItemPreview").RequireAuthorization();

        // Manually request analysis of a bounded batch. Creates one run + one durable
        // job PER item on the WORKER (Compute band); never runs inference in-request.
        // When the feature is disabled, returns a controlled result with every item
        // skipped and NO job created.
        app.MapPost("/api/aesthetics-lab/analyses", async (
            HttpContext httpContext,
            [FromBody] AestheticAnalyzeRequest body,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IOptions<AestheticsOptions> options,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var ids = (body?.ItemIds ?? new List<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            var opts = options.Value;
            if (ids.Count > opts.MaximumBatchItems)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = opts.MaximumBatchItems });
            }
            if (!opts.Enabled)
            {
                // Controlled unavailable: 200 with a clear disabled outcome, no jobs.
                return Results.Ok(new AestheticAnalysisBatchResultDto(
                    Array.Empty<AestheticAnalysisEnqueuedDto>(),
                    ids.Select(i => new AestheticAnalysisSkippedDto(i, AestheticErrorCodes.FeatureDisabled)).ToList()));
            }

            var result = await analysis.RequestAnalysisAsync(ownerUserId, ids, body?.Capabilities, cancellationToken);
            await audit.LogAsync(
                ownerUserId, AuditActions.AestheticAnalyzeRequest, AuditEntityTypes.AestheticRun, null, ip,
                new { enqueued = result.Enqueued.Count, skipped = result.Skipped.Count }, cancellationToken);
            return Results.Accepted("/api/aesthetics-lab/items", result);
        }).WithName("RequestAestheticAnalysis").RequireAuthorization();

        app.MapGet("/api/aesthetics-lab/runs/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticAnalysisService analysis,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var run = await analysis.GetRunAsync(ownerUserId, id, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithName("GetAestheticRun").RequireAuthorization();

        app.MapPost("/api/aesthetics-lab/runs/{id:guid}/cancel", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var ok = await analysis.CancelRunAsync(ownerUserId, id, cancellationToken);
            if (!ok)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticAnalyzeCancel, AuditEntityTypes.AestheticRun, id, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("CancelAestheticRun").RequireAuthorization();

        app.MapPost("/api/aesthetics-lab/runs/{id:guid}/retry", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var run = await analysis.RetryRunAsync(ownerUserId, id, cancellationToken);
            if (run is null)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticAnalyzeRetry, AuditEntityTypes.AestheticRun, run.Id, ip, null, cancellationToken);
            return Results.Accepted($"/api/aesthetics-lab/runs/{run.Id}", run);
        }).WithName("RetryAestheticRun").RequireAuthorization();

        return app;
    }

    // Bounded, path-stripped, printable file name echoed back to the SAME
    // uploader for the mobile per-file success/failure UI. Never persisted
    // or logged.
    private static string SanitizeUploadDisplayName(string? fileName)
    {
        var baseName = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "photo";
        }
        baseName = new string(baseName.Where(c => !char.IsControl(c)).ToArray());
        return baseName.Length > 120 ? baseName[^120..] : baseName;
    }

    // Duplicated from Program.cs's local SetNoStore / SetPrivateDerivativeCache
    // helpers (used by dozens of other still-inline endpoints there, so they
    // stay put) — same logic.
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

// Aesthetics Lab request bodies (POST-only; ids in the body, never the URL).
// Moved from Program.cs's top-level records. AestheticAnalyzeRequest is also
// used by Endpoints/TvEndpoints.cs (TV Personal Area aesthetics analysis) —
// same namespace, so no extra using is required there.
public sealed record AestheticAddFromGalleryRequest(List<Guid> FileItemIds);
public sealed record AestheticAnalyzeRequest(List<Guid> ItemIds, List<string>? Capabilities = null);

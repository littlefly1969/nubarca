using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Http;
using NubArca.Api.Ingestion;
using NubArca.Api.Metadata;
using NubArca.Api.Security;
using NubArca.Api.Storage;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and delivery behavior are unchanged from the original inline
// mappings.
//
// FileItem-scoped surface: media delivery (content/thumbnail/preview/video/
// HLS renditions/poster/video-preview-strip), metadata (read/write/strip-
// embedded/write-datetaken), privacy-safe download, duplicates, similar-
// photo search, and the file lifecycle (upload, rename, move, soft-delete,
// restore). Folder listing, Trash, and folder lifecycle live in the sibling
// Endpoints/FolderTrashEndpoints.cs. Every endpoint is owner-scoped; a
// foreign or missing/soft-deleted file returns a generic 404 (no-leak).
// Never exposes BlobObjectId, SHA/content hash, StorageKey, physical paths,
// raw metadata JSON, or raw vectors.
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/{id:guid}/content", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var content = await files.OpenContentAsync(id, ownerUserId, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.FileDownload,
                entityType: AuditEntityTypes.File,
                entityId: id,
                ipAddress: ip,
                metadata: new { mimeType = content.MimeType, sizeBytes = content.SizeBytes },
                cancellationToken: cancellationToken);

            // Never serve the untrusted client-supplied MIME as authoritative. Only a
            // server-detected image type is trusted (so the gallery lightbox renders);
            // everything else is application/octet-stream. nosniff is set globally.
            // Content-Disposition stays attachment (Results.File with a filename).
            return Results.File(
                content.Content, SafeContentType.ForServing(content.DetectedContentType), content.FileName);
        }).WithName("DownloadFileContent").RequireAuthorization();

        app.MapGet("/api/files/{id:guid}/thumbnail", async (
            Guid id,
            [FromQuery] string? size,
            HttpContext httpContext,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var requestedSize = string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size!;
            if (!ThumbnailSizes.IsKnown(requestedSize))
            {
                return Results.BadRequest(new { error = $"Unknown thumbnail size '{requestedSize}'." });
            }

            // Slice 72: EnsureAsync (was OpenAsync) so a small thumbnail missing from
            // the derived root — e.g. a pre-slice-72 artifact, or a wiped derived cache
            // — self-heals by regenerating into the derived root, matching /preview and
            // /poster. Still null (404) for missing / foreign / soft-deleted / non-image.
            var content = await thumbnails.EnsureAsync(id, ownerUserId, requestedSize, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            // Auth-scoped + immutable bytes: a private cache for one day is safe and
            // keeps gallery scrolling cheap on repeat passes (slice 59). The user's
            // cookie still gates which thumbnails this browser can resolve.
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetFileThumbnail").RequireAuthorization();

        // Medium preview (slice 59). Authorized, owner-scoped, generated on demand
        // the first time the lightbox opens an image. Uses the same FileThumbnail
        // table as the small grid thumbnail: a second row per FileItem with
        // Size = "medium". Subsequent requests open the persisted derivative.
        // Never returns the original full-resolution bytes — the original is only
        // served by /api/files/{id}/content (explicit download).
        app.MapGet("/api/files/{id:guid}/preview", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var content = await thumbnails.EnsureAsync(
                id, ownerUserId, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetFilePreview").RequireAuthorization();

        // Slice 62: authorized, owner-scoped video playback. Streams the original
        // blob bytes (no transcoding) with HTTP Range support so a browser <video>
        // element can seek without downloading the whole file. The endpoint is
        // gated by SERVER-detected video content: `BlobMetadata.MediaCategory ==
        // "video"` AND a `DetectedContentType` in `SafeContentType.IsTrustedVideo`.
        // Spoofed video MIME on a non-video upload (detection slot stays null)
        // resolves to 404, indistinguishable from missing / foreign / soft-deleted.
        // Cookie auth is the only authorization model; there is no public video
        // endpoint in this slice.
        app.MapGet("/api/files/{id:guid}/video", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // Video-hls slice 2: with the HLS provider enabled this endpoint serves
            // the ADAPTIVE contract instead of raw bytes — the master playlist when
            // the ladder is published, 202 (after an idempotent lazy enqueue) while
            // it is being prepared. The gate inside the serving service is the same
            // owner + server-detected-video pair as the legacy branch below, so both
            // worlds 404 identically for missing/foreign/deleted/spoofed.
            if (hlsServing.Enabled)
            {
                var master = await hlsServing.GetMasterAsync(id, ownerUserId, cancellationToken);
                return master.Status switch
                {
                    VideoHlsMasterStatus.Ready => Results.Text(
                        master.MasterPlaylist!, VideoHlsServingService.MasterContentType),
                    VideoHlsMasterStatus.Preparing =>
                        VideoHlsServingService.Preparing(httpContext.Response),
                    _ => Results.NotFound(),
                };
            }

            // Legacy (provider disabled, the default): stream the original blob bytes
            // with HTTP Range support.
            // Owner-scoped + soft-delete-aware existence check first.
            var fileBlob = await db.FileItems
                .AsNoTracking()
                .Where(f => f.Id == id
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Select(f => new { f.BlobObjectId, f.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (fileBlob is null)
            {
                return Results.NotFound();
            }

            // Server-detected video gate. Both MediaCategory + DetectedContentType
            // must agree; a metadata row that lost the detection race (extraction
            // race / pre-slice-62 blob) is treated as "not a recognized video" →
            // 404 (no-leak parity with missing).
            var blobMeta = await db.BlobMetadata
                .AsNoTracking()
                .Where(m => m.BlobObjectId == fileBlob.BlobObjectId)
                .Select(m => new { m.MediaCategory, m.DetectedContentType })
                .FirstOrDefaultAsync(cancellationToken);
            if (blobMeta is null
                || blobMeta.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsTrustedVideo(blobMeta.DetectedContentType))
            {
                return Results.NotFound();
            }

            var content = await files.OpenContentAsync(id, ownerUserId, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            // Range processing on a seekable FileStream lets the browser seek
            // arbitrarily inside the video without re-reading from the start. The
            // global nosniff middleware (slice 54.2) still applies; the trusted
            // detected MIME is what the <video> element dispatches on.
            var safeType = SafeContentType.ForServingVideo(blobMeta.DetectedContentType);
            return Results.File(
                content.Content, safeType, fileBlob.Name, enableRangeProcessing: true);
        }).WithName("StreamFileVideo").RequireAuthorization();

        // Video-hls slice 2: ladder child files (variant playlist / init / media
        // segments). Every request re-runs the full ownership + detected-video gate
        // (centralized authorization on every download); the file name is untrusted
        // URL input whitelisted inside HlsDerivativeStorage. Served only while the
        // HLS provider is enabled — the route 404s otherwise (no legacy equivalent).
        app.MapGet("/api/files/{id:guid}/video/{rendition}/{file}", async (
            Guid id,
            string rendition,
            string file,
            HttpContext httpContext,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            if (!hlsServing.Enabled)
            {
                return Results.NotFound();
            }
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var content = await hlsServing.OpenLadderFileAsync(
                id, ownerUserId, $"{rendition}/{file}", cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("StreamFileVideoHlsFile").RequireAuthorization();

        // Slice 63: video poster derivative. Owner-scoped, lazy-generated on first
        // request and persisted as a FileThumbnail row with `Size = "poster"`.
        // Without FFmpeg the poster is a deterministic synthetic image (dark
        // backdrop + play triangle) — see `FileThumbnailService.TryGeneratePosterArt`.
        // The gate matches the /video endpoint: requires server-detected video
        // AND a trusted detected content type, so a spoofed video MIME on non-video
        // bytes returns 404 (no-leak parity with missing).
        app.MapGet("/api/files/{id:guid}/poster", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // The gate query mirrors /api/files/{id}/video so the two endpoints have
            // identical visibility for the same file: server-detected video category
            // and a trusted detected content type.
            var ok = await db.FileItems
                .AsNoTracking()
                .Where(f => f.Id == id
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Join(
                    db.BlobMetadata.AsNoTracking(),
                    f => f.BlobObjectId,
                    m => m.BlobObjectId,
                    (f, m) => new
                    {
                        m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
                    })
                .FirstOrDefaultAsync(cancellationToken);
            // Serves an ffmpeg-PRODUCED JPEG, so a legacy container confirmed by ffprobe
            // qualifies (SafeContentType.IsServerConfirmedVideo).
            if (ok is null
                || ok.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsServerConfirmedVideo(
                    ok.DetectedContentType, ok.VideoExtractionStatus, ok.VideoCodec))
            {
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                id, ownerUserId, ThumbnailSizes.Poster, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetFilePoster").RequireAuthorization();

        // Six-frame motion preview sprite. It is normally created by the post-ingest
        // derivatives job; this endpoint lazily fills a genuinely missing row once.
        // A recorded failure blocks subsequent pointer/focus requests until an
        // operator explicitly runs the retry-failed backfill.
        app.MapGet("/api/files/{id:guid}/video-preview-strip", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await db.FileItems.AsNoTracking()
                .Where(f => f.Id == id && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
                .Join(db.BlobMetadata.AsNoTracking(), f => f.BlobObjectId, m => m.BlobObjectId,
                    (f, m) => new
                    {
                        m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
                    })
                .FirstOrDefaultAsync(cancellationToken);
            // ffmpeg-PRODUCED strip → server-confirmed video is enough.
            if (ok is null || ok.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsServerConfirmedVideo(
                    ok.DetectedContentType, ok.VideoExtractionStatus, ok.VideoCodec))
                return Results.NotFound();

            var content = await thumbnails.EnsureAsync(
                id, ownerUserId, ThumbnailSizes.VideoPreviewStrip, cancellationToken);
            if (content is null) return Results.NotFound();

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetFileVideoPreviewStrip").RequireAuthorization();

        // Effective, owner-scoped metadata for one file: shared blob-derived facts +
        // the caller's private user metadata. 404 for missing / foreign / soft-deleted
        // (no-leak). Never exposes StorageKey, sha256, BlobObjectId, OwnerUserId,
        // physical paths, or the raw embedded-metadata document.
        app.MapGet("/api/files/{id:guid}/metadata", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IMetadataService metadata,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var result = await metadata.GetFileMetadataAsync(ownerUserId, id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("GetFileMetadata").RequireAuthorization();

        // Replaces the caller's user-metadata (title / description / tags / rating /
        // favorite / overrides) for one owned file. Only this FileItem's user metadata
        // changes — the blob and its blob-derived metadata are never touched, so a
        // deduplicated reference owned by another user is unaffected.
        app.MapPatch("/api/files/{id:guid}/metadata", async (
            Guid id,
            UpdateFileMetadataRequest? body,
            HttpContext httpContext,
            [FromServices] IMetadataService metadata,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing body." });
            }

            try
            {
                var result = await metadata.UpdateUserMetadataAsync(ownerUserId, id, body, cancellationToken);
                if (result is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileMetadataUpdate,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("UpdateFileMetadata").RequireAuthorization();

        // Strong metadata mutation (slice 58). Strips embedded metadata
        // (EXIF/IPTC/XMP/ICC/format text) from an image file by re-encoding the
        // bytes WITHOUT the metadata profiles. NEVER mutates the existing blob in
        // place — the dedup-aware IBlobService.StoreAsync either reuses an
        // existing matching blob or creates a new one with a new SHA-256, and
        // only THIS FileItem is updated to reference it. Other FileItems sharing
        // the old deduplicated blob remain unchanged. User metadata (title /
        // description / tags / rating / favorite / overrides) is preserved.
        // Returns the updated effective metadata DTO (same shape as GET /metadata).
        app.MapPost("/api/files/{id:guid}/metadata/strip-embedded", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IMetadataService metadata,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var updated = await files.StripEmbeddedMetadataAsync(ownerUserId, id, cancellationToken);
                if (updated is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileMetadataStripEmbedded,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                // Re-read curated effective metadata so the client can refresh
                // without a second round-trip. Returns null only on a 404 race
                // (the file vanished between strip + read), which the caller can
                // treat as success — the caller already knows the file id.
                var dto = await metadata.GetFileMetadataAsync(ownerUserId, id, cancellationToken);
                return dto is null ? Results.NoContent() : Results.Ok(dto);
            }
            catch (UnsupportedImageFormatException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            catch (ImageProcessingLimitException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }).WithName("StripFileMetadata").RequireAuthorization();

        // Strong metadata mutation (slice 66). Bakes the file's user DateTaken
        // override into the image bytes (EXIF). Same blob-immutable contract as
        // strip-embedded: a new blob is created/reused, only THIS FileItem is
        // repointed, user metadata + other FileItems are unchanged. Returns the
        // refreshed effective metadata DTO. 400 when no DateTaken override is set;
        // 415 for unsupported formats.
        app.MapPost("/api/files/{id:guid}/metadata/write-datetaken", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IMetadataService metadata,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var updated = await files.WriteDateTakenAsync(ownerUserId, id, cancellationToken);
                if (updated is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileMetadataWriteDateTaken,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                var dto = await metadata.GetFileMetadataAsync(ownerUserId, id, cancellationToken);
                return dto is null ? Results.NoContent() : Results.Ok(dto);
            }
            catch (MetadataOperationInputMissingException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnsupportedImageFormatException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            catch (ImageProcessingLimitException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }).WithName("WriteFileDateTaken").RequireAuthorization();

        // Privacy-safe download (slice 66). Streams a metadata-stripped copy of the
        // file WITHOUT mutating the FileItem or creating a new blob — the source
        // bytes are re-encoded on the fly. Owner-scoped; never serves the untrusted
        // client MIME as authoritative; attachment disposition; nosniff is global.
        // 415 for non-strippable formats (404 for missing/foreign/soft-deleted).
        app.MapGet("/api/files/{id:guid}/content/privacy-safe", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var content = await files.OpenPrivacySafeContentAsync(ownerUserId, id, cancellationToken);
                if (content is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileDownloadPrivacySafe,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { sizeBytes = content.SizeBytes },
                    cancellationToken: cancellationToken);

                return Results.File(
                    content.Content, SafeContentType.ForServing(content.DetectedContentType), content.FileName);
            }
            catch (UnsupportedImageFormatException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            catch (ImageProcessingLimitException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
        }).WithName("DownloadFilePrivacySafe").RequireAuthorization();

        // Slice 75: all active FileItems owned by the caller that share the same
        // underlying blob as the given fileItemId. Returns the logical occurrences
        // list with safe fields only — never SHA-256, BlobObjectId, StorageKey, or
        // raw metadata. 404 for missing / foreign / soft-deleted (no-leak).
        app.MapGet("/api/files/{id:guid}/duplicates", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var occurrences = await files.ListDuplicateOccurrencesAsync(ownerUserId, id, cancellationToken);
            if (occurrences is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(occurrences);
        }).WithName("ListFileDuplicates").RequireAuthorization();

        // Phase 1 (photo similarity v0): owner-private "similar photos" for an image,
        // exact-scan cosine over stored embeddings (no pgvector). Owner-scoped: results
        // are only the caller's own files (cross-owner is impossible). 404 when the
        // query file isn't the caller's; 200 with a possibly-empty list otherwise (no
        // default profile / not indexed yet). DTO carries owner-visible file ids + names
        // + a rounded score only — never raw vectors, blob ids, SHA, or storage keys.
        app.MapGet("/api/files/{id:guid}/similar", async (
            Guid id,
            [FromQuery] int? limit,
            [FromQuery] double? minSimilarity,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] PhotoSimilarityService similarity,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // Owner-private, always the configured active profile (no operator override).
            // Two compatible shapes on the same route:
            //   * legacy Top-N (the compact details-panel): no minSimilarity, no cursor.
            //   * Explorer: minSimilarity (threshold) + keyset cursor pagination.
            if (minSimilarity is null && cursor is null)
            {
                var result = await similarity.FindSimilarAsync(
                    ownerUserId, id, limit ?? 20, profileKeyOverride: null, cancellationToken: cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }

            // Validate the user-facing threshold bound before touching the service.
            var minSim = minSimilarity ?? 0.0;
            if (minSim < 0.0 || minSim > 1.0)
            {
                return Results.BadRequest(new { error = "'minSimilarity' must be between 0 and 1." });
            }

            var page = await similarity.FindSimilarPageAsync(
                ownerUserId, id, minSim, limit ?? PhotoSimilarityService.DefaultPageSize, cursor, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).WithName("ListSimilarPhotos").RequireAuthorization();

        app.MapPost("/api/files", async (
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            [FromServices] IPostIngestionMediaPipelineService mediaPipeline,
            CancellationToken cancellationToken) =>
            await UploadFileAsync(httpContext, parentFolderId: null, files, folders, audit, mediaPipeline, cancellationToken))
            .WithName("UploadRootFile").RequireAuthorization().DisableAntiforgery();

        app.MapPost("/api/folders/{id:guid}/files", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            [FromServices] IPostIngestionMediaPipelineService mediaPipeline,
            CancellationToken cancellationToken) =>
            await UploadFileAsync(httpContext, parentFolderId: id, files, folders, audit, mediaPipeline, cancellationToken))
            .WithName("UploadChildFile").RequireAuthorization().DisableAntiforgery();

        app.MapPatch("/api/files/{id:guid}/rename", async (
            Guid id,
            RenameRequest? body,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(new { error = "Missing 'name'." });
            }

            try
            {
                var renamed = await files.RenameAsync(ownerUserId, id, body.Name, cancellationToken);
                if (renamed is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileRename,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { name = renamed.Name },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FileSummary(renamed.Id, renamed.Name, renamed.MimeType, renamed.SizeBytes, renamed.CreatedAt, renamed.Width, renamed.Height));
            }
            catch (DuplicateFileNameException)
            {
                return Results.Conflict();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("RenameFile").RequireAuthorization();

        app.MapPatch("/api/files/{id:guid}/move", async (
            Guid id,
            MoveRequest? body,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing body." });
            }

            try
            {
                var moved = await files.MoveAsync(ownerUserId, id, body.ParentFolderId, cancellationToken);
                if (moved is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileMove,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { parentFolderId = body.ParentFolderId },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FileSummary(moved.Id, moved.Name, moved.MimeType, moved.SizeBytes, moved.CreatedAt, moved.Width, moved.Height));
            }
            catch (FolderNotFoundException)
            {
                return Results.NotFound();
            }
            catch (DuplicateFileNameException)
            {
                return Results.Conflict();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("MoveFile").RequireAuthorization();

        app.MapDelete("/api/files/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            // Explicit user-intent single-file delete: may record a deleted-content
            // tombstone if this removes the owner's final active occurrence.
            var deleted = await files.SoftDeleteAsync(
                ownerUserId, id, cancellationToken, FileDeleteReason.UserDelete);
            if (!deleted)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.FileDelete,
                entityType: AuditEntityTypes.File,
                entityId: id,
                ipAddress: ip,
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.NoContent();
        }).WithName("DeleteFile").RequireAuthorization();

        app.MapPost("/api/files/{id:guid}/restore", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var restored = await files.RestoreAsync(ownerUserId, id, cancellationToken);
                if (restored is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FileRestore,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { name = restored.Name, parentFolderId = restored.ParentFolderId },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FileSummary(
                    restored.Id, restored.Name, restored.MimeType, restored.SizeBytes, restored.CreatedAt,
                    restored.Width, restored.Height));
            }
            catch (DuplicateFileNameException)
            {
                return Results.Conflict();
            }
            catch (RestoreParentDeletedException)
            {
                return Results.Conflict();
            }
        }).WithName("RestoreFile").RequireAuthorization();


        return app;
    }

    // Slice 76: multipart upload shared by UploadRootFile and UploadChildFile
    // (root vs. folder-nested only differ in `parentFolderId`).
    private static async Task<IResult> UploadFileAsync(
        HttpContext httpContext,
        Guid? parentFolderId,
        IFileItemService files,
        IFolderService folders,
        IAuditLogger audit,
        IPostIngestionMediaPipelineService mediaPipeline,
        CancellationToken cancellationToken)
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

        // Slice 76: optional relative path (browser webkitRelativePath) for folder
        // upload, e.g. "Holiday/2024/IMG_001.jpg". Absent → normal single-file
        // upload into `parentFolderId`. The path is never trusted: it's validated /
        // normalised, and the directory chain is materialised as owner-scoped
        // logical folders.
        var relativePath = form["relativePath"].ToString();

        try
        {
            var parsed = RelativeUploadPath.Parse(
                string.IsNullOrWhiteSpace(relativePath) ? null : relativePath,
                file.FileName);

            // Materialise the directory chain (no-op when there are no segments),
            // then upload the file into the resolved leaf folder using the
            // path's own file-name segment.
            var targetFolderId = await folders.EnsureFolderPathAsync(
                ownerUserId, parentFolderId, parsed.Directories, cancellationToken);

            await using var stream = file.OpenReadStream();
            var created = await files.CreateAsync(
                ownerUserId,
                targetFolderId,
                parsed.FileName,
                file.ContentType,
                stream,
                cancellationToken);

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.FileUpload,
                entityType: AuditEntityTypes.File,
                entityId: created.Id,
                ipAddress: ip,
                metadata: new { name = created.Name, mimeType = created.MimeType, sizeBytes = created.SizeBytes, parentFolderId = created.ParentFolderId },
                cancellationToken: cancellationToken);

            // Enqueue the bounded, idempotent post-ingestion media pipeline (medium
            // preview + AI embedding + any pending metadata) WITHOUT blocking the
            // response on decode/encode/inference. Best-effort: a scheduling failure
            // never breaks the upload. Private Vault / non-media files schedule
            // nothing (the service re-checks eligibility).
            try
            {
                await mediaPipeline.OnFileIngestedAsync(ownerUserId, created.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                httpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PostIngestion")
                    .LogWarning(ex, "Post-ingest scheduling failed after upload; file will be picked up by a later backfill.");
            }

            var summary = new FileSummary(
                created.Id, created.Name, created.MimeType, created.SizeBytes, created.CreatedAt,
                created.Width, created.Height);
            return Results.Created($"/api/files/{created.Id}/content", summary);
        }
        catch (DuplicateFileNameException)
        {
            return Results.Conflict();
        }
        catch (FolderNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UploadTooLargeException)
        {
            // 413 Payload Too Large — app-level Storage:MaxUploadBytes ceiling.
            return Results.Json(
                new { error = "File exceeds the maximum allowed upload size." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (QuotaExceededException)
        {
            // 413 — accepting this file would push the user past their quota.
            // The message stays generic; the user's own figures are available
            // via GET /api/storage/me.
            return Results.Json(
                new { error = "Upload would exceed your storage quota." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (
            ex.StatusCode == StatusCodes.Status413PayloadTooLarge
            || ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            // Slice 78: Kestrel rejects the request body before our code runs when
            // the upload exceeds KestrelServerLimits.MaxRequestBodySize or
            // FormOptions.MultipartBodyLengthLimit. Convert the generic
            // BadHttpRequestException to a clean 413 JSON response so the frontend
            // (and the operator) get a clear, actionable error instead of a vague
            // 400/500. No file bytes or partial FileItem rows are created because
            // Kestrel never handed us the body.
            return Results.Json(
                new { error = "The uploaded file is too large. Check the server's maximum upload size." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    }

    // Duplicated from Program.cs's local SetPrivateDerivativeCache helper
    // (used by dozens of other still-inline endpoints there, so it stays
    // put) — same logic.
    private static void SetPrivateDerivativeCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "private, max-age=86400";
    }
}

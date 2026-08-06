using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization/anonymous metadata,
// rate limits, status codes, DTOs, and audit behavior are unchanged from the
// original inline mappings.
//
// Party (public event albums + owner-side moderation). Covers three route
// groups that were non-contiguous in Program.cs:
//   1. PUBLIC party album read/media/upload (/api/party/{token}/...) —
//      anonymous, token-scoped. A public "Beauty Lab" upload block sat
//      between this group and group 2 in the original file
//      (/api/beauty-lab-upload/*, Aesthetics feature, NOT Party) and stays
//      in Program.cs untouched.
//   2. PUBLIC party face search (/api/party/{token}/face-search/...) —
//      anonymous, view-token-scoped.
//   3. Owner-side album Party settings + upload moderation
//      (/api/albums/{id}/party-settings, /api/albums/{albumId}/party-uploads/*)
//      — normal authenticated owner session, deliberately left in Program.cs
//      during the album-extraction slice specifically so it could move here
//      with the rest of Party.
// Consolidating all three into one MapPartyEndpoints() call is safe: none of
// these route templates overlap the Beauty Lab, photo-export, or album
// templates that remain inline, so registration order does not affect
// matching.
public static class PartyEndpoints
{
    // Mirrors the top-level rate-limit-policy constants still defined in
    // Program.cs for the (untouched) rate limiter policy registration —
    // duplicated here only as the literal policy names, not new policies.
    private const string PartyPublicRateLimitPolicy = "party-public";
    private const string PartyPublicMediaRateLimitPolicy = "party-public-media";
    private const string PartyUploadRateLimitPolicy = "party-upload";
    private const string PartyFaceSearchRateLimitPolicy = "party-face-search";

    public static IEndpointRouteBuilder MapPartyEndpoints(this IEndpointRouteBuilder app)
    {
        // --- PUBLIC party album (read-only) ---
        //
        // Anonymous, token-authenticated. A party token unlocks ONE album for view-only
        // access to PARTY-SAFE media: metadata-stripped, downscaled DERIVED images
        // (small thumbnail for grids, medium preview for viewing/download). Never the
        // original bytes, EXIF/GPS/raw metadata, filenames, owner identity, face/person
        // data, AI data, storage/blob ids, SHA, paths, vectors, or token hash. Every
        // request re-validates the token (enabled, not revoked/expired, album still
        // owner-owned + ShowOnTv), so disabling party mode kills access on the next
        // request. Unknown/revoked/expired/foreign all collapse to a generic 404.
        app.MapGet("/api/party/{token}", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            var header = await partyMedia.GetAlbumAsync(access.OwnerUserId, access.AlbumId, cancellationToken);
            if (header is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyPublicView,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.Ok(new NubArca.Api.Party.PartyAlbumDto(header.Name, header.ItemCount));
        }).WithName("GetPartyAlbum").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapGet("/api/party/{token}/items", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            var header = await partyMedia.GetAlbumAsync(access.OwnerUserId, access.AlbumId, cancellationToken);
            var items = await partyMedia.ListItemsAsync(access.OwnerUserId, access.AlbumId, cancellationToken);
            if (header is null || items is null)
            {
                return Results.NotFound();
            }

            var enc = Uri.EscapeDataString(token);
            var dtos = items.Select(i =>
            {
                var isVideo = i.Kind == NubArca.Api.Party.PartyMediaKind.Video;
                return new NubArca.Api.Party.PartyItemDto(
                    i.FileItemId,
                    isVideo ? "video" : "image",
                    $"/api/party/{enc}/media/{i.FileItemId}/thumbnail",
                    $"/api/party/{enc}/media/{i.FileItemId}/preview",
                    // View-only for videos in this slice (no playback/download); images
                    // get a metadata-stripped medium download.
                    isVideo ? null : $"/api/party/{enc}/media/{i.FileItemId}/download");
            }).ToList();

            return Results.Ok(new NubArca.Api.Party.PartyItemsDto(header.Name, dtos));
        }).WithName("GetPartyItems").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapGet("/api/party/{token}/media/{fileId:guid}/thumbnail", (
            string token,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
            ServePartyMediaAsync(token, fileId, "thumbnail", httpContext, party, partyMedia, thumbnails, stripper, cancellationToken))
            .WithName("GetPartyMediaThumbnail")
            .RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        app.MapGet("/api/party/{token}/media/{fileId:guid}/preview", (
            string token,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
            ServePartyMediaAsync(token, fileId, "preview", httpContext, party, partyMedia, thumbnails, stripper, cancellationToken))
            .WithName("GetPartyMediaPreview")
            .RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        app.MapGet("/api/party/{token}/media/{fileId:guid}/download", (
            string token,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
            ServePartyMediaAsync(token, fileId, "download", httpContext, party, partyMedia, thumbnails, stripper, cancellationToken))
            .WithName("GetPartyMediaDownload")
            .RequireRateLimiting(PartyPublicRateLimitPolicy);

        // PUBLIC party UPLOAD (anonymous, upload-token scoped). Guests add photos to a
        // party album on the owner's behalf. The upload token is SEPARATE from the
        // view token; a view token can never authorize an upload here. Every request
        // re-validates the upload token (enabled + upload sub-switch on + not
        // revoked/expired + album still owner-owned + ShowOnTv), so disabling party (or
        // upload) refuses uploads immediately. Images only (server-detected);
        // metadata/originals are never served back publicly. Rate-limited per IP.
        // Response is a safe count DTO — no storage internals, ids, or stack traces.
        app.MapPost("/api/party/{token}/upload", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyUploadService uploads,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart form upload." });
            }

            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var files = form.Files.Count > 0 ? form.Files : null;
            if (files is null)
            {
                return Results.BadRequest(new { error = "No files were uploaded." });
            }

            var accepted = 0;
            var rejected = 0;
            foreach (var file in files)
            {
                NubArca.Api.Party.PartyUploadOutcome outcome;
                try
                {
                    await using var stream = file.OpenReadStream();
                    outcome = await uploads.UploadAsync(
                        access.OwnerUserId, access.AlbumId,
                        file.FileName, file.ContentType, file.Length, stream,
                        access.PartyAlbumLinkId, access.RequireUploadApproval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    outcome = NubArca.Api.Party.PartyUploadOutcome.Failed;
                }

                if (outcome == NubArca.Api.Party.PartyUploadOutcome.Accepted) accepted++;
                else rejected++;
            }

            // Aggregate-only audit (no token/hash, no file names, no storage internals).
            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyUpload,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { accepted, rejected },
                cancellationToken: cancellationToken);

            return Results.Ok(new NubArca.Api.Party.PartyUploadResultDto(accepted, rejected));
        }).WithName("PartyUpload").RequireRateLimiting(PartyUploadRateLimitPolicy).DisableAntiforgery();

        // PUBLIC party FACE SEARCH (anonymous, VIEW-token scoped). A guest uploads one
        // selfie; the backend detects the most prominent face, embeds it with the SAME
        // face package as the owner's library, and returns the currently-visible members
        // of THIS party album that match. Album-scoped + owner-scoped. The selfie and the
        // query embedding are NEVER stored; the response carries NO similarity score,
        // face id, person id, person name, or vector. When AI / the face model is
        // disabled or unavailable a safe "unavailable" state is returned (503). The
        // tightest per-IP party window applies (detection + embedding is expensive).
        app.MapPost("/api/party/{token}/face-search", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart form upload." });
            }

            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No image was uploaded." });
            }

            byte[] bytes;
            try
            {
                await using var stream = file.OpenReadStream();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                bytes = ms.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Results.BadRequest(new { error = "The image could not be read." });
            }

            var outcome = await faceSearch.SearchAsync(
                access.OwnerUserId, access.AlbumId, access.PartyAlbumLinkId,
                bytes, file.ContentType, cancellationToken);

            // Aggregate-only audit (never the selfie, token/hash, query vector, file
            // names, or storage internals).
            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyFaceSearch,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { status = outcome.Status, resultCount = outcome.ResultCount },
                cancellationToken: cancellationToken);

            var dto = new NubArca.Api.Party.PartyFaceSearchResponseDto(
                outcome.Status, outcome.SearchId, outcome.ResultCount,
                PartyImageItems(token, outcome.FileItemIds));

            return outcome.Status switch
            {
                NubArca.Api.Domain.PartyFaceSearchStatuses.Unavailable =>
                    Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable),
                NubArca.Api.Domain.PartyFaceSearchStatuses.InvalidImage =>
                    Results.Json(dto, statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Ok(dto),
            };
        }).WithName("PartyFaceSearch").RequireRateLimiting(PartyFaceSearchRateLimitPolicy).DisableAntiforgery();

        // Re-fetch a stored face search's currently-visible matches (rank order). Every
        // request re-validates the view token AND re-derives album visibility, so items
        // hidden/removed/pending since the search drop out; an expired/foreign/unknown
        // search → generic 404.
        app.MapGet("/api/party/{token}/face-search/{searchId:guid}", async (
            string token,
            Guid searchId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            var view = await faceSearch.GetAsync(access.OwnerUserId, access.AlbumId, searchId, cancellationToken);
            if (view is null)
            {
                return Results.NotFound();
            }

            var dto = new NubArca.Api.Party.PartyFaceSearchResponseDto(
                NubArca.Api.Domain.PartyFaceSearchStatuses.Ready,
                view.SearchId, view.FileItemIds.Count, PartyImageItems(token, view.FileItemIds));
            return Results.Ok(dto);
        }).WithName("GetPartyFaceSearch").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // Explicitly activate a face search as the album's TV face filter ("Show these
        // photos on TV"). Completing a search never touches the TV by itself — this is
        // the only bridge, and it stays token-scoped: the public caller addresses only
        // its own party's search; the backend maps that to the paired TV state (the
        // party client never calls /api/tv). Server-side ordering: the newest accepted
        // activation replaces the previous one; an empty search (409 no_matches) can
        // never be activated; a stale activation for a search older than the currently
        // active one is rejected (409 stale_search).
        app.MapPost("/api/party/{token}/face-search/{searchId:guid}/activate-tv", async (
            string token,
            Guid searchId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            var result = await faceSearch.ActivateForTvAsync(
                access.OwnerUserId, access.AlbumId, searchId, cancellationToken);

            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyFaceSearchActivateTv,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { status = result.Status.ToString() },
                cancellationToken: cancellationToken);

            return result.Status switch
            {
                NubArca.Api.Party.PartyFaceSearchActivationStatus.Activated =>
                    Results.Ok(new NubArca.Api.Party.PartyFaceSearchActivationDto(
                        searchId, result.ActivationVersion!.Value)),
                NubArca.Api.Party.PartyFaceSearchActivationStatus.NoMatches =>
                    Results.Json(new { error = "no_matches" }, statusCode: StatusCodes.Status409Conflict),
                NubArca.Api.Party.PartyFaceSearchActivationStatus.StaleSearch =>
                    Results.Json(new { error = "stale_search" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        }).WithName("ActivatePartyFaceSearchTv").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // Cancel/delete a face search (session + rank rows + stored face crop) from the
        // guest's phone. Row-scoped by search id, so cancelling an older search never
        // removes a newer active TV filter; if THIS search is the active one, deleting
        // it deactivates the TV filter automatically. Idempotent — repeated or
        // concurrent (phone + TV) deletion completes safely with 204.
        app.MapDelete("/api/party/{token}/face-search/{searchId:guid}", async (
            string token,
            Guid searchId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            await faceSearch.DeleteAsync(access.OwnerUserId, access.AlbumId, searchId, cancellationToken);

            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyFaceSearchDelete,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { source = "party" },
                cancellationToken: cancellationToken);
            return Results.NoContent();
        }).WithName("DeletePartyFaceSearch").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // Owner-only party-mode status for an album. Normal user cookie (never the TV
        // session). Foreign/missing → generic 404. PartyUrl (derived, relative) is
        // present only while an active link exists; never a token hash.
        app.MapGet("/api/albums/{id:guid}/party-settings", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await party.GetOwnerStatusAsync(ownerUserId, id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("GetAlbumPartySettings").RequireAuthorization();

        // Owner-only enable/disable of PUBLIC party mode on an album. Enabling implies
        // ShowOnTv=true; the first enable mints view+upload tokens, and the optional
        // `uploadEnabled` sub-switch toggles anonymous upload without rotating the view
        // token. Disabling revokes ALL public access (view + upload) immediately.
        // Foreign/missing → generic 404. Audited (never the token/hash).
        app.MapMethods("/api/albums/{id:guid}/party-settings", ["PATCH"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] IAuditLogger audit,
            [FromBody] SetAlbumPartyModeRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
                return Results.BadRequest(new { error = "Missing request body." });

            if (body.Enabled)
            {
                // Capture the prior approval-mode so a change can be audited distinctly.
                var before = await party.GetOwnerStatusAsync(ownerUserId, id, cancellationToken);
                var enabled = await party.EnableAsync(
                    ownerUserId, id, ownerUserId, body.UploadEnabled, body.RequireUploadApproval, cancellationToken);
                if (enabled is null)
                    return Results.NotFound();
                await audit.LogAsync(ownerUserId, AuditActions.PartyEnable, AuditEntityTypes.PartyAlbum,
                    enabled.LinkId, ip, new { albumId = id, uploadEnabled = body.UploadEnabled }, cancellationToken);
                // Audit an approval-mode transition separately (never any token/hash).
                if (body.RequireUploadApproval is bool wantApproval
                    && (before is null || before.RequireUploadApproval != wantApproval))
                {
                    await audit.LogAsync(
                        ownerUserId,
                        wantApproval ? AuditActions.PartyApprovalModeEnable : AuditActions.PartyApprovalModeDisable,
                        AuditEntityTypes.PartyAlbum, enabled.LinkId, ip, new { albumId = id }, cancellationToken);
                }
            }
            else
            {
                var ok = await party.DisableAsync(ownerUserId, id, cancellationToken);
                if (!ok)
                    return Results.NotFound();
                await audit.LogAsync(ownerUserId, AuditActions.PartyRevoke, AuditEntityTypes.PartyAlbum,
                    id, ip, new { albumId = id }, cancellationToken);
            }

            var status = await party.GetOwnerStatusAsync(ownerUserId, id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("SetAlbumPartyMode").RequireAuthorization();

        // Owner-side moderation of anonymous party uploads. Owner-authenticated (normal
        // user session). Lets the owner see guest-uploaded items and their moderation
        // state, hide/remove unwanted ones from the public party/TV surfaces, and
        // approve/reject uploads when approval mode is on. Foreign/missing → generic 404.
        // Safe DTOs only (logical file id + name + status + owner-auth thumbnail path).
        app.MapGet("/api/albums/{albumId:guid}/party-uploads", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var list = await moderation.ListAsync(ownerUserId, albumId, cancellationToken);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("ListPartyUploads").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/hide", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Hidden, AuditActions.PartyUploadHide, cancellationToken))
            .WithName("HidePartyUpload").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/approve", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Approved, AuditActions.PartyUploadApprove, cancellationToken))
            .WithName("ApprovePartyUpload").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/reject", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Rejected, AuditActions.PartyUploadReject, cancellationToken))
            .WithName("RejectPartyUpload").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/restore", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Approved, AuditActions.PartyUploadRestore, cancellationToken))
            .WithName("RestorePartyUpload").RequireAuthorization();

        return app;
    }

    // Party-safe DERIVED media bytes. Images: thumbnail=small, preview/download=
    // medium. Videos: thumbnail/preview=poster (no playback/download). Every served
    // image is metadata-stripped (EXIF/GPS/IPTC/XMP/ICC removed) before it leaves
    // the server.
    private static async Task<IResult> ServePartyMediaAsync(
        string token,
        Guid fileId,
        string variant,
        HttpContext httpContext,
        NubArca.Api.Party.IPartyLinkService party,
        NubArca.Api.Party.IPartyMediaService partyMedia,
        IFileThumbnailService thumbnails,
        NubArca.Api.Metadata.IImageMetadataStripper stripper,
        CancellationToken cancellationToken)
    {
        var access = await party.ResolvePublicAsync(token, cancellationToken);
        if (access is null)
        {
            return Results.NotFound();
        }

        var kind = await partyMedia.GetVisibleMediaKindAsync(
            access.OwnerUserId, access.AlbumId, fileId, cancellationToken);
        if (kind is null)
        {
            return Results.NotFound();
        }

        // Videos are view-only posters; no download.
        if (kind == NubArca.Api.Party.PartyMediaKind.Video && variant == "download")
        {
            return Results.NotFound();
        }

        string size = kind == NubArca.Api.Party.PartyMediaKind.Video
            ? ThumbnailSizes.Poster
            : (variant == "thumbnail" ? ThumbnailSizes.Small : ThumbnailSizes.Medium);

        var content = await thumbnails.EnsureAsync(fileId, access.OwnerUserId, size, cancellationToken);
        if (content is null)
        {
            return Results.NotFound();
        }

        // The derivative retains the source EXIF/GPS — strip before serving. JPEG
        // (and PNG) are supported; any other type is refused rather than risk a leak.
        if (!stripper.IsSupported(content.MimeType))
        {
            await content.Content.DisposeAsync();
            return Results.NotFound();
        }

        MemoryStream safe;
        try
        {
            await using (content.Content)
            {
                safe = await stripper.StripAsync(content.Content, content.MimeType, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Results.NotFound();
        }

        SetPrivateDerivativeCache(httpContext);
        return variant == "download"
            ? Results.File(safe, content.MimeType, $"photo-{fileId.ToString("N")[..8]}.jpg")
            : Results.File(safe, content.MimeType);
    }

    private static IReadOnlyList<NubArca.Api.Party.PartyItemDto> PartyImageItems(
        string token, IReadOnlyList<Guid> ids)
    {
        var enc = Uri.EscapeDataString(token);
        return ids.Select(id => new NubArca.Api.Party.PartyItemDto(
            id,
            "image",
            $"/api/party/{enc}/media/{id}/thumbnail",
            $"/api/party/{enc}/media/{id}/preview",
            $"/api/party/{enc}/media/{id}/download")).ToList();
    }

    // Shared owner-moderation action: set a guest upload's status + audit it (album
    // + file id only, never token/hash/storage internals). 404 when foreign/missing.
    private static async Task<IResult> ModeratePartyUploadAsync(
        HttpContext httpContext,
        NubArca.Api.Party.IPartyModerationService moderation,
        IAuditLogger audit,
        Guid albumId,
        Guid fileItemId,
        string status,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var ownerUserId = httpContext.GetCurrentUserId()!.Value;
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        var ok = await moderation.SetStatusAsync(
            ownerUserId, albumId, fileItemId, status, ownerUserId, cancellationToken);
        if (!ok)
        {
            return Results.NotFound();
        }
        await audit.LogAsync(ownerUserId, auditAction, AuditEntityTypes.PartyAlbum,
            albumId, ip, new { albumId, fileItemId }, cancellationToken);
        return Results.NoContent();
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

// Party request bodies. Moved from Program.cs's top-level records — used
// exclusively by the SetAlbumPartyMode endpoint above.
public sealed record SetAlbumPartyModeRequest(bool Enabled, bool? UploadEnabled = null, bool? RequireUploadApproval = null);

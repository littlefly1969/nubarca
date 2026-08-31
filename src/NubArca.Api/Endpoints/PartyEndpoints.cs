using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
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
    private const string PartyMessageRateLimitPolicy = "party-message";

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

            var links = await party.GetActivePartyUrlsAsync(
                access.OwnerUserId, [access.AlbumId], cancellationToken);
            links.TryGetValue(access.AlbumId, out var urls);
            var enc = Uri.EscapeDataString(token);
            var coverUrl = header.CoverFileItemId is Guid cover
                ? $"/api/party/{enc}/media/{cover}/preview" : null;
            var gameEnabled = await httpContext.RequestServices.GetRequiredService<AppDbContext>()
                .PartyAlbumLinks.AsNoTracking()
                .AnyAsync(x => x.Id == access.PartyAlbumLinkId && x.GameEnabled, cancellationToken);
            return Results.Ok(new NubArca.Api.Party.PartyAlbumDto(
                header.Name, header.ItemCount, coverUrl, urls?.UploadUrl, gameEnabled));
        }).WithName("GetPartyAlbum").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapGet("/api/party/{token}/challenges", async (
            string token, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is null) return Results.NotFound();
            var result = await challenges.ListGuestAsync(access, participantId.Value, cancellationToken);
            if (result is null) return Results.NotFound();
            var enc = Uri.EscapeDataString(token);
            return Results.Ok(result with
            {
                Items = result.Items.Select(x => x with
                {
                    MediaUrl = x.MediaUrl is null ? null
                        : $"/api/party/{enc}/challenges/{x.Id}/media",
                }).ToList(),
            });
        }).WithName("ListPartyGuestChallenges").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapPut("/api/party/{token}/challenges/{challengeId:guid}/vote", async (
            string token, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is null) return Results.NotFound();
            var result = await challenges.VoteAsync(access, participantId.Value, challengeId, true, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("VotePartyChallenge").RequireRateLimiting(PartyMessageRateLimitPolicy);

        app.MapDelete("/api/party/{token}/challenges/{challengeId:guid}/vote", async (
            string token, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is null) return Results.NotFound();
            var result = await challenges.VoteAsync(access, participantId.Value, challengeId, false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("UnvotePartyChallenge").RequireRateLimiting(PartyMessageRateLimitPolicy);

        app.MapGet("/api/party/{token}/challenges/{challengeId:guid}/media", async (
            string token, Guid challengeId, HttpContext httpContext,
            [FromServices] AppDbContext db,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var fileId = await db.PartyChallenges.AsNoTracking()
                .Where(x => x.Id == challengeId && x.AlbumId == access.AlbumId && x.IsEnabled)
                .Select(x => x.MediaFileItemId).FirstOrDefaultAsync(cancellationToken);
            return fileId is Guid id
                ? await ServePartyMediaAsync(token, id, "preview", httpContext, party, partyMedia,
                    thumbnails, stripper, cancellationToken)
                : Results.NotFound();
        }).WithName("GetPartyChallengeMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

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
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
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

            // Resolve the participant SERVER-side rather than trusting anything in
            // the request body: a client-supplied id would be a quota the client
            // can reset. Works even when the page never called /upload-session —
            // the session is created here instead.
            var participant = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);

            var acceptedPhotos = 0;
            var acceptedVideos = 0;
            var rejected = 0;
            var quotaRejectedPhotos = 0;
            var quotaRejectedVideos = 0;
            foreach (var file in files)
            {
                NubArca.Api.Party.PartyUploadOutcome outcome;
                try
                {
                    await using var stream = file.OpenReadStream();
                    outcome = await uploads.UploadAsync(
                        access.OwnerUserId, access.AlbumId,
                        file.FileName, file.ContentType, file.Length, stream,
                        access.PartyAlbumLinkId, access.RequireUploadApproval,
                        participant, access.MaxPhotoUploadsPerParticipant,
                        access.MaxVideoUploadsPerParticipant, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    outcome = NubArca.Api.Party.PartyUploadOutcome.Failed;
                }

                switch (outcome)
                {
                    case NubArca.Api.Party.PartyUploadOutcome.AcceptedPhoto: acceptedPhotos++; break;
                    case NubArca.Api.Party.PartyUploadOutcome.AcceptedVideo: acceptedVideos++; break;
                    case NubArca.Api.Party.PartyUploadOutcome.QuotaPhotoExhausted:
                        quotaRejectedPhotos++; rejected++; break;
                    case NubArca.Api.Party.PartyUploadOutcome.QuotaVideoExhausted:
                        quotaRejectedVideos++; rejected++; break;
                    default: rejected++; break;
                }
            }

            var accepted = acceptedPhotos + acceptedVideos;

            // Aggregate-only audit (no token/hash, no file names, no participant
            // id, no storage internals).
            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyUpload,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.AlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new
                {
                    accepted,
                    rejected,
                    acceptedPhotos,
                    acceptedVideos,
                    quotaRejectedPhotos,
                    quotaRejectedVideos,
                },
                cancellationToken: cancellationToken);

            var quota = participant is Guid id && access.PartyAlbumLinkId is Guid linkId
                ? await participants.GetQuotaAsync(linkId, id, cancellationToken)
                : null;
            return Results.Ok(new NubArca.Api.Party.PartyUploadResultDto(
                accepted, rejected, acceptedPhotos, acceptedVideos,
                quotaRejectedPhotos, quotaRejectedVideos,
                Remaining(quota?.MaxPhotos ?? 0, quota?.UsedPhotos ?? 0),
                Remaining(quota?.MaxVideos ?? 0, quota?.UsedVideos ?? 0)));
        }).WithName("PartyUpload").RequireRateLimiting(PartyUploadRateLimitPolicy).DisableAntiforgery();

        // PUBLIC party UPLOAD SESSION (anonymous, upload-token scoped). Idempotent:
        // it validates the upload token exactly like /upload does, then creates or
        // reuses this guest's participant session and reports what they may still
        // upload. The response carries NO participant id and NO token — the
        // identity lives only in an HttpOnly cookie the page cannot read.
        app.MapPost("/api/party/{token}/upload-session", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null || access.PartyAlbumLinkId is not Guid linkId)
            {
                return Results.NotFound();
            }

            var participantId = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is not Guid id)
            {
                return Results.NotFound();
            }

            var quota = await participants.GetQuotaAsync(linkId, id, cancellationToken);
            return Results.Ok(new NubArca.Api.Party.PartyUploadSessionDto(
                Unlimited(quota.MaxPhotos),
                Unlimited(quota.MaxVideos),
                quota.UsedPhotos,
                quota.UsedVideos,
                Remaining(quota.MaxPhotos, quota.UsedPhotos),
                Remaining(quota.MaxVideos, quota.UsedVideos)));
        }).WithName("PartyUploadSession").RequireRateLimiting(PartyUploadRateLimitPolicy).DisableAntiforgery();

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

        // Owner challenge deck. All operations are owner/album scoped; a media
        // reference is accepted only when it is a current member of this album.
        app.MapGet("/api/albums/{albumId:guid}/party-challenges", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            var result = await challenges.ListOwnerAsync(ownerId, albumId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("ListOwnerPartyChallenges").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-challenges", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            [FromBody] NubArca.Api.Party.PartyChallengeWriteRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null) return Results.BadRequest();
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            var result = await challenges.CreateAsync(ownerId, albumId, body, cancellationToken);
            return result is null ? Results.BadRequest() : Results.Created(
                $"/api/albums/{albumId}/party-challenges/{result.Id}", result);
        }).WithName("CreatePartyChallenge").RequireAuthorization();

        app.MapPut("/api/albums/{albumId:guid}/party-challenges/{challengeId:guid}", async (
            Guid albumId, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            [FromBody] NubArca.Api.Party.PartyChallengeWriteRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null) return Results.BadRequest();
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            var result = await challenges.UpdateAsync(ownerId, albumId, challengeId, body, cancellationToken);
            return result is null ? Results.BadRequest() : Results.Ok(result);
        }).WithName("UpdatePartyChallenge").RequireAuthorization();

        app.MapDelete("/api/albums/{albumId:guid}/party-challenges/{challengeId:guid}", async (
            Guid albumId, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            return await challenges.DeleteAsync(ownerId, albumId, challengeId, cancellationToken)
                ? Results.NoContent() : Results.NotFound();
        }).WithName("DeletePartyChallenge").RequireAuthorization();

        app.MapPut("/api/albums/{albumId:guid}/party-challenges/order", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            [FromBody] NubArca.Api.Party.PartyChallengeReorderRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body?.ChallengeIds is null) return Results.BadRequest();
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            return await challenges.ReorderAsync(ownerId, albumId, body.ChallengeIds, cancellationToken)
                ? Results.NoContent() : Results.BadRequest();
        }).WithName("ReorderPartyChallenges").RequireAuthorization();

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
                    ownerUserId, id, ownerUserId, body.UploadEnabled, body.RequireUploadApproval,
                    body.RequireMessageApproval, cancellationToken);
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
                // The MESSAGE approval mode is a separate decision from the upload
                // one and gets its own audit line, so "the host started reading
                // greetings first" is answerable without inferring it from a
                // photo-moderation event.
                if (body.RequireMessageApproval is bool wantMessageApproval
                    && (before is null || before.RequireMessageApproval != wantMessageApproval))
                {
                    await audit.LogAsync(
                        ownerUserId,
                        wantMessageApproval
                            ? AuditActions.PartyMessageApprovalModeEnable
                            : AuditActions.PartyMessageApprovalModeDisable,
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

        // Owner-only party SLIDESHOW/QUOTA settings. Deliberately a separate route
        // from party-settings: these four numbers are saved as a draft from the
        // owner panel and must never be able to rotate a token, flip the party or
        // upload switch, or change approval mode as a side effect. Validated
        // server-side against the SAME ranges the client shows; an out-of-range
        // value is a 400, never a silent clamp. Requires an active party link.
        app.MapMethods("/api/albums/{id:guid}/party-slideshow-settings", ["PATCH"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromBody] SetPartySlideshowSettingsRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            if (body.PhotoSlideSeconds is int photo && !PartySlideshowDefaults.IsValidPhotoSeconds(photo))
            {
                return Results.BadRequest(new { error = "photoSlideSeconds out of range." });
            }
            if (body.MaxVideoSlideSeconds is int video && !PartySlideshowDefaults.IsValidMaxVideoSeconds(video))
            {
                return Results.BadRequest(new { error = "maxVideoSlideSeconds out of range." });
            }
            if (body.MaxPhotoUploadsPerParticipant is int maxPhotos && !PartySlideshowDefaults.IsValidQuota(maxPhotos))
            {
                return Results.BadRequest(new { error = "maxPhotoUploadsPerParticipant out of range." });
            }
            if (body.MaxVideoUploadsPerParticipant is int maxVideos && !PartySlideshowDefaults.IsValidQuota(maxVideos))
            {
                return Results.BadRequest(new { error = "maxVideoUploadsPerParticipant out of range." });
            }

            var ok = await party.UpdateSlideshowSettingsAsync(
                ownerUserId, id,
                body.PhotoSlideSeconds, body.MaxVideoSlideSeconds,
                body.MaxPhotoUploadsPerParticipant, body.MaxVideoUploadsPerParticipant,
                cancellationToken);
            if (!ok)
            {
                return Results.NotFound();
            }

            var status = await party.GetOwnerStatusAsync(ownerUserId, id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("SetPartySlideshowSettings").RequireAuthorization();

        app.MapMethods("/api/albums/{id:guid}/party-game-settings", ["PATCH"], async (
            Guid id, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromBody] NubArca.Api.Party.PartyGameSettingsRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null || !PartyChallengeDefaults.IsValid(
                body.MinChallengeIntervalSeconds, body.MaxChallengeIntervalSeconds,
                body.VotesPerGuest, body.MaxChallengesPerSession))
                return Results.BadRequest(new { error = "invalid_party_game_settings" });
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            if (!await party.UpdateGameSettingsAsync(ownerId, id, body.GameEnabled,
                body.MinChallengeIntervalSeconds, body.MaxChallengeIntervalSeconds,
                body.VotesPerGuest, body.MaxChallengesPerSession, cancellationToken))
                return Results.NotFound();
            return Results.Ok(await party.GetOwnerStatusAsync(ownerId, id, cancellationToken));
        }).WithName("SetPartyGameSettings").RequireAuthorization();

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

        // --- PUBLIC party MESSAGES (anonymous, upload-token scoped) ---
        //
        // A guest writes a short greeting instead of (or as well as) a photo. The
        // UPLOAD token authorizes it, not the view token: the message form lives on
        // the upload page, which is the only public surface that holds an upload
        // token, and turning off "guests may contribute" should stop written
        // contributions for the same reason it stops photographic ones. Every
        // request re-validates the link (enabled + upload on + not revoked/expired
        // + album still owner-owned and ShowOnTv), so revoking a party silences new
        // messages immediately.
        //
        // The response carries the id, the resulting status and the timestamp and
        // nothing else — never the owner, the party link, the participant, or the
        // token. The audit line carries the message id and the status; the BODY is
        // never logged anywhere.
        app.MapPost("/api/party/{token}/messages", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyMessageSubmitRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            // Provenance for an abuse investigation, never for display. A guest who
            // has not uploaded yet gets their participant session minted here.
            var participantId = await ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);

            var result = await messages.SubmitAsync(
                access, body.DisplayName, body.Text, participantId, cancellationToken);

            if (result.Error is NubArca.Api.Party.PartyMessageSubmissionError error)
            {
                // Safe, machine-readable codes. The client already enforces the same
                // limits from the same contract, so this is the backstop, not the
                // copy the guest normally reads.
                return Results.BadRequest(new
                {
                    error = error == NubArca.Api.Party.PartyMessageSubmissionError.InvalidDisplayName
                        ? "invalid_display_name"
                        : "invalid_text",
                    maxDisplayNameLength = PartyMessageLimits.MaxDisplayNameLength,
                    maxTextLength = PartyMessageLimits.MaxBodyLength,
                });
            }

            var message = result.Message!;
            await audit.LogAsync(
                userId: null,
                action: AuditActions.PartyMessageSubmit,
                entityType: AuditEntityTypes.PartyMessage,
                entityId: message.Id,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { albumId = access.AlbumId, status = message.Status },
                cancellationToken: cancellationToken);

            return Results.Ok(message);
        }).WithName("SubmitPartyMessage").RequireRateLimiting(PartyMessageRateLimitPolicy);

        // --- OWNER / DELEGATE party message moderation ---
        //
        // Authorization is `owner || activeMembership.CanManagePartyMessages`,
        // resolved in ONE place (IPartyMessageAccessResolver) and re-read on every
        // request. An album role never grants it: an `editor` without the
        // capability is refused here exactly like a stranger, and both get the same
        // generic 404 so neither can tell an album they may not manage from one
        // that does not exist.
        //
        // Every route is scoped to the album's CURRENTLY ACTIVE party, so a message
        // id from another album — or from the same album's previous party — is
        // simply not found rather than a probe.
        app.MapGet("/api/albums/{albumId:guid}/party-messages", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var list = await messages.ListForManagerAsync(albumId, actorUserId, cancellationToken);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("ListPartyMessages").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/approve", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyMessageAsync(
                httpContext, messages, audit, albumId, messageId,
                NubArca.Api.Domain.PartyMessageModeration.Approve,
                AuditActions.PartyMessageApprove, cancellationToken))
            .WithName("ApprovePartyMessage").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/reject", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyMessageAsync(
                httpContext, messages, audit, albumId, messageId,
                NubArca.Api.Domain.PartyMessageModeration.Reject,
                AuditActions.PartyMessageReject, cancellationToken))
            .WithName("RejectPartyMessage").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/hide", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyMessageAsync(
                httpContext, messages, audit, albumId, messageId,
                NubArca.Api.Domain.PartyMessageModeration.Hide,
                AuditActions.PartyMessageHide, cancellationToken))
            .WithName("HidePartyMessage").RequireAuthorization();

        // Restore lands on the same state as approve and is a SEPARATE route only
        // so the audit trail distinguishes "the host read it and let it through"
        // from "the host put back something they had taken down".
        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/restore", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyMessageAsync(
                httpContext, messages, audit, albumId, messageId,
                NubArca.Api.Domain.PartyMessageModeration.Restore,
                AuditActions.PartyMessageRestore, cancellationToken))
            .WithName("RestorePartyMessage").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/promote-hero", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await SetPartyMessageHeroAsync(
                httpContext, messages, audit, albumId, messageId, true,
                AuditActions.PartyMessageHeroPromote, cancellationToken))
            .WithName("PromotePartyMessageHero").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/demote-hero", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await SetPartyMessageHeroAsync(
                httpContext, messages, audit, albumId, messageId, false,
                AuditActions.PartyMessageHeroDemote, cancellationToken))
            .WithName("DemotePartyMessageHero").RequireAuthorization();

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

    // Shared manager action for a party MESSAGE: apply a transition, then audit
    // the message id and the album — never the body, the guest's name, the party
    // token, or the participant. A refused transition is a 400; everything else
    // that could have gone wrong (no such album, no such message, no authority,
    // no active party) is the same 404.
    //
    // The route carries an ACTION, not a target state. `approve` and `restore`
    // both end at visible but start from different places, and only the action
    // distinguishes them — which is what lets the domain refuse the transitions
    // no route is named after (visible → rejected, pending → hidden).
    private static async Task<IResult> ModeratePartyMessageAsync(
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid albumId,
        Guid messageId,
        NubArca.Api.Domain.PartyMessageModeration action,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var actorUserId = httpContext.GetCurrentUserId()!.Value;
        var result = await messages.ModerateAsync(
            albumId, actorUserId, messageId, action, cancellationToken);
        return await CompletePartyMessageMutationAsync(
            httpContext, audit, result, actorUserId, albumId, messageId, auditAction,
            new { albumId, messageId, action = action.ToString() }, cancellationToken);
    }

    private static async Task<IResult> SetPartyMessageHeroAsync(
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid albumId,
        Guid messageId,
        bool hero,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var actorUserId = httpContext.GetCurrentUserId()!.Value;
        var result = await messages.SetHeroAsync(
            albumId, actorUserId, messageId, hero, cancellationToken);
        return await CompletePartyMessageMutationAsync(
            httpContext, audit, result, actorUserId, albumId, messageId, auditAction,
            new { albumId, messageId, hero }, cancellationToken);
    }

    private static async Task<IResult> CompletePartyMessageMutationAsync(
        HttpContext httpContext,
        IAuditLogger audit,
        NubArca.Api.Party.PartyMessageMutation result,
        Guid actorUserId,
        Guid albumId,
        Guid messageId,
        string auditAction,
        object metadata,
        CancellationToken cancellationToken)
    {
        switch (result)
        {
            case NubArca.Api.Party.PartyMessageMutation.NotFound:
                return Results.NotFound();
            case NubArca.Api.Party.PartyMessageMutation.InvalidTransition:
                // The message is real and the caller may manage it; the domain
                // refused the move — promoting something not visible, or a
                // moderation action the state machine does not allow from where
                // the message currently is. Hiding that behind a 404 would make
                // a legitimate UI state unexplainable, so it is a 400.
                return Results.BadRequest(new { error = "invalid_transition" });
        }

        await audit.LogAsync(
            actorUserId, auditAction, AuditEntityTypes.PartyMessage, messageId,
            httpContext.Connection.RemoteIpAddress?.ToString(), metadata, cancellationToken);
        return Results.NoContent();
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
    // Cookie name for the anonymous guest's participant session. ONE name for
    // every party: the cookie is PATH-scoped to this upload token's API prefix,
    // so a guest attending two parties holds two cookies the browser keeps apart
    // by path, and neither party can see or spend the other's allowance. Scoping
    // by name instead would have meant either leaking a link id into the cookie
    // name or overwriting the first party's session on arrival at the second.
    private const string PartyParticipantCookieName = "NubArca.PartyGuest";

    // Resolve (or mint) the guest's participant session and make sure the cookie
    // is set. The raw token exists for exactly one response; only its hash is
    // stored. Never reads a participant id from the request body or query — the
    // whole point is an identity the client did not choose.
    private static async Task<Guid?> ResolvePartyParticipantAsync(
        HttpContext context,
        NubArca.Api.Party.IPartyParticipantService participants,
        Guid? partyAlbumLinkId,
        string uploadToken,
        CancellationToken cancellationToken)
    {
        if (partyAlbumLinkId is not Guid linkId)
        {
            return null;
        }

        context.Request.Cookies.TryGetValue(PartyParticipantCookieName, out var existing);
        var resolution = await participants.ResolveOrCreateAsync(linkId, existing, cancellationToken);
        if (resolution.NewRawToken is string issued)
        {
            context.Response.Cookies.Append(PartyParticipantCookieName, issued, new CookieOptions
            {
                HttpOnly = true,
                // Secure only over HTTPS: a party is often demoed over plain
                // http on a LAN, and an unconditional Secure flag would silently
                // drop the cookie there, handing every upload a fresh quota.
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                // Narrowest path that still covers this link's upload calls.
                Path = $"/api/party/{uploadToken}",
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true,
            });
        }
        return resolution.ParticipantId;
    }

    // Domain 0 means unlimited; the public DTO says null so a client cannot read
    // "no limit" as "no slots left".
    private static int? Unlimited(int max) => max > 0 ? max : null;

    private static int? Remaining(int max, int used) => max > 0 ? Math.Max(0, max - used) : null;

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
public sealed record SetAlbumPartyModeRequest(
    bool Enabled,
    bool? UploadEnabled = null,
    bool? RequireUploadApproval = null,
    // Owner-only, and deliberately on THIS route rather than a message route: a
    // delegate moderates messages but never changes what the party requires.
    bool? RequireMessageApproval = null);

// A guest's greeting. Plain text only — the server normalises and measures it
// (PartyMessageText) and is the authority on both limits.
public sealed record PartyMessageSubmitRequest(string? DisplayName = null, string? Text = null);

// Owner-side slideshow timing + per-participant quotas. Every field is optional
// so the panel can save just what changed, and NONE of them touches the party
// tokens, the party/upload switches or the approval mode.
public sealed record SetPartySlideshowSettingsRequest(
    int? PhotoSlideSeconds = null,
    int? MaxVideoSlideSeconds = null,
    int? MaxPhotoUploadsPerParticipant = null,
    int? MaxVideoUploadsPerParticipant = null);

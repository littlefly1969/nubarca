using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Access;

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

    // The two public Party policies the guest book shares. It is deliberately
    // not given policies of its own: reading the book is an ordinary public
    // Party read and writing in it is a public Party write, so they belong in
    // the same windows as the surfaces beside them rather than in a third
    // budget an operator would have to learn about.
    internal const string PublicRateLimitPolicy = PartyPublicRateLimitPolicy;
    internal const string MessageRateLimitPolicy = PartyMessageRateLimitPolicy;
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
            [FromServices] NubArca.Api.Party.IPartyGuestContentService guestContent,
            [FromServices] AppDbContext db,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }

            var header = await partyMedia.GetAlbumAsync(access.OwnerUserId, access.MainAlbumId, cancellationToken);
            if (header is null)
            {
                return Results.NotFound();
            }

            // The PARTY names itself now, not its album: they have been separate
            // things since the host could rename either without the other.
            var root = await db.Parties.AsNoTracking()
                .Where(p => p.Id == access.PartyId)
                .Select(p => new
                {
                    p.Title, p.EventStartsAt, p.Version,
                    p.InvitationCoverFileItemId, p.LiveCoverFileItemId,
                })
                .FirstOrDefaultAsync(cancellationToken);
            var partyTitle = root?.Title ?? header.Name;
            var eventStartsAt = root?.EventStartsAt;

            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyPublicView,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.MainAlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: null,
                cancellationToken: cancellationToken);

            var enc = Uri.EscapeDataString(token);
            var content = await guestContent.ForGuestAsync(
                access.PartyId, access.OwnerUserId, access.Experience.Phase, token, cancellationToken);

            // WHICH PHOTOGRAPH OPENS THE PAGE. The party's own choices come first
            // (PartyCoverPolicy): on the invitation its cover; at the party and
            // in the memories the live cover, then the invitation's. Each is a
            // Party reference that may be in no album, so it arrives on its own
            // relation-scoped address rather than the album route, which would
            // rightly refuse it.
            //
            // Without one, the album — before the party only a cover the host
            // CHOSE, because the gallery is closed and whichever photo sorts
            // first is not a decision; afterwards its cover as every surface
            // resolves it. Without that, the page's composition.
            //
            // The invitation SLOT's photograph is not a candidate: it is part of
            // what the invitation says, and sits in its section.
            var coverUrl = await GuestCoverUrlAsync(
                db, access, header, root?.InvitationCoverFileItemId, root?.LiveCoverFileItemId,
                root?.Version ?? 0, enc, cancellationToken);

            // A capability the HOST is no longer permitted to run, or that does
            // not belong to this phase, is ABSENT from the hub — never a
            // disabled tile. `access.Capabilities` was resolved once with the
            // token, with the phase already folded in, so this costs no extra
            // query and cannot disagree with what the routes themselves allow.
            var live = access.Experience.AllowsLiveCapabilities;
            var gameEnabled = access.Capabilities.Games
                && await httpContext.RequestServices.GetRequiredService<AppDbContext>()
                    .PartyAlbumLinks.AsNoTracking()
                    .AnyAsync(x => x.Id == access.PartyAlbumLinkId && x.GameEnabled, cancellationToken);

            string? contributionUrl = null;
            string? printUrl = null;
            if (live)
            {
                var links = await party.GetActivePartyUrlsAsync(
                    access.OwnerUserId, [access.MainAlbumId], cancellationToken);
                links.TryGetValue(access.MainAlbumId, out var urls);
                contributionUrl = access.Capabilities.Contributions ? urls?.UploadUrl : null;

                // Printing is offered only when it would actually work right
                // now. The resolver re-checks the whole chain — the host's
                // `party.print` permission, the phase, profile, station, printer
                // format, budget — so a card never appears for a party that
                // cannot print, and disappears the moment it stops being able to.
                printUrl = await httpContext.RequestServices
                    .GetRequiredService<NubArca.Api.Party.IPartyPrintUrlProvider>()
                    .GetAsync(access.PartyAlbumLinkId, access.MainAlbumId, cancellationToken);
            }

            return Results.Ok(new NubArca.Api.Party.PartyGuestContextDto(
                partyTitle,
                NubArca.Api.Domain.PartyGuestPhases.Wire(access.Experience.Phase),
                NubArca.Api.Domain.PartyGuestAccessModes.Wire(access.Experience.Access),
                eventStartsAt,
                // The album is named only where it means something: during the
                // party and in the memories. An invitation is not an album.
                access.Experience.AllowsAlbumMedia ? header.Name : null,
                access.Experience.AllowsAlbumMedia ? header.ItemCount : 0,
                coverUrl,
                content,
                new NubArca.Api.Party.PartyGuestCapabilitiesDto(
                    contributionUrl,
                    // Same rule as printing: the capability states where it
                    // lives, and its absence is the whole answer.
                    gameEnabled ? NubArca.Api.Party.PartyLinkService.BuildGameUrl(token) : null,
                    printUrl,
                    access.Capabilities.FaceSearch,
                    // THE SLIDESHOW COMPOSER. Its own field rather than the
                    // client deriving it from the contribution URL, because
                    // deriving it is exactly how a party that takes no
                    // greetings ends up offering somewhere to write one. Null
                    // whenever the party takes none — and the hub has no card,
                    // no tab, no empty state and no feed to fetch.
                    access.SlideshowMessagesEnabled && contributionUrl is not null
                        ? NubArca.Api.Party.PartyContributionModes.Message(contributionUrl)
                        : null,
                    // THE BOOK, which outlives the composer: readable for as
                    // long as the memories are, so it is not inside the `live`
                    // branch above.
                    access.GuestbookEnabled && access.Experience.AllowsAlbumMedia
                        ? NubArca.Api.Party.PartyLinkService.BuildGuestbookUrl(token)
                        : null),
                new NubArca.Api.Party.PartyGuestLibraryDto(
                    access.Experience.LibraryAvailable,
                    access.Experience.LibraryAccessEndsAt)));
        }).WithName("GetPartyAlbum").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // THE LINK PREVIEW: what a chat app draws when somebody pastes a party's
        // link — the party's name, one line about it, and the picture its page
        // opens on. The frontend's nginx sends only link-preview fetchers here
        // (a person gets the SPA), because those fetchers run no JavaScript.
        //
        // It says exactly what opening the link would show and nothing more.
        // A token that does not open a party — unknown, draft, revoked, over —
        // gets the generic NubArca card: one answer for all of them. Not audited
        // as a view: a crawler drawing a card is not a guest visiting.
        app.MapGet("/api/party/{token}/link-preview", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            [FromServices] AppDbContext db,
            [FromServices] IOptionsMonitor<NubArca.Api.Auth.Recovery.MailOptions> mail,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var english = httpContext.Request.Headers.AcceptLanguage.ToString()
                .StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var origin = NubArca.Api.Party.PartyLinkPreview.Origin(mail.CurrentValue.PublicOrigin);
            const string html = "text/html; charset=utf-8";

            var access = await party.ResolvePublicAsync(token, cancellationToken);
            var header = access is null
                ? null
                : await partyMedia.GetAlbumAsync(access.OwnerUserId, access.MainAlbumId, cancellationToken);
            var root = access is null
                ? null
                : await db.Parties.AsNoTracking()
                    .Where(p => p.Id == access.PartyId)
                    .Select(p => new
                    {
                        p.Title, p.EventStartsAt, p.Version,
                        p.InvitationCoverFileItemId, p.LiveCoverFileItemId,
                    })
                    .FirstOrDefaultAsync(cancellationToken);
            if (access is null || header is null || root is null)
            {
                return Results.Content(NubArca.Api.Party.PartyLinkPreview.Generic(origin), html);
            }

            var enc = Uri.EscapeDataString(token);
            var cover = await GuestCoverUrlAsync(
                db, access, header, root.InvitationCoverFileItemId, root.LiveCoverFileItemId,
                root.Version, enc, cancellationToken);
            return Results.Content(NubArca.Api.Party.PartyLinkPreview.ForParty(
                origin,
                $"/party/{enc}",
                root.Title,
                NubArca.Api.Party.PartyLinkPreview.Describe(
                    access.Experience.Phase, root.EventStartsAt, english),
                cover), html);
        }).WithName("GetPartyLinkPreview").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapGet("/api/party/{token}/challenges", async (
            string token, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyParticipantService participants,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            var participantId = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
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
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            // VOTE NEVER MINTS IDENTITY — the same rule as the hosted game, and
            // for the same reason: a cookie is a claim, and a claim the server
            // never issued must not become a vote. This is resolve-only, so a
            // caller with no guest session is refused before a participant, a
            // vote or a budget counter exists. The ordinary flow establishes the
            // session on GET /challenges, which is how a guest arrives anyway.
            var participantId = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            if (participantId is null)
            {
                // A conflict rather than a denial: it is a state the caller can
                // leave, by opening the party surface. `error` rather than
                // `code` keeps the shape the legacy challenge API already uses.
                return Results.Json(
                    new { error = "not_joined" }, statusCode: StatusCodes.Status409Conflict);
            }
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
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            // VOTE NEVER MINTS IDENTITY — the same rule as the hosted game, and
            // for the same reason: a cookie is a claim, and a claim the server
            // never issued must not become a vote. This is resolve-only, so a
            // caller with no guest session is refused before a participant, a
            // vote or a budget counter exists. The ordinary flow establishes the
            // session on GET /challenges, which is how a guest arrives anyway.
            var participantId = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            if (participantId is null)
            {
                // A conflict rather than a denial: it is a state the caller can
                // leave, by opening the party surface. `error` rather than
                // `code` keeps the shape the legacy challenge API already uses.
                return Results.Json(
                    new { error = "not_joined" }, statusCode: StatusCodes.Status409Conflict);
            }
            var result = await challenges.VoteAsync(access, participantId.Value, challengeId, false, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("UnvotePartyChallenge").RequireRateLimiting(PartyMessageRateLimitPolicy);

        // An activity's picture, reached THROUGH the activity. It is a Party
        // reference rather than album media, so it deliberately does not go
        // through the album route: the owner may give an activity any of their
        // own images, and one that is in no album must work here while staying
        // unreachable there. Outside a running game — no Games capability, which
        // already folds in the phase — there is nothing to reach it through.
        app.MapGet("/api/party/{token}/challenges/{challengeId:guid}/media", async (
            string token, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            var fileId = await challenges.GuestMediaFileAsync(access, challengeId, cancellationToken);
            return fileId is Guid id
                ? await ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, id, NubArca.Api.Party.PartyMediaKind.Image, "preview",
                    httpContext, thumbnails, stripper, cancellationToken)
                : Results.NotFound();
        }).WithName("GetPartyChallengeMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        // A guest-content slot's photograph — the menu's, the invitation's —
        // reached THROUGH the slot. A party token is not a grant over the owner's
        // files: it reaches exactly the file a slot on the guest's CURRENT
        // surface references, and none of the owner's others, however well their
        // ids are guessed. A slot that is missing, disabled, scoped to another
        // phase or without a photograph, and a photograph that stopped
        // qualifying, are all the same generic 404. Only the derived preview is
        // served: seeing a picture on the menu is not permission to download
        // the owner's original.
        app.MapGet("/api/party/{token}/content/{kind}/media", async (
            string token, string kind, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyGuestContentService guestContent,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var fileId = await guestContent.GuestMediaFileAsync(
                access.PartyId, access.OwnerUserId, access.Experience.Phase, kind, cancellationToken);
            return fileId is Guid id
                ? await ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, id, NubArca.Api.Party.PartyMediaKind.Image, "preview",
                    httpContext, thumbnails, stripper, cancellationToken)
                : Results.NotFound();
        }).WithName("GetPartyContentMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        // THE COVER, on its own relation-scoped address. Only the photograph the
        // page is showing RIGHT NOW resolves — asked through the same policy the
        // guest context used — so the invitation's cover stops answering here
        // the moment a live cover outranks it, neither answers for a file that
        // stopped qualifying, and no other file of the owner's answers at all.
        // Only the derived preview is served, never the original.
        app.MapGet("/api/party/{token}/cover/{which}/media", async (
            string token, string which, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] AppDbContext db,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var chosen = await NubArca.Api.Party.PartyCoverPolicy.ResolveAsync(db, access, cancellationToken);
            return chosen is { } pick && pick.Which == which
                ? await ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, pick.FileId, NubArca.Api.Party.PartyMediaKind.Image, "preview",
                    httpContext, thumbnails, stripper, cancellationToken)
                : Results.NotFound();
        }).WithName("GetPartyCoverMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        app.MapGet("/api/party/{token}/items", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyMediaService partyMedia,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            // The gallery IS album media, so it obeys the same rule the bytes
            // do: absent before the party, present during it, and afterwards for
            // exactly as long as the memories last.
            if (access is null || !access.Experience.AllowsAlbumMedia)
            {
                return Results.NotFound();
            }

            var header = await partyMedia.GetAlbumAsync(access.OwnerUserId, access.MainAlbumId, cancellationToken);
            var items = await partyMedia.ListItemsAsync(access.OwnerUserId, access.MainAlbumId, cancellationToken);
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
            if (access is null || !access.Capabilities.Contributions)
            {
                return Results.NotFound();
            }

            // THE PHOTO SWITCH, asked here rather than by the token. A party
            // that takes greetings and dedications but no photographs is a real
            // party, and its guests must still reach the page — so this refusal
            // belongs to the upload, not to the link. Same shape as the
            // composer's: well formed, and declined by configuration.
            if (!access.UploadEnabled)
            {
                return Results.Json(
                    new { error = "party_uploads_disabled" },
                    statusCode: StatusCodes.Status409Conflict);
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
            var participant = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);

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
                        access.OwnerUserId, access.MainAlbumId,
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
                actor: null,
                action: AuditActions.PartyUpload,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.MainAlbumId,
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
            if (access is null || !access.Capabilities.Contributions)
            {
                return Results.NotFound();
            }
            var linkId = access.PartyAlbumLinkId;

            var participantId = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            if (participantId is not Guid id)
            {
                return Results.NotFound();
            }

            var quota = await participants.GetQuotaAsync(linkId, id, cancellationToken);
            // Greetings are a guest allowance like the media ones, so they are
            // reported beside them rather than through a surface of their own.
            var usedMessages = await participants.MessageCountAsync(id, cancellationToken);
            var usedGuestbook = await participants.GuestbookCountAsync(id, cancellationToken);
            return Results.Ok(new NubArca.Api.Party.PartyUploadSessionDto(
                Unlimited(quota.MaxPhotos),
                Unlimited(quota.MaxVideos),
                quota.UsedPhotos,
                quota.UsedVideos,
                Remaining(quota.MaxPhotos, quota.UsedPhotos),
                Remaining(quota.MaxVideos, quota.UsedVideos),
                Unlimited(access.MaxMessagesPerParticipant),
                usedMessages,
                Remaining(access.MaxMessagesPerParticipant, usedMessages),
                // WHETHER THE WRITTEN HALF OF THIS PAGE EXISTS. Reported here
                // rather than left to the page to assume, so a party that takes
                // photographs and no greetings renders a page about
                // photographs — with no composer, no switch between two halves
                // and nothing to ask the server for.
                access.SlideshowMessagesEnabled,
                access.UploadEnabled,
                access.GuestbookEnabled,
                Unlimited(access.MaxGuestbookEntriesPerParticipant),
                usedGuestbook,
                Remaining(access.MaxGuestbookEntriesPerParticipant, usedGuestbook),
                $"/party/{Uri.EscapeDataString(party.DeriveViewToken(linkId))}"));
        }).WithName("PartyUploadSession").RequireRateLimiting(PartyUploadRateLimitPolicy).DisableAntiforgery();

        // PUBLIC party FACE SEARCH (anonymous, VIEW-token scoped). A guest uploads one
        // selfie; the backend detects the most prominent face, embeds it with the SAME
        // face package as the owner's library, and returns the currently-visible members
        // of THIS party album that match. Album-scoped + owner-scoped. The selfie and the
        // query embedding are NEVER stored; the response carries NO similarity score,
        // face id, person id, person name, or vector. When AI / the face model is
        // disabled or unavailable a safe "unavailable" state is returned (503). The
        // tightest per-IP party window applies (detection + embedding is expensive).
        // DETECT ONLY, before any search. The guest's phone asks "can you see my
        // face, and where is it?", and gets an answer with no embedding, no
        // matching, no session row and nothing recorded.
        //
        // It exists so the flow on the phone can be honest. "We cannot see your
        // face" and "there are several people here" are now reached WITHOUT a
        // face search — the guest retakes the selfie before anything expensive
        // or private is spent — and the box that comes back is the box the
        // search will use, which is what lets the scanner show the guest the
        // crop their results really come from.
        //
        // Same rate-limit window as the search itself: detection is the
        // expensive half, and a cheaper budget here would be a way around it.
        app.MapPost("/api/party/{token}/face-search/detect", async (
            string token,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null || !access.Capabilities.FaceSearch)
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

            var outcome = await faceSearch.DetectAsync(bytes, file.ContentType, cancellationToken);

            // NOT AUDITED. The search's line records that a guest searched this
            // party's album; a detection decided nothing about the album, and a
            // trail that counted "somebody pointed a camera at themselves" would
            // be collecting the one thing this feature promises not to keep.
            var dto = new NubArca.Api.Party.PartyFaceDetectResponseDto(
                outcome.Status,
                outcome.Face is { } box
                    ? new NubArca.Api.Party.PartyFaceBoxDto(box.X, box.Y, box.Width, box.Height)
                    : null,
                outcome.SelectionToken);

            return outcome.Status switch
            {
                NubArca.Api.Domain.PartyFaceSearchStatuses.Unavailable =>
                    Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable),
                NubArca.Api.Domain.PartyFaceSearchStatuses.InvalidImage =>
                    Results.Json(dto, statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Ok(dto),
            };
        }).WithName("PartyFaceDetect")
            .RequireRateLimiting(PartyFaceSearchRateLimitPolicy).DisableAntiforgery();

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
            if (access is null || !access.Capabilities.FaceSearch)
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

            // The detection's ticket, travelling beside the selfie in the same
            // form. Never in the URL: a query string is the one place a value
            // reliably ends up in an access log and in somebody's history.
            var selectionToken = form["selectionToken"].ToString();

            var outcome = await faceSearch.SearchAsync(
                access.OwnerUserId, access.MainAlbumId, access.PartyAlbumLinkId,
                bytes, file.ContentType, selectionToken, cancellationToken);

            // Aggregate-only audit (never the selfie, token/hash, query vector, file
            // names, or storage internals).
            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyFaceSearch,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.MainAlbumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { status = outcome.Status, resultCount = outcome.ResultCount },
                cancellationToken: cancellationToken);

            var dto = new NubArca.Api.Party.PartyFaceSearchResponseDto(
                outcome.Status, outcome.SearchId, outcome.ResultCount,
                PartyImageItems(token, outcome.FileItemIds),
                outcome.Face is { } box
                    ? new NubArca.Api.Party.PartyFaceBoxDto(
                        box.X, box.Y, box.Width, box.Height)
                    : null);

            return outcome.Status switch
            {
                NubArca.Api.Domain.PartyFaceSearchStatuses.Unavailable =>
                    Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable),
                NubArca.Api.Domain.PartyFaceSearchStatuses.InvalidImage =>
                    Results.Json(dto, statusCode: StatusCodes.Status400BadRequest),
                // The request was well formed and nothing is wrong with the
                // photograph: what is missing is a confirmation of which face
                // it is about.
                NubArca.Api.Domain.PartyFaceSearchStatuses.InvalidSelection =>
                    Results.Json(dto, statusCode: StatusCodes.Status400BadRequest),
                // A conflict in the plainest sense: the selfie no longer agrees
                // with the decision the guest was shown.
                NubArca.Api.Domain.PartyFaceSearchStatuses.FaceSelectionChanged =>
                    Results.Json(dto, statusCode: StatusCodes.Status409Conflict),
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
            if (access is null || !access.Capabilities.FaceSearch)
            {
                return Results.NotFound();
            }

            var view = await faceSearch.GetAsync(access.OwnerUserId, access.MainAlbumId, searchId, cancellationToken);
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
            if (access is null || !access.Capabilities.FaceSearch)
            {
                return Results.NotFound();
            }

            var result = await faceSearch.ActivateForTvAsync(
                access.OwnerUserId, access.MainAlbumId, searchId, cancellationToken);

            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyFaceSearchActivateTv,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.MainAlbumId,
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
            if (access is null || !access.Capabilities.FaceSearch)
            {
                return Results.NotFound();
            }

            await faceSearch.DeleteAsync(access.OwnerUserId, access.MainAlbumId, searchId, cancellationToken);

            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyFaceSearchDelete,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: access.MainAlbumId,
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
        }).WithName("ListOwnerPartyChallenges").RequirePartyGames();

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
        }).WithName("CreatePartyChallenge").RequirePartyGames();

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
        }).WithName("UpdatePartyChallenge").RequirePartyGames();

        app.MapDelete("/api/albums/{albumId:guid}/party-challenges/{challengeId:guid}", async (
            Guid albumId, Guid challengeId, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyChallengeService challenges,
            CancellationToken cancellationToken) =>
        {
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            return await challenges.DeleteAsync(ownerId, albumId, challengeId, cancellationToken)
                ? Results.NoContent() : Results.NotFound();
        }).WithName("DeletePartyChallenge").RequirePartyGames();

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
        }).WithName("ReorderPartyChallenges").RequirePartyGames();

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
        }).WithName("GetAlbumPartySettings").RequirePermission(Permissions.PartyAccess);

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
            return await PartyAlbumSettingsOperations.SetPartyModeAsync(
                party, audit, ownerUserId, ownerUserId, id, body,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        }).WithName("SetAlbumPartyMode").RequirePermission(Permissions.PartyAccess);

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
            await PartyAlbumSettingsOperations.SetSlideshowSettingsAsync(
                party, httpContext.GetCurrentUserId()!.Value, id, body, cancellationToken))
            .WithName("SetPartySlideshowSettings").RequirePermission(Permissions.PartyAccess);

        // WHICH CONTRIBUTIONS THE PARTY TAKES — photographs, greetings for the
        // slideshow, the guest book — and the approval mode of each. Its own
        // route rather than a wider `party-settings` save, for the reason the
        // slideshow settings have one: none of these six switches may rotate a
        // token, publish a draft or move the party's lifecycle, and a route
        // that cannot do those things is easier to trust than a body that
        // promises not to.
        app.MapMethods("/api/albums/{id:guid}/party-contributions", ["PATCH"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromServices] IAuditLogger audit,
            [FromBody] NubArca.Api.Party.PartyContributionSettingsRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return await PartyAlbumSettingsOperations.SetContributionSettingsAsync(
                party, audit, ownerUserId, ownerUserId, id, body,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        }).WithName("SetPartyContributionSettings").RequirePartyContributions();

        app.MapMethods("/api/albums/{id:guid}/party-game-settings", ["PATCH"], async (
            Guid id, HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyLinkService party,
            [FromBody] NubArca.Api.Party.PartyGameSettingsRequest? body,
            CancellationToken cancellationToken) =>
            await PartyAlbumSettingsOperations.SetGameSettingsAsync(
                party, httpContext.GetCurrentUserId()!.Value, id, body, cancellationToken))
            .WithName("SetPartyGameSettings").RequirePartyGames();

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
        }).WithName("ListPartyUploads").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/hide", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Hidden, AuditActions.PartyUploadHide, cancellationToken))
            .WithName("HidePartyUpload").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/approve", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Approved, AuditActions.PartyUploadApprove, cancellationToken))
            .WithName("ApprovePartyUpload").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/reject", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Rejected, AuditActions.PartyUploadReject, cancellationToken))
            .WithName("RejectPartyUpload").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-uploads/{fileItemId:guid}/restore", async (
            Guid albumId, Guid fileItemId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await ModeratePartyUploadAsync(
                httpContext, moderation, audit, albumId, fileItemId,
                NubArca.Api.Domain.PartyUploadStatuses.Approved, AuditActions.PartyUploadRestore, cancellationToken))
            .WithName("RestorePartyUpload").RequirePermission(Permissions.PartyAccess);

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
            if (access is null || !access.Capabilities.Contributions)
            {
                return Results.NotFound();
            }

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            // Provenance for an abuse investigation, never for display. A guest who
            // has not uploaded yet gets their participant session minted here.
            var participantId = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);

            var result = await messages.SubmitAsync(
                access, body.DisplayName, body.Text, participantId, cancellationToken);

            // A QUOTA IS NOT A RATE LIMIT, and they get different codes because a
            // client has to tell them apart: 409 says the host allowed this guest
            // so many greetings and they have sent them, which no amount of
            // waiting changes; 429 says the requests are arriving too fast, which
            // waiting does change.
            if (result.Error is NubArca.Api.Party.PartyMessageSubmissionError.LimitReached)
            {
                return Results.Json(new
                {
                    error = "guest_message_limit_reached",
                    maxMessages = access.MaxMessagesPerParticipant,
                }, statusCode: StatusCodes.Status409Conflict);
            }

            // THE PARTY TAKES NO GREETINGS. A 409 like the quota above — the
            // request is well-formed and the party's configuration is what
            // refuses it — with its own stable code, so a client can tell "you
            // have sent your allowance" apart from "there is nowhere to write
            // one here". Nothing was stored and no allowance was spent.
            if (result.Error is NubArca.Api.Party.PartyMessageSubmissionError.Disabled)
            {
                return Results.Json(
                    new { error = "party_messages_disabled" },
                    statusCode: StatusCodes.Status409Conflict);
            }

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
                actor: null,
                action: AuditActions.PartyMessageSubmit,
                entityType: AuditEntityTypes.PartyMessage,
                entityId: message.Id,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { albumId = access.MainAlbumId, status = message.Status },
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
        }).WithName("ListPartyMessages").RequirePermission(Permissions.PartyAccess);

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
            .WithName("ApprovePartyMessage").RequirePermission(Permissions.PartyAccess);

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
            .WithName("RejectPartyMessage").RequirePermission(Permissions.PartyAccess);

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
            .WithName("HidePartyMessage").RequirePermission(Permissions.PartyAccess);

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
            .WithName("RestorePartyMessage").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/promote-hero", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await SetPartyMessageHeroAsync(
                httpContext, messages, audit, albumId, messageId, true,
                AuditActions.PartyMessageHeroPromote, cancellationToken))
            .WithName("PromotePartyMessageHero").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-messages/{messageId:guid}/demote-hero", async (
            Guid albumId, Guid messageId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Party.IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await SetPartyMessageHeroAsync(
                httpContext, messages, audit, albumId, messageId, false,
                AuditActions.PartyMessageHeroDemote, cancellationToken))
            .WithName("DemotePartyMessageHero").RequirePermission(Permissions.PartyAccess);

        return app;
    }

    // Party-safe DERIVED media bytes. Images: thumbnail=small, preview/download=
    // medium. Videos: thumbnail/preview=poster (no playback/download). Every served
    // image is metadata-stripped (EXIF/GPS/IPTC/XMP/ICC removed) before it leaves
    // the server.
    /// <summary>
    /// Party-safe derived bytes for an ALREADY-RESOLVED party. Public so the
    /// print capability can serve exactly the same media through its own token:
    /// same sizes, same metadata stripping, same refusal to serve an original.
    /// Duplicating this for a second token would be duplicating the privacy
    /// guarantees with it.
    /// </summary>
    internal static Task<IResult> ServeResolvedPartyMediaAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid fileId,
        string variant,
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMediaService partyMedia,
        IFileThumbnailService thumbnails,
        NubArca.Api.Metadata.IImageMetadataStripper stripper,
        CancellationToken cancellationToken)
        => ServeMediaCoreAsync(
            ownerUserId, albumId, fileId, variant, httpContext,
            partyMedia, thumbnails, stripper, cancellationToken);

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

        // THE LIFECYCLE, on the bytes themselves. Hiding the gallery in the
        // browser is not a rule; this is. During the party the photographs are
        // the party, afterwards they are the memories and last exactly as long
        // as the library does, and before it there is nothing to show.
        if (!access.Experience.AllowsAlbumMedia)
        {
            // ONE exception, and it is the invitation's hero: the cover the host
            // chose for the album. It is the same derived, metadata-stripped
            // preview every other party surface serves, and it is the only file
            // id that resolves before the party — an invitation with a
            // photograph on it is still not a gallery.
            var header = await partyMedia.GetAlbumAsync(
                access.OwnerUserId, access.MainAlbumId, cancellationToken);
            if (access.Experience.Phase != NubArca.Api.Domain.PartyGuestPhase.Before
                || header?.ChosenCoverFileItemId != fileId)
            {
                return Results.NotFound();
            }
        }

        return await ServeMediaCoreAsync(
            access.OwnerUserId, access.MainAlbumId, fileId, variant, httpContext,
            partyMedia, thumbnails, stripper, cancellationToken);
    }

    internal static async Task<IResult> ServeMediaCoreAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid fileId,
        string variant,
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMediaService partyMedia,
        IFileThumbnailService thumbnails,
        NubArca.Api.Metadata.IImageMetadataStripper stripper,
        CancellationToken cancellationToken)
    {
        // AUTHORIZATION, album-scoped: a displayable member of THIS album.
        var kind = await partyMedia.GetVisibleMediaKindAsync(
            ownerUserId, albumId, fileId, cancellationToken);
        if (kind is null)
        {
            return Results.NotFound();
        }

        return await ServeAuthorizedDerivativeAsync(
            ownerUserId, fileId, kind.Value, variant, httpContext,
            thumbnails, stripper, cancellationToken);
    }

    /// <summary>
    /// The BYTES, for a file some relation has ALREADY authorized — album
    /// membership, or a Party reference such as a menu's photograph or an
    /// activity's picture. Authorization and byte serving are separate on
    /// purpose: there are several ways to be allowed to see a file and exactly
    /// one way its bytes leave the server — a derived rendition from the
    /// ordinary thumbnail pipeline, metadata-stripped, never the original.
    /// </summary>
    internal static async Task<IResult> ServeAuthorizedDerivativeAsync(
        Guid ownerUserId,
        Guid fileId,
        NubArca.Api.Party.PartyMediaKind kind,
        string variant,
        HttpContext httpContext,
        IFileThumbnailService thumbnails,
        NubArca.Api.Metadata.IImageMetadataStripper stripper,
        CancellationToken cancellationToken)
    {
        // Videos are view-only posters; no download.
        if (kind == NubArca.Api.Party.PartyMediaKind.Video && variant == "download")
        {
            return Results.NotFound();
        }

        string size = kind == NubArca.Api.Party.PartyMediaKind.Video
            ? ThumbnailSizes.Poster
            : (variant == "thumbnail" ? ThumbnailSizes.Small : ThumbnailSizes.Medium);

        var content = await thumbnails.EnsureAsync(fileId, ownerUserId, size, cancellationToken);
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
    private static Task<IResult> ModeratePartyMessageAsync(
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid albumId,
        Guid messageId,
        NubArca.Api.Domain.PartyMessageModeration action,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var ownerUserId = httpContext.GetCurrentUserId()!.Value;
        return ModeratePartyMessageAsync(
            messages, audit, ownerUserId, ownerUserId, albumId, messageId, action, auditAction,
            httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
    }

    /// <summary>Moderating one greeting, shared with the Party Crew façade. See the upload twin above.</summary>
    internal static async Task<IResult> ModeratePartyMessageAsync(
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        Guid messageId,
        NubArca.Api.Domain.PartyMessageModeration action,
        string auditAction,
        string? ip,
        CancellationToken cancellationToken)
    {
        var result = await messages.ModerateAsync(
            albumId, ownerUserId, messageId, action, cancellationToken);
        return await CompletePartyMessageMutationAsync(
            audit, result, actor, messageId, auditAction,
            new { albumId, messageId, action = action.ToString() }, ip, cancellationToken);
    }

    private static Task<IResult> SetPartyMessageHeroAsync(
        HttpContext httpContext,
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid albumId,
        Guid messageId,
        bool hero,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var ownerUserId = httpContext.GetCurrentUserId()!.Value;
        return SetPartyMessageHeroAsync(
            messages, audit, ownerUserId, ownerUserId, albumId, messageId, hero, auditAction,
            httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
    }

    /// <summary>Choosing the greeting the screens lead with. Shared with the Party Crew façade.</summary>
    internal static async Task<IResult> SetPartyMessageHeroAsync(
        NubArca.Api.Party.IPartyMessageService messages,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        Guid messageId,
        bool hero,
        string auditAction,
        string? ip,
        CancellationToken cancellationToken)
    {
        var result = await messages.SetHeroAsync(
            albumId, ownerUserId, messageId, hero, cancellationToken);
        return await CompletePartyMessageMutationAsync(
            audit, result, actor, messageId, auditAction,
            new { albumId, messageId, hero }, ip, cancellationToken);
    }

    private static async Task<IResult> CompletePartyMessageMutationAsync(
        IAuditLogger audit,
        NubArca.Api.Party.PartyMessageMutation result,
        AuditActor actor,
        Guid messageId,
        string auditAction,
        object metadata,
        string? ip,
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
            actor, auditAction, AuditEntityTypes.PartyMessage, messageId, ip, metadata,
            cancellationToken);
        return Results.NoContent();
    }

    // Shared owner-moderation action: set a guest upload's status + audit it (album
    // + file id only, never token/hash/storage internals). 404 when foreign/missing.
    private static Task<IResult> ModeratePartyUploadAsync(
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
        return ModeratePartyUploadAsync(
            moderation, audit, ownerUserId, ownerUserId, albumId, fileItemId, status, auditAction,
            httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
    }

    /// <summary>
    /// Moderating one guest photograph, shared with the Party Crew façade.
    ///
    /// <para><c>ownerUserId</c> is whose album it is; <c>actor</c> is who
    /// decided. For a host they are the same. For a collaborator the album is
    /// still the host's, and the audit says who actually hid the photograph —
    /// which is the question somebody asks the morning after.</para>
    /// </summary>
    internal static async Task<IResult> ModeratePartyUploadAsync(
        NubArca.Api.Party.IPartyModerationService moderation,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        Guid fileItemId,
        string status,
        string auditAction,
        string? ip,
        CancellationToken cancellationToken)
    {
        var ok = await moderation.SetStatusAsync(
            ownerUserId, albumId, fileItemId, status, ownerUserId, cancellationToken);
        if (!ok)
        {
            return Results.NotFound();
        }
        await audit.LogAsync(actor, auditAction, AuditEntityTypes.PartyAlbum,
            albumId, ip, new { albumId, fileItemId }, cancellationToken);
        return Results.NoContent();
    }

    // Duplicated from Program.cs's local `SetNoStore` / `SetPrivateDerivativeCache`
    // helpers (used by dozens of other still-inline endpoints there, so they stay
    // put) — same logic.

    // Domain 0 means unlimited; the public DTO says null so a client cannot read
    // "no limit" as "no slots left".
    private static int? Unlimited(int max) => max > 0 ? max : null;

    private static int? Remaining(int max, int used) => max > 0 ? Math.Max(0, max - used) : null;

    /// <summary>
    /// The picture a guest page opens on, as an address on this token: the
    /// party's own cover choice (<c>PartyCoverPolicy</c>), else the album's
    /// cover the way this phase may show it — before the party only one the host
    /// CHOSE, because the gallery is closed and whichever photo sorts first is
    /// not a decision — else nothing, and the page draws its composition. One
    /// rule for the page and for its link preview.
    /// </summary>
    private static async Task<string?> GuestCoverUrlAsync(
        AppDbContext db,
        NubArca.Api.Party.PartyAccess access,
        NubArca.Api.Party.PartyAlbumHeader header,
        Guid? invitationCover,
        Guid? liveCover,
        int partyVersion,
        string enc,
        CancellationToken cancellationToken)
    {
        var chosen = await NubArca.Api.Party.PartyCoverPolicy.ResolveAsync(
            db, access, invitationCover, liveCover, cancellationToken);
        var albumCover = access.Experience.AllowsAlbumMedia
            ? header.CoverFileItemId
            : header.ChosenCoverFileItemId;
        return chosen is { } pick
            // The party's version is the cache key: choosing a cover spends one,
            // so a phone never keeps yesterday's picture.
            ? $"/api/party/{enc}/cover/{pick.Which}/media?v={partyVersion}"
            : albumCover is Guid cover
                ? $"/api/party/{enc}/media/{cover}/preview"
                : null;
    }

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
    int? MaxVideoUploadsPerParticipant = null,
    // Greetings per guest, on the same 0 = unlimited scale as the media quotas.
    int? MaxMessagesPerParticipant = null,
    int? MaxGuestbookEntriesPerParticipant = null);

using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// THE GUEST BOOK, public and owner-facing, in one file.
///
/// <para>The public half rides EITHER guest token — the party's VIEW token on
/// the QR, or the contribution page's upload token. Reading the book is part of
/// looking at the party, and a host may keep a book while accepting no
/// photographs at all: the three contributions are independent, so the book
/// must not hang off the upload capability's switch. No new token model was
/// introduced; this is the same capability, the same seam, the same guest
/// session cookie and the same rate-limit family every other public Party
/// surface uses.</para>
///
/// <para>A memory's photograph is served on the memory's OWN route
/// (<c>…/guestbook/{entryId}/photo</c>), from the memory's own blob — never
/// through the album file it was chosen from, which may be long gone. The
/// photographs a guest may choose from are served on the chooser's route, by
/// the same rule the publish re-asks.</para>
///
/// <para>The owner half is PARTY-scoped rather than album-scoped, because the
/// book is. A party may be re-minted, re-linked, or hold no album at all, and
/// its book survives every one of those.</para>
/// </summary>
public static class PartyGuestbookEndpoints
{
    /// <summary>
    /// What a guest sends. No party id, no participant id, no owner, no album
    /// and no template version: the only authority in the request is the token
    /// in the route, and everything else is resolved on the server.
    /// </summary>
    public sealed record SubmitGuestbookEntryRequest(
        Guid? SourceMediaItemId,
        string? AuthorDisplayName,
        string? Body,
        string? TemplateKey,
        GuestbookCropRequest? Crop);

    /// <summary>The framing: centre as fractions of the photograph, zoom from 1.</summary>
    public sealed record GuestbookCropRequest(double? CenterX, double? CenterY, double? Zoom);

    public static IEndpointRouteBuilder MapPartyGuestbookEndpoints(this IEndpointRouteBuilder app)
    {
        // ── PUBLIC (anonymous, view-token scoped) ───────────────────────────

        app.MapGet("/api/party/{token}/guestbook", async (
            string token,
            HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] IPartyParticipantService participants,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            // EITHER TOKEN READS, for the same reason either token writes. A
            // guest reaches the book from the contribution page holding the
            // UPLOAD token, and that page has to READ the book before it can
            // offer the composer — so a read that accepted only the view token
            // answered 404 to the very guests it had just invited to write, and
            // the page reported a party with no book. Neither token is widened:
            // each still resolves only its own hash, and `Readable` still asks
            // the book's own switch.
            var access = await party.ResolvePublicAsync(token, cancellationToken)
                ?? await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();

            // RESOLVE, never create. Reading the book is not arriving at the
            // party, so opening the page must not mint a guest — only writing
            // in it does. A reader nobody has seen before simply has no count
            // yet, which is the honest answer.
            var reader = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);

            // A party that keeps no book has no book to read. Generic
            // not-found, exactly like every other absent Party capability: a
            // guest has no business learning that the surface exists elsewhere.
            var page = await guestbook.GetPublicPageAsync(access, token, reader, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).WithName("GetPartyGuestbook").RequireRateLimiting(PartyEndpoints.PublicRateLimitPolicy);

        // The photographs a guest may make a memory from: the main album's,
        // and only photographs. Either token, because either token writes.
        app.MapGet("/api/party/{token}/guestbook/photos", async (
            string token,
            HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGuestbookService guestbook,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken)
                ?? await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();

            var photos = await guestbook.ListPhotosAsync(access, token, cancellationToken);
            return photos is null ? Results.NotFound() : Results.Ok(photos);
        }).WithName("ListPartyGuestbookPhotos").RequireRateLimiting(PartyEndpoints.PublicRateLimitPolicy);

        // One of those photographs, as the chooser draws it. The SAME derived,
        // metadata-stripped serving path every party surface uses, authorized by
        // the chooser's own rule — so the upload token sees exactly what the
        // book lets it choose, and not one file more.
        app.MapGet("/api/party/{token}/guestbook/photos/{fileId:guid}/{variant}", async (
            string token,
            Guid fileId,
            string variant,
            HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] NubArca.Api.Files.IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            // The two sizes the composer needs. Never a download.
            if (variant is not ("thumbnail" or "preview")) return Results.NotFound();

            var access = await party.ResolvePublicAsync(token, cancellationToken)
                ?? await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            if (!await guestbook.IsChoosableAsync(access, fileId, cancellationToken)) return Results.NotFound();

            return await PartyEndpoints.ServeAuthorizedDerivativeAsync(
                access.OwnerUserId, fileId, PartyMediaKind.Image, variant,
                httpContext, thumbnails, stripper, cancellationToken);
        }).WithName("GetPartyGuestbookPhoto").RequireRateLimiting(PartyEndpoints.PublicMediaRateLimitPolicy);

        // A memory's picture. From the memory's own blob, so it is there for as
        // long as the memory is — whatever has happened to the album since.
        app.MapGet("/api/party/{token}/guestbook/{entryId:guid}/photo", async (
            string token,
            Guid entryId,
            HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] IPartyParticipantService participants,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await party.ResolvePublicAsync(token, cancellationToken)
                ?? await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();

            // Who is looking — resolved, never minted — because while the book
            // is closed to the room a guest sees only the pictures of what they
            // wrote.
            var reader = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            var photo = await guestbook.OpenPublicPhotoAsync(access, entryId, reader, cancellationToken);
            return photo is null
                ? Results.NotFound()
                : await PartyEndpoints.ServeStrippedDerivativeAsync(
                    photo, "image/jpeg", httpContext, stripper, cancellationToken);
        }).WithName("GetPartyGuestbookEntryPhoto").RequireRateLimiting(PartyEndpoints.PublicMediaRateLimitPolicy);

        app.MapPost("/api/party/{token}/guestbook", async (
            string token,
            HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] IPartyParticipantService participants,
            [FromServices] IAuditLogger audit,
            [FromBody] SubmitGuestbookEntryRequest? body,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            // EITHER TOKEN WRITES, exactly as either token reads. The view
            // token is the one printed on the QR and the one a keepsake
            // outlives the party on; the upload token is the one a guest
            // reaches the contribution page with. Both doors open the same
            // book. Neither token is widened: each still resolves only its own
            // hash, and the book's own switch still decides.
            var access = await party.ResolvePublicAsync(token, cancellationToken)
                ?? await party.ResolveUploadAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            if (body is null) return Results.BadRequest(new { error = "Missing request body." });

            // Provenance for an abuse investigation, never for display. Writing
            // in the book is a guest ARRIVING, so it may mint the session — the
            // same rule contributing a photograph follows.
            var participantId = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);

            var result = await guestbook.SubmitAsync(
                access,
                token,
                new PartyGuestbookSubmission(
                    body.SourceMediaItemId,
                    body.AuthorDisplayName,
                    body.Body,
                    body.TemplateKey,
                    body.Crop?.CenterX,
                    body.Crop?.CenterY,
                    body.Crop?.Zoom),
                participantId,
                cancellationToken);

            if (result.Error is PartyGuestbookSubmissionError.Disabled)
            {
                // The same shape of refusal a greeting gets at a party that
                // takes none: well-formed, and refused by the party's
                // configuration rather than by the shape of what was sent.
                return Results.Json(
                    new { error = "guestbook_disabled" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (result.Error is PartyGuestbookSubmissionError.LimitReached)
            {
                // The party has a budget and this guest has spent it. A 409
                // like the greetings' quota, with its own stable code, so a
                // client can tell "you have written your allowance" apart from
                // "there is nowhere to write one here" and from "you are going
                // too fast". Nothing was stored and no slot was spent.
                return Results.Json(
                    new
                    {
                        error = "guestbook_limit_reached",
                        maxEntries = access.MaxGuestbookEntriesPerParticipant,
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (result.Error is PartyGuestbookSubmissionError.PhotoUnavailable)
            {
                // THE RACE, answered: the photograph was there when the guest
                // chose it and is not one they may choose now — removed from the
                // album, trashed, or never this party's. A 409 with its own
                // code, so the composer keeps the words and sends the guest back
                // to the photographs. Nothing was stored and no slot was spent.
                return Results.Json(
                    new { error = "guestbook_photo_unavailable" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (result.Error is PartyGuestbookSubmissionError error)
            {
                // Safe, machine-readable codes with the limits beside them. The
                // client enforces the same limits from the same contract, so
                // this is the backstop rather than the copy a guest usually
                // reads.
                return Results.BadRequest(new
                {
                    error = error switch
                    {
                        PartyGuestbookSubmissionError.InvalidAuthorDisplayName => "guestbook_invalid_author",
                        PartyGuestbookSubmissionError.PhotoRequired => "guestbook_photo_required",
                        PartyGuestbookSubmissionError.PhotoNotImage => "guestbook_photo_not_image",
                        PartyGuestbookSubmissionError.InvalidTemplate => "guestbook_invalid_template",
                        PartyGuestbookSubmissionError.InvalidCrop => "guestbook_invalid_crop",
                        _ => "guestbook_invalid_body",
                    },
                    maxAuthorDisplayNameLength = PartyGuestbookLimits.MaxAuthorDisplayNameLength,
                    maxBodyLength = PartyGuestbookLimits.MaxBodyLength,
                });
            }

            var entry = result.Entry!;
            // The line records that somebody wrote in the book, with which
            // design, and what it became. The BODY, the signature and the
            // photograph's blob are never logged anywhere, exactly as a
            // greeting's text is not.
            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyGuestbookSubmit,
                entityType: AuditEntityTypes.PartyGuestbookEntry,
                entityId: entry.Id,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { status = entry.Status, template = entry.Entry?.Template.Key },
                cancellationToken: cancellationToken);

            return Results.Ok(entry);
        }).WithName("SubmitPartyGuestbookEntry")
            .RequireRateLimiting(PartyEndpoints.MessageRateLimitPolicy);

        // ── OWNER / DELEGATE ────────────────────────────────────────────────
        //
        // Authorised by the SAME authority that moderates the party's
        // greetings — owner, or a delegate holding the party-message capability
        // on the party's main album — resolved inside the service. Contributions
        // are one job, and a party where somebody may take a greeting down but
        // not a dedication would be a distinction nobody asked for.

        app.MapGet("/api/parties/{partyId:guid}/guestbook", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyGuestbookService guestbook,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var list = await guestbook.ListForManagerAsync(
                partyId, actorUserId,
                entryId => PartyGuestbookService.ManagerPhotoUrl(partyId, entryId),
                cancellationToken);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("ListPartyGuestbook").RequirePermission(Permissions.PartyAccess);

        // Any memory's picture, pending ones included: whoever decides whether a
        // memory goes in the book has to see what it shows.
        app.MapGet("/api/parties/{partyId:guid}/guestbook/{entryId:guid}/photo", async (
            Guid partyId,
            Guid entryId,
            HttpContext httpContext,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var photo = await guestbook.OpenManagedPhotoAsync(partyId, actorUserId, entryId, cancellationToken);
            return photo is null
                ? Results.NotFound()
                : await PartyEndpoints.ServeStrippedDerivativeAsync(
                    photo, "image/jpeg", httpContext, stripper, cancellationToken);
        }).WithName("GetPartyGuestbookManagedPhoto").RequirePermission(Permissions.PartyAccess);

        // ── THE GUEST BOOK, LIVE (owner) ─────────────────────────────────────
        //
        // Album-scoped, beside the game's own control room: the regia runs the
        // evening from one place. Letting the room read the book and putting
        // it on the television are two decisions with one version; a refusal
        // is a 409 carrying the current state, exactly like a game command.

        app.MapGet("/api/albums/{albumId:guid}/party-guestbook-live", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IPartyGuestbookLiveService live,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var control = await live.GetAsync(
                httpContext.GetCurrentUserId()!.Value, albumId, PartyGuestbookLiveRights.Owner, cancellationToken);
            return control is null ? Results.NotFound() : Results.Ok(control);
        }).WithName("GetPartyGuestbookLive").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/albums/{albumId:guid}/party-guestbook-live/commands", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IPartyGuestbookLiveService live,
            [FromBody] PartyGuestbookLiveCommandRequest? body,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            return await ExecuteLiveAsync(
                live, httpContext.GetCurrentUserId()!.Value, albumId, PartyGuestbookLiveRights.Owner,
                body, cancellationToken);
        }).WithName("ExecutePartyGuestbookLiveCommand").RequirePermission(Permissions.PartyAccess);

        MapModeration(app, "approve", PartyMessageModeration.Approve, AuditActions.PartyGuestbookApprove);
        MapModeration(app, "reject", PartyMessageModeration.Reject, AuditActions.PartyGuestbookReject);
        MapModeration(app, "hide", PartyMessageModeration.Hide, AuditActions.PartyGuestbookHide);
        MapModeration(app, "restore", PartyMessageModeration.Restore, AuditActions.PartyGuestbookRestore);

        return app;
    }

    /// <summary>
    /// One guest-book live command, shared by the host's route and the Party
    /// Crew façade — so the two cannot drift. The rights say what THIS caller
    /// may do; the service re-checks every one on arrival.
    /// </summary>
    internal static async Task<IResult> ExecuteLiveAsync(
        IPartyGuestbookLiveService live,
        Guid ownerUserId,
        Guid albumId,
        PartyGuestbookLiveRights rights,
        PartyGuestbookLiveCommandRequest? body,
        CancellationToken cancellationToken)
    {
        if (body?.Command is null || body.ExpectedVersion is null) return Results.BadRequest();

        var result = await live.ExecuteAsync(
            ownerUserId, albumId, body.Command, body.ExpectedVersion.Value, rights, cancellationToken);
        return result.Error switch
        {
            null => Results.Ok(result.Control),
            PartyGuestbookLiveError.NotFound => Results.NotFound(),
            PartyGuestbookLiveError.UnknownCommand => Results.BadRequest(new { error = "unknown_command" }),
            PartyGuestbookLiveError.Forbidden => Results.Json(
                new PartyGuestbookLiveRefusalDto("forbidden", result.Control),
                statusCode: StatusCodes.Status403Forbidden),
            PartyGuestbookLiveError error => Results.Json(
                new PartyGuestbookLiveRefusalDto(PartyGuestbookLiveService.Code(error), result.Control),
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>
    /// One moderation decision, shared by the host's route and the Party Crew
    /// façade — so the two cannot drift, and the audit line differs only in who
    /// it names.
    /// </summary>
    internal static async Task<IResult> ModerateAsync(
        IPartyGuestbookService guestbook,
        IAuditLogger audit,
        Guid partyId,
        Guid ownerUserId,
        AuditActor actor,
        Guid entryId,
        PartyMessageModeration action,
        string auditAction,
        string? ip,
        CancellationToken cancellationToken)
    {
        var result = await guestbook.ModerateAsync(
            partyId, ownerUserId, entryId, action, cancellationToken);
        if (result == PartyMessageMutation.NotFound) return Results.NotFound();
        if (result == PartyMessageMutation.InvalidTransition)
        {
            return Results.BadRequest(new { error = "invalid_transition" });
        }

        await audit.LogAsync(
            actor, auditAction, AuditEntityTypes.PartyGuestbookEntry, entryId, ip,
            new { partyId }, cancellationToken);
        return Results.NoContent();
    }

    private static void MapModeration(
        IEndpointRouteBuilder app, string segment, PartyMessageModeration action, string auditAction)
    {
        app.MapPost($"/api/parties/{{partyId:guid}}/guestbook/{{entryId:guid}}/{segment}", async (
            Guid partyId,
            Guid entryId,
            HttpContext httpContext,
            [FromServices] IPartyGuestbookService guestbook,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            return await ModerateAsync(
                guestbook, audit, partyId, actorUserId, AuditActor.User(actorUserId), entryId,
                action, auditAction,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        }).WithName($"PartyGuestbook{segment}").RequirePermission(Permissions.PartyAccess);
    }

    // Dedications awaiting a decision, and a book a manager is reading. Never
    // cached.
    private static void NoStore(HttpContext http) =>
        http.Response.Headers.CacheControl = "no-store";
}

using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Party aggregate root's owner surface: the host's parties, one party's
/// own data, where it draws its media from, and its lifecycle.
///
/// <para>Deliberately small. Everything a party is CONFIGURED with — guest
/// contributions, moderation, slideshow timing, games, printing, face search —
/// keeps living on its existing album-scoped routes, reached through the
/// party's main album. This module owns the root and nothing else, which is
/// what keeps a second copy of the Party application from growing here.</para>
///
/// <para>Party status is not an access gate. Nothing in this file opens or
/// closes a guest capability; that stays the link's <c>Enabled</c>/
/// <c>RevokedAt</c> and the owner's <c>party.access</c> permission, so there is
/// one answer to "why is this party closed" rather than two.</para>
/// </summary>
public static class PartyOwnerEndpoints
{
    /// <summary>A write that states the version it read, like every album mutation.</summary>
    public sealed record PartyVersionedRequest(int Version);

    public sealed record CreatePartyRequest(
        string? Title, string? Description, DateTime? EventStartsAt);

    public sealed record UpdatePartyRequest(
        string? Title,
        string? Description,
        DateTime? EventStartsAt,
        DateTime? GuestAccessExpiresAt,
        DateTime? LibraryAccessExpiresAt,
        int Version);

    public sealed record SetPartyMainMediaSourceRequest(Guid AlbumId, int Version);

    /// <summary>
    /// One typed slot as the owner writes it. <c>Content</c> is raw JSON on the
    /// wire and validated SERVER-SIDE against the shape its kind declares, so
    /// knowing the route is not permission to store arbitrary documents.
    /// </summary>
    public sealed record PartyGuestContentRequest(
        bool Enabled,
        bool VisibleBefore,
        bool VisibleLive,
        bool VisibleAfter,
        System.Text.Json.JsonElement? Content,
        int Version);

    public static IEndpointRouteBuilder MapPartyOwnerEndpoints(this IEndpointRouteBuilder app)
    {
        // The host's own parties. Owner-scoped in the query itself, so there is
        // no list to filter afterwards and no cursor a stranger could replay.
        app.MapGet("/api/parties", async (
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await parties.ListAsync(ownerUserId, cancellationToken));
        }).WithName("ListParties").RequirePermission(Permissions.PartyAccess);

        // A party begins as a NAME and a date, and nothing else — no album, no
        // capability, no token, no television, no game, no print configuration.
        // The event exists before the photographs.
        app.MapPost("/api/parties", async (
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] CreatePartyRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.CreateAsync(
                ownerUserId,
                new PartyMetadataRequest(
                    body.Title, body.Description, body.EventStartsAt, null, null),
                cancellationToken);
            if (result.Outcome != PartyMutationOutcome.Ok)
            {
                return ToResult(result);
            }

            var created = result.Party!;
            await audit.LogAsync(
                ownerUserId, AuditActions.PartyCreate, AuditEntityTypes.Party, created.Id,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                new { status = created.Status }, cancellationToken);
            return Results.Created($"/api/parties/{created.Id}", created);
        }).WithName("CreateParty").RequirePermission(Permissions.PartyAccess);

        app.MapGet("/api/parties/{partyId:guid}", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var party = await parties.GetAsync(ownerUserId, partyId, cancellationToken);
            return party is null ? Results.NotFound() : Results.Ok(party);
        }).WithName("GetParty").RequirePermission(Permissions.PartyAccess);

        // The party's own DATA. One route rather than one per field, because
        // they are edited on one form and share one version — and deliberately
        // no way to write Status, LiveStartedAt or LiveEndedAt, which belong to
        // the transitions below.
        app.MapMethods("/api/parties/{partyId:guid}", ["PATCH"], async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] UpdatePartyRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.UpdateMetadataAsync(
                ownerUserId,
                partyId,
                new PartyMetadataRequest(
                    body.Title, body.Description, body.EventStartsAt,
                    body.GuestAccessExpiresAt, body.LibraryAccessExpiresAt),
                body.Version,
                cancellationToken);
            if (result.Outcome == PartyMutationOutcome.Ok)
            {
                // The metadata itself is not audited field by field — a title is
                // not a security event — but closing guest access IS, because it
                // is the host deciding when the guests stop.
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyUpdate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new
                    {
                        guestAccessExpiresAt = result.Party!.GuestAccessExpiresAt,
                        libraryAccessExpiresAt = result.Party!.LibraryAccessExpiresAt,
                    },
                    cancellationToken);
            }
            return ToResult(result);
        }).WithName("UpdateParty").RequirePermission(Permissions.PartyAccess);

        // Where the party draws its media from. PUT because it states the whole
        // fact — this party's main album is that one — rather than a change to
        // apply on top of whatever was there.
        app.MapPut("/api/parties/{partyId:guid}/media/main", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] SetPartyMainMediaSourceRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.SetMainMediaSourceAsync(
                ownerUserId, partyId, body.AlbumId, body.Version, cancellationToken);
            if (result.Outcome == PartyMutationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyMediaSourceSet, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { albumId = body.AlbumId }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("SetPartyMainMediaSource").RequirePermission(Permissions.PartyAccess);

        // Tearing a party down keeps its ALBUM. The photographs the guests were
        // allowed to see stay exactly where they are; the ones the host never
        // let through go to Trash the ordinary way, and the party's own rows —
        // links, participants, counters, games, sessions, content — go with it.
        app.MapDelete("/api/parties/{partyId:guid}", async (
            Guid partyId,
            [FromQuery] int version,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.TeardownAsync(
                ownerUserId, partyId, version, cancellationToken);
            if (result.Outcome != PartyMutationOutcome.Ok)
            {
                return ToResult(result);
            }

            // The party's rows are gone; what happened to it is not. This line
            // is the only remaining record that the evening existed, which is
            // exactly where that belongs.
            await audit.LogAsync(
                ownerUserId, AuditActions.PartyTeardown, AuditEntityTypes.Party, partyId,
                httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.NoContent();
        }).WithName("TearDownParty").RequirePermission(Permissions.PartyAccess);

        // What the party TELLS its guests. Two routes for six typed slots, not
        // one endpoint per kind: they are the same shape of decision and they
        // are edited on one screen.
        app.MapGet("/api/parties/{partyId:guid}/guest-content", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyGuestContentService content,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var slots = await content.ListAsync(ownerUserId, partyId, cancellationToken);
            return slots is null ? Results.NotFound() : Results.Ok(slots);
        }).WithName("ListPartyGuestContent").RequirePermission(Permissions.PartyAccess);

        // PUT because it states the whole slot — what it says, whether it is on,
        // and which surfaces it belongs to. The version is the SLOT's own, so
        // editing the menu never contends with renaming the party.
        app.MapPut("/api/parties/{partyId:guid}/guest-content/{kind}", async (
            Guid partyId,
            string kind,
            HttpContext httpContext,
            [FromServices] IPartyGuestContentService content,
            [FromBody] PartyGuestContentRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await content.UpsertAsync(
                ownerUserId, partyId, kind,
                new PartyGuestContentWrite(
                    body.Enabled, body.VisibleBefore, body.VisibleLive, body.VisibleAfter,
                    body.Content, body.Version),
                cancellationToken);

            return result.Outcome switch
            {
                PartyGuestContentOutcome.Ok => Results.Ok(result.Content),
                // A kind the product does not define is a not-found rather than
                // a validation error: there is no such slot to talk about.
                PartyGuestContentOutcome.UnknownKind => Results.NotFound(),
                PartyGuestContentOutcome.InvalidPayload =>
                    Results.BadRequest(new { error = "invalid_content" }),
                PartyGuestContentOutcome.VersionConflict => Results.Json(
                    new { error = "version_conflict", content = result.Content },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        }).WithName("SetPartyGuestContent").RequirePermission(Permissions.PartyAccess);

        // One route per ACTION rather than a status the caller chooses:
        // `publish` and `start-live` are different decisions that happen to
        // leave the party in adjacent states, and a PATCH taking a status would
        // let a client invent a fifth one.
        MapTransition(app, "publish", PartyLifecycleAction.Publish, AuditActions.PartyPublish);
        MapTransition(app, "start-live", PartyLifecycleAction.StartLive, AuditActions.PartyStartLive);
        MapTransition(app, "end-live", PartyLifecycleAction.EndLive, AuditActions.PartyEndLive);

        return app;
    }

    private static void MapTransition(
        IEndpointRouteBuilder app, string segment, PartyLifecycleAction action, string auditAction)
    {
        app.MapPost($"/api/parties/{{partyId:guid}}/{segment}", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyVersionedRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.TransitionAsync(
                ownerUserId, partyId, action, body.Version, cancellationToken);
            if (result.Outcome == PartyMutationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, auditAction, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { status = result.Party!.Status }, cancellationToken);
            }
            return ToResult(result);
        }).WithName($"Party{action}").RequirePermission(Permissions.PartyAccess);
    }

    /// <summary>
    /// The ONE place an owner mutation becomes an HTTP answer.
    ///
    /// <para>Every refusal that has a current state carries it, so a client
    /// refreshes and says what actually happened instead of guessing — and every
    /// refusal that would confirm the existence of something the caller may not
    /// see is the generic 404 instead.</para>
    /// </summary>
    private static IResult ToResult(PartyMutationResult result) => result.Outcome switch
    {
        PartyMutationOutcome.Ok => Results.Ok(result.Party),

        PartyMutationOutcome.InvalidRequest =>
            Results.BadRequest(new { error = "invalid_party" }),

        PartyMutationOutcome.InvalidTransition =>
            Results.BadRequest(new { error = "invalid_transition", party = result.Party }),

        PartyMutationOutcome.VersionConflict => Results.Json(
            new { error = "version_conflict", party = result.Party },
            statusCode: StatusCodes.Status409Conflict),

        // The album is fixed because guests, greetings, prints and games may
        // already be scoped to the capability that named it. A conflict rather
        // than a refusal: it describes a state, and the state is legible.
        PartyMutationOutcome.MediaSourceLocked => Results.Json(
            new { error = "media_source_locked", party = result.Party },
            statusCode: StatusCodes.Status409Conflict),

        PartyMutationOutcome.AlbumAlreadyInUse => Results.Json(
            new { error = "album_already_in_use", party = result.Party },
            statusCode: StatusCodes.Status409Conflict),

        _ => Results.NotFound(),
    };
}

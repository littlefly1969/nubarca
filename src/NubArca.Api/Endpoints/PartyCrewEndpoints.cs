using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Albums;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Print;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// What a Party Crew device may do — the whole surface, in one file.
///
/// <para><b>Every route here is the SAME operation the host's route performs.</b>
/// Not a copy of it: the services are owner-scoped and already correct, and the
/// handler bodies that carried validation or audit logic were extracted so both
/// surfaces call one implementation. A collaborator toggling the party on and a
/// host toggling the party on run the same code with the same ranges and write
/// the same audit lines — differing only in who the line names.</para>
///
/// <para><b>The client names the party, and nothing else.</b> No owner id, no
/// album id, no collaborator id and no capability is ever accepted from a
/// request: every one of those comes from resolving the device cookie. The
/// PARTY is named, because it has to be — one browser may legitimately hold
/// several assignments, and picking whichever grant came back first would make
/// "which evening am I running" a property of the query plan.</para>
///
/// <para>That id is a RESOURCE SELECTOR and never an authority. The resolver
/// requires it to match a live grant this device actually holds, in the same
/// clause that finds the grant, so naming another party is the same generic
/// nothing as holding no device at all — never a fallback to the one party this
/// device does have.</para>
///
/// <para><b>Deny by default, per capability, on every request.</b> Each route
/// declares the one capability it needs. The resolver re-reads the grants and
/// intersects them with what the OWNER is still permitted to do, so a role
/// change, a revoke, or a permission taken off the host's role takes effect on
/// the next request rather than at a token's expiry. A capability the request
/// does not hold answers 404, never 403 — a collaborator has no business
/// learning that a surface exists for somebody else.</para>
///
/// <para><b>What is deliberately absent.</b> Creating a party, choosing its
/// main album, duplicating it, tearing it down, and managing collaborators.
/// Those are the owner's for the life of the party: authority that can extend
/// itself is not bounded by anything, and a collaborator who could re-point the
/// album could hand the host's library to a party. The print STATION and the TV
/// DEVICE are likewise absent — they are installation hardware, not this
/// evening's, and enumerating them is the host administering their own
/// equipment rather than anyone running a party.</para>
/// </summary>
public static class PartyCrewEndpoints
{
    public static IEndpointRouteBuilder MapPartyCrewEndpoints(this IEndpointRouteBuilder app)
    {
        // ── This browser, and this browser at this party ────────────────────

        // EVERY PARTY THIS BROWSER MAY OPERATE. The one route with no party in
        // it, because it is what a person needs BEFORE they have chosen one:
        // the same phone can legitimately hold two assignments, and something
        // has to be able to say so. Derived from this device's own grants —
        // never from the owner's list of parties.
        app.MapGet("/api/party-crew/me", async (
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            CancellationToken ct) =>
        {
            NoStore(http);
            var assignments = await resolver.AssignmentsAsync(PartyCrewSession.Device(http), ct);
            return assignments.Count == 0
                ? Results.NotFound()
                : Results.Ok(new PartyCrewMeDto(assignments));
        }).WithName("GetPartyCrewAssignments");

        // DISCONNECT THIS BROWSER FROM PARTY CREW ENTIRELY. The device, not one
        // grant: every party it helps at, at once. Its own route because it is
        // a different decision from leaving one party, and a product that spells
        // them the same is a product that loses somebody two jobs when they
        // meant to leave one.
        app.MapDelete("/api/party-crew/device", async (
            HttpContext http,
            [FromServices] IPartyCrewSignOutService signOut,
            CancellationToken ct) =>
        {
            NoStore(http);
            await signOut.DisconnectDeviceAsync(PartyCrewSession.Device(http), Ip(http), ct);
            // The cookie goes regardless: a device whose grants were already
            // revoked is still holding a credential it should not keep.
            PartyCrewSession.ClearDevice(http);
            return Results.NoContent();
        }).WithName("DisconnectPartyCrewDevice");

        // ── At one party ────────────────────────────────────────────────────

        // What this device is AT THIS PARTY, and what the shell draws from it.
        // No capability: being told who you are is not an operation.
        app.MapGet("/api/party-crew/parties/{partyId:guid}/session", async (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            CancellationToken ct) =>
        {
            NoStore(http);
            var ctx = await Resolve(http, partyId, resolver, ct);
            return ctx is null
                ? Results.NotFound()
                : Results.Ok(new PartyCrewSessionDto(
                    ctx.PartyId, ctx.PartyTitle, ctx.CollaboratorName, ctx.RoleKey,
                    [.. ctx.Capabilities.Order(StringComparer.Ordinal)]));
        }).WithName("GetPartyCrewSession");

        // LEAVE THIS PARTY. Revokes the GRANT and nothing else: the same phone
        // may be helping at another party, and the cookie stays because that
        // other assignment still needs it.
        app.MapDelete("/api/party-crew/parties/{partyId:guid}/session", async (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyCrewSignOutService signOut,
            CancellationToken ct) =>
        {
            NoStore(http);
            var ctx = await Resolve(http, partyId, resolver, ct);
            if (ctx is null) return Results.NotFound();
            await signOut.SignOutAsync(ctx, Ip(http), ct);

            // The credential only goes when nothing else is using it.
            if (!await signOut.HasOtherAssignmentsAsync(ctx, ct)) PartyCrewSession.ClearDevice(http);
            return Results.NoContent();
        }).WithName("LeavePartyCrewParty");

        // This collaborator's own devices AT THIS PARTY, and dropping one.
        // Capability-free for the same reason the session is: recognising and
        // removing your own devices is not an operation on the party.
        app.MapGet("/api/party-crew/parties/{partyId:guid}/devices", async (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyCrewSignOutService signOut,
            CancellationToken ct) =>
        {
            NoStore(http);
            var ctx = await Resolve(http, partyId, resolver, ct);
            return ctx is null ? Results.NotFound() : Results.Ok(await signOut.DevicesAsync(ctx, ct));
        }).WithName("ListPartyCrewDevices");

        app.MapDelete("/api/party-crew/parties/{partyId:guid}/devices/{grantId:guid}", async (
            Guid partyId,
            Guid grantId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyCrewSignOutService signOut,
            CancellationToken ct) =>
        {
            NoStore(http);
            var ctx = await Resolve(http, partyId, resolver, ct);
            if (ctx is null) return Results.NotFound();
            if (!await signOut.RevokeAsync(ctx, grantId, Ip(http), ct)) return Results.NotFound();

            // Dropping the grant you are holding leaves this party — and the
            // cookie survives if this browser still helps at another one.
            if (grantId == ctx.DeviceGrantId && !await signOut.HasOtherAssignmentsAsync(ctx, ct))
            {
                PartyCrewSession.ClearDevice(http);
            }
            return Results.NoContent();
        }).WithName("RevokePartyCrewDevice");

        // ── The party itself ────────────────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/party", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyService parties,
            CancellationToken ct) =>
            With(http, partyId, resolver, null, ct, async ctx =>
            {
                var party = await parties.GetAsync(ctx.OwnerUserId, ctx.PartyId, ct);
                return party is null ? Results.NotFound() : Results.Ok(party);
            })).WithName("GetPartyCrewParty");

        app.MapMethods("/api/party-crew/parties/{partyId:guid}/party", ["PATCH"], (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyOwnerEndpoints.UpdatePartyRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.DetailsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "Missing request body." });
                var result = await parties.UpdateMetadataAsync(
                    ctx.OwnerUserId, ctx.PartyId,
                    new PartyMetadataRequest(
                        body.Title, body.Description, body.EventStartsAt,
                        body.GuestAccessExpiresAt, body.LibraryAccessExpiresAt),
                    body.Version, ct);
                if (result.Outcome == PartyMutationOutcome.Ok)
                {
                    await audit.LogAsync(
                        Actor(ctx), AuditActions.PartyUpdate, AuditEntityTypes.Party, ctx.PartyId, Ip(http),
                        new
                        {
                            guestAccessExpiresAt = result.Party!.GuestAccessExpiresAt,
                            libraryAccessExpiresAt = result.Party!.LibraryAccessExpiresAt,
                        }, ct);
                }
                return PartyOwnerEndpoints.ToResult(result);
            })).WithName("UpdatePartyCrewParty");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/party/covers", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyService parties,
            [FromBody] PartyOwnerEndpoints.SetPartyCoversRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.DetailsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "Missing request body." });
                return PartyOwnerEndpoints.ToResult(await parties.SetCoversAsync(
                    ctx.OwnerUserId, ctx.PartyId, body.InvitationCoverFileItemId,
                    body.LiveCoverFileItemId, body.Version, ct));
            })).WithName("SetPartyCrewCovers");

        MapTransition(app, "publish", PartyLifecycleAction.Publish, AuditActions.PartyPublish);
        MapTransition(app, "start-live", PartyLifecycleAction.StartLive, AuditActions.PartyStartLive);
        MapTransition(app, "end-live", PartyLifecycleAction.EndLive, AuditActions.PartyEndLive);

        // ── The album's party settings ──────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/album-settings", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyLinkService links,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, null, ct, async (ctx, albumId) =>
            {
                var status = await links.GetOwnerStatusAsync(ctx.OwnerUserId, albumId, ct);
                return status is null ? Results.NotFound() : Results.Ok(status);
            })).WithName("GetPartyCrewAlbumSettings");

        // TWO decisions share one route, so it is gated on both. Whether the
        // party is OPEN is the lifecycle — it is what publishes a draft — while
        // whether contributions need approving is the moderation configuration.
        // A role holding one and not the other gets exactly the half it holds.
        app.MapMethods("/api/party-crew/parties/{partyId:guid}/album-settings", ["PATCH"], (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyLinkService links,
            [FromServices] IAuditLogger audit,
            [FromBody] SetAlbumPartyModeRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.LifecycleManage, ct, (ctx, albumId) =>
            {
                var configures = body is not null && (body.UploadEnabled is not null
                    || body.RequireUploadApproval is not null
                    || body.RequireMessageApproval is not null);
                if (configures && !ctx.Can(PartyCrewCapabilities.ContributionsConfigure))
                    return Task.FromResult(Results.NotFound());
                return PartyAlbumSettingsOperations.SetPartyModeAsync(
                    links, audit, ctx.OwnerUserId, Actor(ctx), albumId, body, Ip(http), ct);
            })).WithName("SetPartyCrewAlbumSettings");

        app.MapMethods("/api/party-crew/parties/{partyId:guid}/slideshow-settings", ["PATCH"], (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyLinkService links,
            [FromBody] SetPartySlideshowSettingsRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsConfigure, ct, (ctx, albumId) =>
                PartyAlbumSettingsOperations.SetSlideshowSettingsAsync(
                    links, ctx.OwnerUserId, albumId, body, ct)))
            .WithName("SetPartyCrewSlideshowSettings");

        app.MapMethods("/api/party-crew/parties/{partyId:guid}/game-settings", ["PATCH"], (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyLinkService links,
            [FromBody] PartyGameSettingsRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, (ctx, albumId) =>
                PartyAlbumSettingsOperations.SetGameSettingsAsync(
                    links, ctx.OwnerUserId, albumId, body, ct)))
            .WithName("SetPartyCrewGameSettings");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/tv-visibility", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IAlbumService albums,
            [FromBody] SetAlbumTvVisibilityRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ScreensManage, ct, async (ctx, albumId) =>
            {
                if (body is null) return Results.BadRequest(new { error = "Missing request body." });
                var detail = await albums.SetTvVisibilityAsync(albumId, ctx.OwnerUserId, body.ShowOnTv, ct);
                return detail is null ? Results.NotFound() : Results.Ok(detail);
            })).WithName("SetPartyCrewTvVisibility");

        // ── What the party tells its guests ─────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/guest-content", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGuestContentService content,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.ExperienceManage, ct, async ctx =>
            {
                var slots = await content.ListAsync(ctx.OwnerUserId, ctx.PartyId, ct);
                return slots is null ? Results.NotFound() : Results.Ok(slots);
            })).WithName("ListPartyCrewGuestContent");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/guest-content/{kind}", (
            Guid partyId,
            string kind,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGuestContentService content,
            [FromBody] PartyOwnerEndpoints.PartyGuestContentRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.ExperienceManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "Missing request body." });
                return PartyOwnerEndpoints.ToResult(await content.UpsertAsync(
                    ctx.OwnerUserId, ctx.PartyId, kind,
                    new PartyGuestContentWrite(
                        body.Enabled, body.VisibleBefore, body.VisibleLive, body.VisibleAfter,
                        body.Content, body.Version, body.MediaFileItemId,
                        body.MediaPresentation ?? PartyGuestContentMediaPresentations.Inline,
                        body.TextAlign, body.MediaOrientation, body.MediaCrop, body.TextPlacement),
                    ct));
            })).WithName("SetPartyCrewGuestContent");

        // ── The guest list ──────────────────────────────────────────────────
        //
        // READING is `guests.read` and WRITING is `invitations.manage`, two
        // capabilities rather than one, because the reception desk needs the
        // names and the arrival ticks and has no business rewriting the list or
        // sending anything. A DIRECTOR holds neither and never fetches a name.

        app.MapPost("/api/party-crew/parties/{partyId:guid}/guest-directory/query", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGuestDirectoryService directory,
            [FromBody] PartyGuestDirectoryQuery? query,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.GuestsRead, ct, async ctx =>
            {
                var result = await directory.PageAsync(
                    ctx.OwnerUserId, ctx.PartyId,
                    query ?? new PartyGuestDirectoryQuery(null, null, null, null), ct);
                return result.Outcome switch
                {
                    PartyGuestDirectoryOutcome.Ok => Results.Ok(result.Page),
                    PartyGuestDirectoryOutcome.NotFound => Results.NotFound(),
                    _ => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
                };
            })).WithName("QueryPartyCrewGuestDirectory");

        app.MapGet("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.GuestsRead, ct, async ctx =>
            {
                var detail = await directory.GroupAsync(ctx.OwnerUserId, ctx.PartyId, groupId, ct);
                return detail is null ? Results.NotFound() : Results.Ok(detail);
            })).WithName("GetPartyCrewInvitationGroup");

        app.MapGet("/api/party-crew/parties/{partyId:guid}/rsvp-questions", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.GuestsRead, ct, async ctx =>
            {
                var questions = await directory.QuestionsAsync(ctx.OwnerUserId, ctx.PartyId, ct);
                return questions is null ? Results.NotFound() : Results.Ok(new { questions });
            })).WithName("GetPartyCrewRsvpQuestions");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/invitation-groups", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.GroupRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.CreateGroupAsync(
                        ctx.OwnerUserId, ctx.PartyId, PartyInvitationEndpoints.Write(body), ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("CreatePartyCrewInvitationGroup");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.GroupRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.UpdateGroupAsync(
                        ctx.OwnerUserId, ctx.PartyId, groupId,
                        PartyInvitationEndpoints.Write(body), body.Version, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("UpdatePartyCrewInvitationGroup");

        app.MapDelete("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}", (
            Guid partyId,
            Guid groupId,
            [FromQuery] int version,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
                PartyInvitationEndpoints.ToResult(
                    await invitations.DeleteGroupAsync(ctx.OwnerUserId, ctx.PartyId, groupId, version, ct),
                    PreferHeader.WantsMinimal(http))))
            .WithName("DeletePartyCrewInvitationGroup");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}/rotate-link", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.VersionRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.RotateLinkAsync(
                        ctx.OwnerUserId, ctx.PartyId, groupId, body.Version, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("RotatePartyCrewInvitationLink");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}/send", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationDeliveryService deliveries,
            [FromBody] PartyInvitationEndpoints.SendRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await deliveries.SendAsync(
                        ctx.OwnerUserId, ctx.PartyId, groupId, body.ClientRequestId, body.PartyVersion, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("SendPartyCrewInvitation")
            .RequireRateLimiting(PartyInvitationEndpoints.SendRateLimitPolicy);

        app.MapPost("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}/remind", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationDeliveryService deliveries,
            [FromBody] PartyInvitationEndpoints.SendRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await deliveries.RemindAsync(
                        ctx.OwnerUserId, ctx.PartyId, groupId, body.ClientRequestId, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("RemindPartyCrewInvitation")
            .RequireRateLimiting(PartyInvitationEndpoints.SendRateLimitPolicy);

        app.MapPost("/api/party-crew/parties/{partyId:guid}/invitation-groups/{groupId:guid}/share", (
            Guid partyId,
            Guid groupId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationDeliveryService deliveries,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyInvitationEndpoints.ShareRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                var result = await deliveries.ShareAsync(
                    ctx.OwnerUserId, ctx.PartyId, groupId, body.Channel, body.ClientRequestId,
                    body.PartyVersion, ct);
                return await PartyInvitationEndpoints.CompleteShareAsync(
                    audit, result, Actor(ctx), ctx.PartyId, groupId, Ip(http), ct);
            })).WithName("SharePartyCrewInvitation")
            .RequireRateLimiting(PartyInvitationEndpoints.ShareRateLimitPolicy);

        app.MapPost("/api/party-crew/parties/{partyId:guid}/rsvp-questions", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.QuestionRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.CreateQuestionAsync(
                        ctx.OwnerUserId, ctx.PartyId, PartyInvitationEndpoints.Write(body), ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("CreatePartyCrewRsvpQuestion");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/rsvp-questions/order", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.QuestionOrderRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body?.QuestionIds is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.ReorderQuestionsAsync(
                        ctx.OwnerUserId, ctx.PartyId, body.QuestionIds, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("ReorderPartyCrewRsvpQuestions");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/rsvp-questions/{questionId:guid}", (
            Guid partyId,
            Guid questionId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] PartyInvitationEndpoints.QuestionRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.InvitationsManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyInvitationEndpoints.ToResult(
                    await invitations.UpdateQuestionAsync(
                        ctx.OwnerUserId, ctx.PartyId, questionId,
                        PartyInvitationEndpoints.Write(body), body.Version, ct),
                    PreferHeader.WantsMinimal(http));
            })).WithName("UpdatePartyCrewRsvpQuestion");

        // ── Who arrived ─────────────────────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/attendance", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
            {
                var result = await attendance.GetAsync(ctx.OwnerUserId, ctx.PartyId, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })).WithName("GetPartyCrewAttendance");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/attendance/guests/{guestId:guid}", (
            Guid partyId,
            Guid guestId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
            {
                var result = await attendance.CheckInGuestAsync(ctx.OwnerUserId, ctx.PartyId, guestId, ct);
                if (result.Changed)
                {
                    await audit.LogAsync(
                        Actor(ctx), AuditActions.PartyAttendanceCheckIn, AuditEntityTypes.Party,
                        ctx.PartyId, Ip(http),
                        new { guestId, source = PartyAttendanceSources.Owner }, ct);
                }
                return PartyAttendanceEndpoints.ToResult(
                    result, PreferHeader.WantsMinimal(http), guestId: guestId);
            })).WithName("CheckInPartyCrewGuest");

        app.MapDelete("/api/party-crew/parties/{partyId:guid}/attendance/guests/{guestId:guid}", (
            Guid partyId,
            Guid guestId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
            {
                var result = await attendance.UndoGuestCheckInAsync(ctx.OwnerUserId, ctx.PartyId, guestId, ct);
                if (result.Changed)
                {
                    await audit.LogAsync(
                        Actor(ctx), AuditActions.PartyAttendanceUndo, AuditEntityTypes.Party,
                        ctx.PartyId, Ip(http),
                        new { guestId, source = PartyAttendanceSources.Owner }, ct);
                }
                return PartyAttendanceEndpoints.ToResult(
                    result, PreferHeader.WantsMinimal(http), guestId: guestId);
            })).WithName("UndoPartyCrewGuestCheckIn");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/attendance/other-guests", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            [FromBody] PartyAttendanceEndpoints.OtherGuestCreateRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                var result = await attendance.CreateOtherGuestAsync(
                    ctx.OwnerUserId, ctx.PartyId, body.Name, body.ClientRequestId, ct);
                return PartyAttendanceEndpoints.ToResult(
                    result, PreferHeader.WantsMinimal(http),
                    otherGuestId: result.AttendanceGuestId);
            })).WithName("CreatePartyCrewAttendanceGuest");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/attendance/other-guests/{attendanceGuestId:guid}", (
            Guid partyId,
            Guid attendanceGuestId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            [FromBody] PartyAttendanceEndpoints.OtherGuestUpdateRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                return PartyAttendanceEndpoints.ToResult(
                    await attendance.UpdateOtherGuestAsync(
                        ctx.OwnerUserId, ctx.PartyId, attendanceGuestId, body.Name, body.Version, ct),
                    PreferHeader.WantsMinimal(http), otherGuestId: attendanceGuestId);
            })).WithName("UpdatePartyCrewAttendanceGuest");

        app.MapDelete("/api/party-crew/parties/{partyId:guid}/attendance/other-guests/{attendanceGuestId:guid}", (
            Guid partyId,
            Guid attendanceGuestId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyAttendanceService attendance,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.AttendanceManage, ct, async ctx =>
                PartyAttendanceEndpoints.ToResult(
                    await attendance.DeleteOtherGuestAsync(
                        ctx.OwnerUserId, ctx.PartyId, attendanceGuestId, ct),
                    PreferHeader.WantsMinimal(http))))
            .WithName("DeletePartyCrewAttendanceGuest");

        // ── The photographs ─────────────────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/uploads", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyModerationService moderation,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsModerate, ct, async (ctx, albumId) =>
            {
                var list = await moderation.ListAsync(ctx.OwnerUserId, albumId, ct);
                return list is null ? Results.NotFound() : Results.Ok(list);
            })).WithName("ListPartyCrewUploads");

        MapUploadModeration(app, "hide", PartyUploadStatuses.Hidden, AuditActions.PartyUploadHide);
        MapUploadModeration(app, "approve", PartyUploadStatuses.Approved, AuditActions.PartyUploadApprove);
        MapUploadModeration(app, "reject", PartyUploadStatuses.Rejected, AuditActions.PartyUploadReject);
        MapUploadModeration(app, "restore", PartyUploadStatuses.Approved, AuditActions.PartyUploadRestore);

        // ── The greetings ───────────────────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/messages", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyMessageService messages,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsModerate, ct, async (ctx, albumId) =>
            {
                var list = await messages.ListForManagerAsync(albumId, ctx.OwnerUserId, ct);
                return list is null ? Results.NotFound() : Results.Ok(list);
            })).WithName("ListPartyCrewMessages");

        MapMessageModeration(app, "approve", PartyMessageModeration.Approve, AuditActions.PartyMessageApprove);
        MapMessageModeration(app, "reject", PartyMessageModeration.Reject, AuditActions.PartyMessageReject);
        MapMessageModeration(app, "hide", PartyMessageModeration.Hide, AuditActions.PartyMessageHide);
        MapMessageModeration(app, "restore", PartyMessageModeration.Restore, AuditActions.PartyMessageRestore);
        MapMessageHero(app, "promote-hero", true, AuditActions.PartyMessageHeroPromote);
        MapMessageHero(app, "demote-hero", false, AuditActions.PartyMessageHeroDemote);

        // ── The activities ──────────────────────────────────────────────────

        app.MapGet("/api/party-crew/parties/{partyId:guid}/challenges", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyChallengeService challenges,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
            {
                var list = await challenges.ListOwnerAsync(ctx.OwnerUserId, albumId, ct);
                return list is null ? Results.NotFound() : Results.Ok(list);
            })).WithName("ListPartyCrewChallenges");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/challenges", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyChallengeService challenges,
            [FromBody] PartyChallengeWriteRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
            {
                if (body is null) return Results.BadRequest();
                var created = await challenges.CreateAsync(ctx.OwnerUserId, albumId, body, ct);
                return created is null ? Results.BadRequest() : Results.Ok(created);
            })).WithName("CreatePartyCrewChallenge");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/challenges/order", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyChallengeService challenges,
            [FromBody] PartyChallengeReorderRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
            {
                if (body?.ChallengeIds is null) return Results.BadRequest();
                return await challenges.ReorderAsync(ctx.OwnerUserId, albumId, body.ChallengeIds, ct)
                    ? Results.NoContent() : Results.BadRequest();
            })).WithName("ReorderPartyCrewChallenges");

        app.MapPut("/api/party-crew/parties/{partyId:guid}/challenges/{challengeId:guid}", (
            Guid partyId,
            Guid challengeId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyChallengeService challenges,
            [FromBody] PartyChallengeWriteRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
            {
                if (body is null) return Results.BadRequest();
                var updated = await challenges.UpdateAsync(ctx.OwnerUserId, albumId, challengeId, body, ct);
                return updated is null ? Results.BadRequest() : Results.Ok(updated);
            })).WithName("UpdatePartyCrewChallenge");

        app.MapDelete("/api/party-crew/parties/{partyId:guid}/challenges/{challengeId:guid}", (
            Guid partyId,
            Guid challengeId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyChallengeService challenges,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
                await challenges.DeleteAsync(ctx.OwnerUserId, albumId, challengeId, ct)
                    ? Results.NoContent() : Results.NotFound()))
            .WithName("DeletePartyCrewChallenge");

        // The party's own photographs, so an activity can name one. Scoped to
        // the party's MAIN album and nothing else: a collaborator never reaches
        // the host's library, only the album this evening is about.
        app.MapGet("/api/party-crew/parties/{partyId:guid}/album-items", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IAlbumService albums,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesManage, ct, async (ctx, albumId) =>
            {
                var items = await albums.ListItemsAsync(albumId, ctx.OwnerUserId, ct);
                return items is null ? Results.NotFound() : Results.Ok(items);
            })).WithName("ListPartyCrewAlbumItems");

        // Running it. A separate capability from writing the list: the DIRECTOR
        // presses start and next all evening and never edits a question.
        app.MapGet("/api/party-crew/parties/{partyId:guid}/game", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGameService game,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesControl, ct, async (ctx, albumId) =>
            {
                var snapshot = await game.GetOwnerSnapshotAsync(ctx.OwnerUserId, albumId, ct);
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            })).WithName("GetPartyCrewGameSnapshot");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/game/commands", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGameService game,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyGameCommandRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesControl, ct, (ctx, albumId) =>
                PartyGameEndpoints.ExecuteAsync(
                    game, audit, ctx.OwnerUserId, Actor(ctx), albumId, body, Ip(http), ct)))
            .WithName("ExecutePartyCrewGameCommand");

        app.MapPost("/api/party-crew/parties/{partyId:guid}/game/plan", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyGameService game,
            [FromBody] PartyGamePlanRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ActivitiesControl, ct, (ctx, albumId) =>
                PartyGameEndpoints.PlanAsync(game, ctx.OwnerUserId, albumId, body, ct)))
            .WithName("PlanPartyCrewGame");

        // ── Printing ────────────────────────────────────────────────────────
        //
        // The party's print PROFILE — what guests may make and how many —
        // because that is this evening's decision. The print STATION and its
        // devices are not here: they are the installation's hardware, and a
        // collaborator at one party has no business re-pointing a printer.

        app.MapGet("/api/party-crew/parties/{partyId:guid}/print-settings", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyPrintProfileService profiles,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.PrintManage, ct, async (ctx, albumId) =>
            {
                var profile = await profiles.GetAsync(ctx.OwnerUserId, albumId, ct);
                return profile is null ? Results.NotFound() : Results.Ok(profile);
            })).WithName("GetPartyCrewPrintSettings");

        app.MapMethods("/api/party-crew/parties/{partyId:guid}/print-settings", ["PATCH"], (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyPrintProfileService profiles,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyPrintProfileRequest? body,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.PrintManage, ct, (ctx, albumId) =>
                PartyPrintOwnerEndpoints.SaveAsync(
                    profiles, audit, ctx.OwnerUserId, Actor(ctx), albumId, body, Ip(http), ct)))
            .WithName("SetPartyCrewPrintSettings");

        return app;
    }

    // ── The seam ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve, check, run — and one answer for every way it can go wrong.
    ///
    /// <para>No device, a revoked device, a revoked collaborator, a party that
    /// no longer exists, an owner who lost <c>party.access</c>, a capability
    /// this role does not hold: all of them are the same 404. A collaborator
    /// has no business learning which of those is true, and none of the
    /// distinctions helps them.</para>
    /// </summary>
    private static async Task<IResult> With(
        HttpContext http,
        Guid partyId,
        IPartyCrewAccessResolver resolver,
        string? capability,
        CancellationToken ct,
        Func<PartyCrewAccessContext, Task<IResult>> work)
    {
        NoStore(http);
        var ctx = await Resolve(http, partyId, resolver, ct);
        if (ctx is null) return Results.NotFound();
        if (capability is not null && !ctx.Can(capability)) return Results.NotFound();
        return await work(ctx);
    }

    /// <summary>
    /// The same, for the routes that also need the party's main album.
    ///
    /// <para>The album is resolved SERVER-SIDE from the party, never supplied.
    /// A party with no album yet answers not-found, which is also the honest
    /// answer: there is nothing to moderate, print or play with.</para>
    /// </summary>
    private static Task<IResult> WithAlbum(
        HttpContext http,
        Guid partyId,
        IPartyCrewAccessResolver resolver,
        string? capability,
        CancellationToken ct,
        Func<PartyCrewAccessContext, Guid, Task<IResult>> work)
        => With(http, partyId, resolver, capability, ct, ctx =>
            ctx.MainAlbumId is Guid albumId ? work(ctx, albumId) : Task.FromResult(Results.NotFound()));

    /// <summary>
    /// The device token from the cookie, at the party the ROUTE named.
    ///
    /// <para>The id is a selector, never an authority: the resolver requires it
    /// to match a grant this device actually holds, so naming another party is
    /// the same nothing as holding no device at all.</para>
    /// </summary>
    private static Task<PartyCrewAccessContext?> Resolve(
        HttpContext http, Guid partyId, IPartyCrewAccessResolver resolver, CancellationToken ct)
        => resolver.ResolveAsync(PartyCrewSession.Device(http), partyId, ct);

    private static void MapTransition(
        IEndpointRouteBuilder app, string segment, PartyLifecycleAction action, string auditAction)
    {
        app.MapPost($"/api/party-crew/parties/{{partyId:guid}}/party/{segment}", (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyOwnerEndpoints.PartyVersionedRequest? body,
            CancellationToken ct) =>
            With(http, partyId, resolver, PartyCrewCapabilities.LifecycleManage, ct, async ctx =>
            {
                if (body is null) return Results.BadRequest(new { error = "Missing request body." });
                var result = await parties.TransitionAsync(
                    ctx.OwnerUserId, ctx.PartyId, action, body.Version, ct);
                if (result.Outcome == PartyMutationOutcome.Ok)
                {
                    await audit.LogAsync(
                        Actor(ctx), auditAction, AuditEntityTypes.Party, ctx.PartyId, Ip(http),
                        new { status = result.Party!.Status }, ct);
                }
                return PartyOwnerEndpoints.ToResult(result);
            })).WithName($"PartyCrew{action}");
    }

    private static void MapUploadModeration(
        IEndpointRouteBuilder app, string segment, string status, string auditAction)
    {
        app.MapPost($"/api/party-crew/parties/{{partyId:guid}}/uploads/{{fileItemId:guid}}/{segment}", (
            Guid partyId,
            Guid fileItemId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsModerate, ct, (ctx, albumId) =>
                PartyEndpoints.ModeratePartyUploadAsync(
                    moderation, audit, ctx.OwnerUserId, Actor(ctx), albumId, fileItemId,
                    status, auditAction, Ip(http), ct)))
            .WithName($"PartyCrewUpload{segment}");
    }

    private static void MapMessageModeration(
        IEndpointRouteBuilder app, string segment, PartyMessageModeration action, string auditAction)
    {
        app.MapPost($"/api/party-crew/parties/{{partyId:guid}}/messages/{{messageId:guid}}/{segment}", (
            Guid partyId,
            Guid messageId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsModerate, ct, (ctx, albumId) =>
                PartyEndpoints.ModeratePartyMessageAsync(
                    messages, audit, ctx.OwnerUserId, Actor(ctx), albumId, messageId,
                    action, auditAction, Ip(http), ct)))
            .WithName($"PartyCrewMessage{segment}");
    }

    private static void MapMessageHero(
        IEndpointRouteBuilder app, string segment, bool hero, string auditAction)
    {
        app.MapPost($"/api/party-crew/parties/{{partyId:guid}}/messages/{{messageId:guid}}/{segment}", (
            Guid partyId,
            Guid messageId,
            HttpContext http,
            [FromServices] IPartyCrewAccessResolver resolver,
            [FromServices] IPartyMessageService messages,
            [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
            WithAlbum(http, partyId, resolver, PartyCrewCapabilities.ContributionsModerate, ct, (ctx, albumId) =>
                PartyEndpoints.SetPartyMessageHeroAsync(
                    messages, audit, ctx.OwnerUserId, Actor(ctx), albumId, messageId,
                    hero, auditAction, Ip(http), ct)))
            .WithName($"PartyCrewMessage{segment}");
    }

    /// <summary>
    /// Who the audit names. Never the owner, and never nobody: a collaborator
    /// did this, and the trail says so.
    /// </summary>
    private static AuditActor Actor(PartyCrewAccessContext ctx) => AuditActor.Crew(ctx.CollaboratorId);

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    // Guest names, arrivals, greetings awaiting a decision. Never cached.
    private static void NoStore(HttpContext http) =>
        http.Response.Headers.CacheControl = "no-store";
}

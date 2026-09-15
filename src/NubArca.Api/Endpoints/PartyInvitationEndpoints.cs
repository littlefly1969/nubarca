using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The guest list and the personal invitation.
///
/// <para>Two surfaces with two authorities, and they never meet. The HOST's
/// routes live under <c>/api/parties/{partyId}</c>, require
/// <c>party.access</c> and are owner-scoped in every query. The GUEST's live
/// under <c>/api/party-invitations/{token}</c> — deliberately not under
/// <c>/api/party/{token}</c>: the personal token is not the party's QR and must
/// never be mistaken for it by a route, a cookie path or a person reading a
/// log.</para>
///
/// <para>Nothing here logs or audits a name, an address, a phone number, a
/// dietary note, an answer, a token, a hash or an invitation URL.</para>
/// </summary>
public static class PartyInvitationEndpoints
{
    internal const string RsvpRateLimitPolicy = "party-rsvp";
    internal const string SendRateLimitPolicy = "party-invitation-send";
    private const string PartyPublicRateLimitPolicy = "party-public";
    private const string PartyPublicMediaRateLimitPolicy = "party-public-media";

    public sealed record GroupRequest(
        string? Label,
        string? RecipientEmail,
        string? Phone,
        int MaxAdditionalGuests,
        IReadOnlyList<PartyNamedGuestWrite>? Guests,
        int Version = 0);

    /// <summary>
    /// One click. <c>ClientRequestId</c> is minted by the browser per click and
    /// reused for its retries; <c>PartyVersion</c> is needed only when this send
    /// is what publishes a Draft party.
    /// </summary>
    public sealed record SendRequest(Guid ClientRequestId, int? PartyVersion = null);

    public sealed record VersionRequest(int Version);

    public sealed record QuestionRequest(
        string? Prompt,
        string? Kind,
        bool Required,
        IReadOnlyList<string?>? Options,
        bool IsActive = true,
        int Version = 0);

    public sealed record QuestionOrderRequest(IReadOnlyList<Guid>? QuestionIds);

    public static IEndpointRouteBuilder MapPartyInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // --- OWNER --------------------------------------------------------------

        // The whole list in one read — groups, people, answers, questions and
        // the summary — because every one of them is on the same screen and the
        // counts must describe the same rows the list shows.
        app.MapGet("/api/parties/{partyId:guid}/guest-list", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var list = await invitations.GetGuestListAsync(
                httpContext.GetCurrentUserId()!.Value, partyId, cancellationToken);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("GetPartyGuestList").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/parties/{partyId:guid}/invitation-groups", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            [FromBody] GroupRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.CreateGroupAsync(ownerUserId, partyId, Write(body), cancellationToken);
            if (result.Outcome == PartyInvitationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyInvitationGroupCreate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { namedGuests = body.Guests?.Count ?? 0 }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("CreatePartyInvitationGroup").RequirePermission(Permissions.PartyAccess);

        // PUT because it states the group whole — its named guests included. The
        // +1s the group added are not the host's to state and are kept.
        app.MapPut("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}", async (
            Guid partyId,
            Guid groupId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            [FromBody] GroupRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.UpdateGroupAsync(
                ownerUserId, partyId, groupId, Write(body), body.Version, cancellationToken);
            if (result is { Outcome: PartyInvitationOutcome.Ok, LinkRotated: true })
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyInvitationRotate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { invitationGroupId = groupId, reason = "recipient_changed" }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("UpdatePartyInvitationGroup").RequirePermission(Permissions.PartyAccess);

        app.MapDelete("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}", async (
            Guid partyId,
            Guid groupId,
            [FromQuery] int version,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.DeleteGroupAsync(ownerUserId, partyId, groupId, version, cancellationToken);
            if (result.Outcome == PartyInvitationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyInvitationGroupDelete, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { invitationGroupId = groupId }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("DeletePartyInvitationGroup").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}/rotate-link", async (
            Guid partyId,
            Guid groupId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            [FromBody] VersionRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.RotateLinkAsync(ownerUserId, partyId, groupId, body.Version, cancellationToken);
            if (result.Outcome == PartyInvitationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyInvitationRotate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { invitationGroupId = groupId, reason = "owner" }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("RotatePartyInvitationLink").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}/send", async (
            Guid partyId,
            Guid groupId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationDeliveryService deliveries,
            [FromServices] IAuditLogger audit,
            [FromBody] SendRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await deliveries.SendAsync(
                ownerUserId, partyId, groupId, body.ClientRequestId, body.PartyVersion, cancellationToken);
            await AuditDeliveryAsync(httpContext, audit, ownerUserId, partyId, groupId, result, cancellationToken);
            return ToResult(result);
        }).WithName("SendPartyInvitation")
            .RequirePermission(Permissions.PartyAccess)
            .RequireRateLimiting(SendRateLimitPolicy);

        app.MapPost("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}/remind", async (
            Guid partyId,
            Guid groupId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationDeliveryService deliveries,
            [FromServices] IAuditLogger audit,
            [FromBody] SendRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await deliveries.RemindAsync(
                ownerUserId, partyId, groupId, body.ClientRequestId, cancellationToken);
            await AuditDeliveryAsync(httpContext, audit, ownerUserId, partyId, groupId, result, cancellationToken);
            return ToResult(result);
        }).WithName("RemindPartyInvitation")
            .RequirePermission(Permissions.PartyAccess)
            .RequireRateLimiting(SendRateLimitPolicy);

        app.MapPost("/api/parties/{partyId:guid}/rsvp-questions", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            [FromBody] QuestionRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.CreateQuestionAsync(ownerUserId, partyId, Write(body), cancellationToken);
            if (result.Outcome == PartyInvitationOutcome.Ok)
            {
                // The kind, never the prompt: a question can name somebody.
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyRsvpQuestionCreate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { kind = body.Kind }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("CreatePartyRsvpQuestion").RequirePermission(Permissions.PartyAccess);

        // Registered before the {questionId} route so "order" is never read as one.
        app.MapPut("/api/parties/{partyId:guid}/rsvp-questions/order", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromBody] QuestionOrderRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body?.QuestionIds is null) return Results.BadRequest(new { error = "invalid_order" });
            var result = await invitations.ReorderQuestionsAsync(
                httpContext.GetCurrentUserId()!.Value, partyId, body.QuestionIds, cancellationToken);
            return ToResult(result);
        }).WithName("ReorderPartyRsvpQuestions").RequirePermission(Permissions.PartyAccess);

        app.MapPut("/api/parties/{partyId:guid}/rsvp-questions/{questionId:guid}", async (
            Guid partyId,
            Guid questionId,
            HttpContext httpContext,
            [FromServices] IPartyInvitationService invitations,
            [FromServices] IAuditLogger audit,
            [FromBody] QuestionRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await invitations.UpdateQuestionAsync(
                ownerUserId, partyId, questionId, Write(body), body.Version, cancellationToken);
            if (result.Outcome == PartyInvitationOutcome.Ok)
            {
                await audit.LogAsync(
                    ownerUserId, AuditActions.PartyRsvpQuestionUpdate, AuditEntityTypes.Party, partyId,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    new { questionId, isActive = body.IsActive }, cancellationToken);
            }
            return ToResult(result);
        }).WithName("UpdatePartyRsvpQuestion").RequirePermission(Permissions.PartyAccess);

        // --- GUEST (the personal invitation token) ------------------------------

        // Resolve, read, answer. It mints no participant and sets no cookie: an
        // invitation holder is not a browser at the party, and nothing here
        // pretends otherwise.
        app.MapGet("/api/party-invitations/{token}", async (
            string token,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await rsvp.ResolveAsync(token, cancellationToken);
            return access is null
                ? Results.NotFound()
                : Results.Ok(await rsvp.ViewAsync(access, token, cancellationToken));
        }).WithName("GetPartyInvitation").RequireRateLimiting(PartyPublicRateLimitPolicy);

        app.MapPut("/api/party-invitations/{token}/rsvp", async (
            string token,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            [FromBody] PartyRsvpWrite? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_rsvp" });
            var result = await rsvp.SubmitAsync(token, body, cancellationToken);
            return result.Outcome switch
            {
                PartyRsvpOutcome.Ok => Results.Ok(result.View),
                PartyRsvpOutcome.NotFound => Results.NotFound(),
                PartyRsvpOutcome.InvalidRequest => Results.BadRequest(new { error = result.Error }),
                // A stale form and a closed invitation are both states the page
                // can show, so the refusal carries the invitation as it is now.
                _ => Results.Json(
                    new { error = result.Error, invitation = result.View },
                    statusCode: StatusCodes.Status409Conflict),
            };
        }).WithName("SubmitPartyRsvp").RequireRateLimiting(RsvpRateLimitPolicy);

        // A slot's photograph, reached THROUGH the slot on this invitation's
        // current surface, by the same rule the party's page uses — and served
        // by the same one derivative path: derived, metadata-stripped, never an
        // original, never a download.
        app.MapGet("/api/party-invitations/{token}/content/{kind}/media", async (
            string token,
            string kind,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            [FromServices] IPartyGuestContentService guestContent,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await rsvp.ResolveAsync(token, cancellationToken);
            if (access is null)
            {
                return Results.NotFound();
            }
            var fileId = await guestContent.GuestMediaFileAsync(
                access.PartyId, access.OwnerUserId, access.Experience.Phase, kind, cancellationToken);
            return fileId is Guid id
                ? await PartyEndpoints.ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, id, PartyMediaKind.Image, "preview",
                    httpContext, thumbnails, stripper, cancellationToken)
                : Results.NotFound();
        }).WithName("GetPartyInvitationContentMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        // THE COVER the invitation opens on, and only while it is the one the
        // invitation shows. An album's chosen cover is served through album
        // membership, a party's own choice through its reference rule.
        app.MapGet("/api/party-invitations/{token}/cover/{which}/media", async (
            string token,
            string which,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            [FromServices] IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await rsvp.ResolveAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var cover = await rsvp.CoverAsync(access, cancellationToken);
            if (cover is not { } pick || pick.Which != which) return Results.NotFound();
            return pick.AlbumId is Guid albumId
                ? await PartyEndpoints.ServeMediaCoreAsync(
                    access.OwnerUserId, albumId, pick.FileId, "preview",
                    httpContext, partyMedia, thumbnails, stripper, cancellationToken)
                : await PartyEndpoints.ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, pick.FileId, PartyMediaKind.Image, "preview",
                    httpContext, thumbnails, stripper, cancellationToken);
        }).WithName("GetPartyInvitationCoverMedia").RequireRateLimiting(PartyPublicMediaRateLimitPolicy);

        return app;
    }

    private static PartyInvitationGroupWrite Write(GroupRequest body) =>
        new(body.Label, body.RecipientEmail, body.Phone, body.MaxAdditionalGuests, body.Guests);

    private static PartyRsvpQuestionWrite Write(QuestionRequest body) =>
        new(body.Prompt, body.Kind, body.Required, body.Options, body.IsActive);

    // Only a NEW attempt is an event. A replayed click sent nothing, so it
    // records nothing either.
    private static async Task AuditDeliveryAsync(
        HttpContext httpContext, IAuditLogger audit, Guid ownerUserId, Guid partyId, Guid groupId,
        PartyInvitationSendResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome != PartyInvitationOutcome.Ok || result.Delivery is not { Replayed: false } delivery)
        {
            return;
        }
        await audit.LogAsync(
            ownerUserId, AuditActions.PartyInvitationSend, AuditEntityTypes.Party, partyId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            new
            {
                partyId,
                invitationGroupId = groupId,
                deliveryKind = delivery.Kind,
                deliveryStatus = delivery.Status,
            },
            cancellationToken);
    }

    private static IResult ToResult(PartyInvitationResult result) => result.Outcome switch
    {
        PartyInvitationOutcome.Ok => Results.Ok(result.GuestList),
        PartyInvitationOutcome.NotFound => Results.NotFound(),
        PartyInvitationOutcome.InvalidRequest => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
        // Every other refusal describes a state, and carries the list as it is
        // now so the page adopts it instead of overwriting it.
        _ => Results.Json(
            new { error = result.Error ?? "conflict", guestList = result.GuestList },
            statusCode: StatusCodes.Status409Conflict),
    };

    private static IResult ToResult(PartyInvitationSendResult result) => result.Outcome switch
    {
        PartyInvitationOutcome.Ok => Results.Ok(new
        {
            delivery = result.Delivery,
            guestList = result.GuestList,
            party = result.Party,
        }),
        PartyInvitationOutcome.NotFound => Results.NotFound(),
        PartyInvitationOutcome.InvalidRequest => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
        _ => Results.Json(
            new { error = result.Error ?? "conflict", guestList = result.GuestList, party = result.Party },
            statusCode: StatusCodes.Status409Conflict),
    };

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

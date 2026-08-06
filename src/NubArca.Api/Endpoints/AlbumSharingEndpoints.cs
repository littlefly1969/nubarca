using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Security;

namespace NubArca.Api.Endpoints;

// SHARE-ALBUM-01: live album sharing between authenticated NubArca users.
//
// TWO ROUTE FAMILIES, ONE GATE:
//
//   /api/albums/{id}/members...    owner-only management (invite / permission /
//                                  revoke). Album ownership is the requirement.
//   /api/shared-albums/...         the recipient's view. EVERY request resolves
//                                  an AlbumAccessGrant first.
//
// The ordinary owner-only endpoints under /api/files/{id}/* are NOT touched.
// Their `OwnerUserId == caller` checks stay exactly as they were; shared media
// is served here by resolving a grant and then calling the SAME unchanged
// owner-scoped services with the ALBUM OWNER's id. That is the shape the public
// Party surface already uses, and it means a defect in sharing cannot widen a
// private endpoint.
//
// CACHING: shared derived media is `no-store`, unlike the owner's own
// `private, max-age=86400`. Revocation has to take effect immediately on every
// protected representation, and a response already sitting in the recipient's
// HTTP cache would outlive the revoke. Correctness beats the repeat-scroll
// saving here; the owner's own surfaces are unaffected.
public static class AlbumSharingEndpoints
{
    public static IEndpointRouteBuilder MapAlbumSharingEndpoints(this IEndpointRouteBuilder app)
    {
        MapOwnerMemberManagement(app);
        MapCollaborativeEditing(app);
        MapContribution(app);
        MapRecipientInvitations(app);
        MapSharedAlbumReads(app);
        MapSharedAlbumMedia(app);
        return app;
    }

    // ── Owner-only: who is this album shared with ───────────────────────────

    private static void MapOwnerMemberManagement(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/{id:guid}/members", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var members = await sharing.ListMembersAsync(ownerUserId, id, cancellationToken);
            return members is null ? Results.NotFound() : Results.Ok(members);
        }).WithName("ListAlbumMembers").RequireAuthorization();

        // Step 1 of inviting: confirm an exact email belongs to an invitable
        // account. POST (not GET) so the address never lands in a URL, a server
        // access log, a browser history entry or a Referer header.
        app.MapPost("/api/albums/{id:guid}/members/resolve", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromBody] ResolveAlbumRecipientRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var resolved = await sharing.ResolveRecipientAsync(
                ownerUserId, id, body?.Email, cancellationToken);
            // One answer for every failure — album missing/foreign, no such
            // account, disabled account, malformed address, or the owner's own
            // address. Nothing here can be used to probe the user directory.
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        }).WithName("ResolveAlbumRecipient").RequireAuthorization();

        app.MapPost("/api/albums/{id:guid}/members", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            [FromBody] InviteAlbumMemberRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var (result, member) = await sharing.InviteAsync(ownerUserId, id, body, cancellationToken);
            switch (result)
            {
                case InviteAlbumMemberResult.Ok:
                    // Audit carries the album id and the MEMBERSHIP id — never
                    // the recipient's email, display name or user id.
                    await audit.LogAsync(ownerUserId, AuditActions.AlbumShareInvite,
                        AuditEntityTypes.Album, id, ip,
                        new { membershipId = member!.MembershipId, role = member.Role }, cancellationToken);
                    return Results.Ok(member);
                case InviteAlbumMemberResult.AlbumNotFound:
                    return Results.NotFound();
                case InviteAlbumMemberResult.RecipientUnavailable:
                    return Results.NotFound(new { error = "No NubArca account can be invited with that address." });
                case InviteAlbumMemberResult.RecipientIsOwner:
                    return Results.BadRequest(new { error = "You already own this album." });
                case InviteAlbumMemberResult.AlreadyInvited:
                    return Results.Conflict(new { error = "That person already has access or a pending invitation." });
                case InviteAlbumMemberResult.RoleNotAssignable:
                    return Results.BadRequest(new { error = "That role cannot be assigned." });
                default:
                    return Results.BadRequest(new { error = "Enter a valid email address." });
            }
        }).WithName("InviteAlbumMember").RequireAuthorization();

        app.MapMethods("/api/albums/{id:guid}/members/{membershipId:guid}", ["PATCH"], async (
            Guid id,
            Guid membershipId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            [FromBody] UpdateAlbumMemberRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var (result, member) = await sharing.UpdateMemberAsync(
                ownerUserId, id, membershipId, body.AllowOriginalDownload, cancellationToken);
            if (result != AlbumMemberMutationResult.Ok)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.AlbumShareUpdate,
                AuditEntityTypes.Album, id, ip,
                new { membershipId, allowOriginalDownload = body.AllowOriginalDownload }, cancellationToken);
            return Results.Ok(member);
        }).WithName("UpdateAlbumMember").RequireAuthorization();

        // SHARE-ALBUM-02: promote Viewer → Contributor / demote Contributor →
        // Viewer. Owner-only; `editor` is refused. A demotion leaves existing
        // contributions in place — see IAlbumSharingService.
        app.MapMethods("/api/albums/{id:guid}/members/{membershipId:guid}/role", ["PATCH"], async (
            Guid id,
            Guid membershipId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            [FromBody] ChangeAlbumMemberRoleRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var (result, member) = await sharing.ChangeMemberRoleAsync(
                ownerUserId, id, membershipId, body.Role, cancellationToken);
            switch (result)
            {
                case InviteAlbumMemberResult.Ok:
                    await audit.LogAsync(ownerUserId, AuditActions.AlbumShareRoleChange,
                        AuditEntityTypes.Album, id, ip,
                        new { membershipId, role = member!.Role }, cancellationToken);
                    return Results.Ok(member);
                case InviteAlbumMemberResult.RoleNotAssignable:
                    return Results.BadRequest(new { error = "That role cannot be assigned." });
                default:
                    return Results.NotFound();
            }
        }).WithName("ChangeAlbumMemberRole").RequireAuthorization();

        // Cancels a pending invitation OR revokes an accepted membership — one
        // operation, because both mean the same thing to the person on the other
        // end. Idempotent.
        //
        // SHARE-ALBUM-02: also withdraws every item that member contributed, in
        // the same transaction, and audits each withdrawal separately from the
        // revocation itself.
        app.MapDelete("/api/albums/{id:guid}/members/{membershipId:guid}", async (
            Guid id,
            Guid membershipId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var (result, revoked) = await sharing.RevokeMemberAsync(
                ownerUserId, id, membershipId, cancellationToken);
            if (result != AlbumMemberMutationResult.Ok)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.AlbumShareRevoke,
                AuditEntityTypes.Album, id, ip,
                new { membershipId, withdrawnItems = revoked!.WithdrawnFileItemIds.Count },
                cancellationToken);

            // One event per withdrawn item. The ACTOR is the album owner who
            // revoked; the SOURCE OWNER is the member whose media left.
            foreach (var fileItemId in revoked.WithdrawnFileItemIds)
            {
                await audit.LogAsync(ownerUserId, AuditActions.AlbumContributionAutoWithdraw,
                    AuditEntityTypes.AlbumContribution, fileItemId, ip,
                    new
                    {
                        albumId = id,
                        albumOwnerUserId = ownerUserId,
                        sourceOwnerUserId = revoked.MemberUserId,
                        reason = "membership_revoked",
                    },
                    cancellationToken);
            }

            return Results.NoContent();
        }).WithName("RevokeAlbumMember").RequireAuthorization();

        // The OWNER's moderation view: their own items plus every contribution,
        // with provenance and current source state. Additive — nothing here
        // merges into the owner's gallery, library or album workspace.
        app.MapGet("/api/albums/{id:guid}/content", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var content = await sharing.ListAlbumContentAsync(actorUserId, id, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetNoStore(httpContext);
            return Results.Ok(content);
        }).WithName("ListAlbumContent").RequireAuthorization();

        // The owner removing ANY item — their own or a contribution. Album
        // membership only; the source file is never passed to a deletion path.
        app.MapDelete("/api/albums/{id:guid}/content/{fileId:guid}", async (
            Guid id,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var (result, removed) = await sharing.RemoveItemAsOwnerAsync(
                ownerUserId, id, fileId, cancellationToken);
            if (result != AlbumItemRemovalResult.Ok)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.AlbumContributionRemove,
                AuditEntityTypes.AlbumContribution, fileId, ip,
                new
                {
                    albumId = id,
                    albumOwnerUserId = ownerUserId,
                    sourceOwnerUserId = removed!.SourceOwnerUserId,
                    addedByUserId = removed.AddedByUserId,
                    reason = "removed_by_album_owner",
                },
                cancellationToken);
            return Results.NoContent();
        }).WithName("RemoveAlbumContentItem").RequireAuthorization();
    }

    // ── Contributor: linking and withdrawing own media ──────────────────────

    private static void MapContribution(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/shared-albums/{albumId:guid}/contributions", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IAuditLogger audit,
            [FromBody] ContributeAlbumItemRequest? body,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var result = await sharing.ContributeAsync(
                actorUserId, albumId, body.FileItemId, cancellationToken);
            switch (result)
            {
                case AlbumContributionResult.Ok:
                    // The ALBUM OWNER is recorded alongside the actor: the same
                    // media may be contributed to several albums, and the trail
                    // has to say whose album gained it.
                    var grant = await access.ResolveAsync(albumId, actorUserId, cancellationToken);
                    await audit.LogAsync(actorUserId, AuditActions.AlbumContributionAdd,
                        AuditEntityTypes.AlbumContribution, body.FileItemId, ip,
                        new
                        {
                            albumId,
                            albumOwnerUserId = grant?.AlbumOwnerUserId,
                            sourceOwnerUserId = actorUserId,
                        },
                        cancellationToken);
                    return Results.NoContent();
                case AlbumContributionResult.AlreadyPresent:
                    return Results.Conflict(new { error = "That item is already in this album." });
                case AlbumContributionResult.RoleNotPermitted:
                    return Results.Forbid();
                case AlbumContributionResult.FileNotContributable:
                    // Missing, foreign, deleted, excluded, vaulted or non-media
                    // all collapse here — no existence leak.
                    return Results.NotFound();
                default:
                    return Results.NotFound();
            }
        }).WithName("ContributeToSharedAlbum").RequireAuthorization();

        // "Remove MY contribution" — never "delete the file". Allowed after a
        // downgrade to Viewer, and after revocation, because the right comes
        // from owning the media and having contributed it.
        app.MapDelete("/api/shared-albums/{albumId:guid}/contributions/{fileId:guid}", async (
            Guid albumId,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var result = await sharing.WithdrawContributionAsync(
                actorUserId, albumId, fileId, cancellationToken);
            if (result != AlbumItemRemovalResult.Ok)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(actorUserId, AuditActions.AlbumContributionWithdraw,
                AuditEntityTypes.AlbumContribution, fileId, ip,
                new
                {
                    albumId,
                    sourceOwnerUserId = actorUserId,
                    reason = "withdrawn_by_source_owner",
                },
                cancellationToken);
            return Results.NoContent();
        }).WithName("WithdrawSharedAlbumContribution").RequireAuthorization();
    }


    // ── SHARE-ALBUM-03: collaborative editing ───────────────────────────────
    //
    // Title, description, cover, order and editorial removal — all on the
    // album's COLLABORATIVE surface and all through IAlbumEditingService, so
    // the Owner and an Editor traverse identical authorization, concurrency and
    // audit. The existing owner-only routes are untouched.
    //
    // Every mutation carries `expectedVersion`. A stale one answers 409 with the
    // album's CURRENT state so the client refreshes and tells the user what
    // happened, instead of blindly retrying a destructive command. Nothing is
    // written and nothing is audited on a conflict.
    //
    // The AUDIT is written by the service, inside the mutation's transaction —
    // not here. Auditing after the service returned would mean a curation
    // change could commit and the entry explaining it could then fail.
    private static void MapCollaborativeEditing(IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/shared-albums/{albumId:guid}", ["PATCH"], async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumEditingService editing,
            [FromBody] EditAlbumDetailsRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null) return Results.BadRequest(new { error = "Missing request body." });
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await editing.UpdateDetailsAsync(
                actorUserId, albumId, body.ExpectedVersion, body.Name, body.Description,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return Respond(result, httpContext, albumId);
        }).WithName("EditSharedAlbumDetails").RequireAuthorization();

        app.MapMethods("/api/shared-albums/{albumId:guid}/cover", ["PUT"], async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumEditingService editing,
            [FromBody] SetAlbumCoverRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null) return Results.BadRequest(new { error = "Missing request body." });
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await editing.SetCoverAsync(
                actorUserId, albumId, body.ExpectedVersion, body.FileItemId,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return Respond(result, httpContext, albumId);
        }).WithName("SetSharedAlbumCover").RequireAuthorization();

        app.MapMethods("/api/shared-albums/{albumId:guid}/order", ["PUT"], async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumEditingService editing,
            [FromBody] ReorderAlbumRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body?.AlbumItemIds is null)
                return Results.BadRequest(new { error = "Missing 'albumItemIds'." });
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await editing.ReorderAsync(
                actorUserId, albumId, body.ExpectedVersion, body.AlbumItemIds,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return Respond(result, httpContext, albumId);
        }).WithName("ReorderSharedAlbum").RequireAuthorization();

        // EDITORIAL removal of any item. Distinct from a contributor withdrawing
        // their own — which action happened follows the route invoked, not the
        // actor's identity, so an Editor removing their own contribution is
        // recorded as an editorial removal because that is what they asked for.
        app.MapDelete("/api/shared-albums/{albumId:guid}/items/{albumItemId:guid}", async (
            Guid albumId,
            Guid albumItemId,
            [FromQuery] int expectedVersion,
            HttpContext httpContext,
            [FromServices] IAlbumEditingService editing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await editing.RemoveItemAsync(
                actorUserId, albumId, expectedVersion, albumItemId,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return Respond(result, httpContext, albumId);
        }).WithName("RemoveSharedAlbumItem").RequireAuthorization();
    }

    // One mapping from editorial outcome to HTTP, so every editing route answers
    // the same way. Pure: the audit already happened inside the service's
    // transaction, so there is nothing to write here.
    private static IResult Respond(AlbumEditResult result, HttpContext httpContext, Guid albumId)
    {
        switch (result.Outcome)
        {
            case AlbumEditOutcome.Ok:
                SetNoStore(httpContext);
                return Results.Ok(new
                {
                    albumId,
                    version = result.Version,
                    name = result.Name,
                    description = result.Description,
                    coverFileItemId = result.CoverFileItemId,
                });

            case AlbumEditOutcome.VersionConflict:
                // 409 with the CURRENT state: enough for the client to refresh
                // and explain the collision without a second round-trip.
                SetNoStore(httpContext);
                return Results.Json(new
                {
                    error = result.Message,
                    albumId,
                    version = result.Version,
                    name = result.Name,
                    description = result.Description,
                    coverFileItemId = result.CoverFileItemId,
                }, statusCode: StatusCodes.Status409Conflict);

            case AlbumEditOutcome.RoleNotPermitted:
                return Results.Forbid();

            case AlbumEditOutcome.InvalidCommand:
                return Results.BadRequest(new { error = result.Message });

            case AlbumEditOutcome.ItemNotFound:
            default:
                // NotAccessible collapses here too: a non-member must not learn
                // the album exists.
                return Results.NotFound();
        }
    }

    // ── Recipient: invitations addressed to me ──────────────────────────────

    private static void MapRecipientInvitations(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/shared-albums/invitations", async (
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var invitations = await sharing.ListInvitationsAsync(actorUserId, cancellationToken);
            SetNoStore(httpContext);
            return Results.Ok(invitations);
        }).WithName("ListAlbumInvitations").RequireAuthorization();

        app.MapPost("/api/shared-albums/invitations/{membershipId:guid}/accept", async (
            Guid membershipId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await RespondAsync(membershipId, accept: true, httpContext, sharing, audit, cancellationToken))
            .WithName("AcceptAlbumInvitation").RequireAuthorization();

        app.MapPost("/api/shared-albums/invitations/{membershipId:guid}/decline", async (
            Guid membershipId,
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await RespondAsync(membershipId, accept: false, httpContext, sharing, audit, cancellationToken))
            .WithName("DeclineAlbumInvitation").RequireAuthorization();
    }

    private static async Task<IResult> RespondAsync(
        Guid membershipId,
        bool accept,
        HttpContext httpContext,
        IAlbumSharingService sharing,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        var actorUserId = httpContext.GetCurrentUserId()!.Value;
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();

        var result = await sharing.RespondToInvitationAsync(
            actorUserId, membershipId, accept, cancellationToken);
        if (result != AlbumInvitationResponseResult.Ok)
        {
            return Results.NotFound();
        }

        // The ACTOR is the recipient here, not the album owner — the audit trail
        // has to keep the two apart. entityId is the membership, because the
        // recipient is not entitled to have the album id treated as theirs.
        await audit.LogAsync(actorUserId,
            accept ? AuditActions.AlbumShareAccept : AuditActions.AlbumShareDecline,
            AuditEntityTypes.AlbumMembership, membershipId, ip, null, cancellationToken);
        return Results.NoContent();
    }

    // ── Recipient: the shared album itself ──────────────────────────────────

    private static void MapSharedAlbumReads(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/shared-albums", async (
            HttpContext httpContext,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var albums = await sharing.ListSharedWithMeAsync(actorUserId, cancellationToken);
            SetNoStore(httpContext);
            return Results.Ok(albums);
        }).WithName("ListSharedAlbums").RequireAuthorization();

        app.MapGet("/api/shared-albums/{albumId:guid}", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var grant = await access.ResolveAsync(albumId, actorUserId, cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            var detail = await sharing.GetSharedAlbumAsync(grant, cancellationToken);
            SetNoStore(httpContext);
            return Results.Ok(detail);
        }).WithName("GetSharedAlbum").RequireAuthorization();

        app.MapGet("/api/shared-albums/{albumId:guid}/items", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IAlbumSharingService sharing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var grant = await access.ResolveAsync(albumId, actorUserId, cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            var items = await sharing.ListSharedItemsAsync(grant, cancellationToken);
            SetNoStore(httpContext);
            return Results.Ok(items);
        }).WithName("ListSharedAlbumItems").RequireAuthorization();
    }

    // ── Recipient: media bytes ──────────────────────────────────────────────
    //
    // Each of these resolves the grant, then delegates to the SAME owner-scoped
    // service the owner's own endpoint uses, passing the ALBUM OWNER's id. The
    // services are unchanged; the only new thing is who is allowed to ask.

    private static void MapSharedAlbumMedia(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/thumbnail", async (
            Guid albumId,
            Guid fileId,
            [FromQuery] string? size,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            // Only the grid size is reachable through this route; the viewer has
            // its own /preview. An unknown value is a 400, exactly as on the
            // owner's endpoint.
            var requested = string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size!;
            if (requested != ThumbnailSizes.Small)
            {
                return Results.BadRequest(new { error = $"Unknown thumbnail size '{requested}'." });
            }

            return await ServeDerivativeAsync(
                albumId, fileId, ThumbnailSizes.Small, only: null,
                httpContext, access, thumbnails, cancellationToken);
        }).WithName("GetSharedAlbumThumbnail").RequireAuthorization();

        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/preview", async (
            Guid albumId,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
            await ServeDerivativeAsync(
                albumId, fileId, ThumbnailSizes.Medium, only: null,
                httpContext, access, thumbnails, cancellationToken))
            .WithName("GetSharedAlbumPreview").RequireAuthorization();

        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/poster", async (
            Guid albumId,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
            await ServeDerivativeAsync(
                albumId, fileId, ThumbnailSizes.Poster, only: SharedMediaKind.Video,
                httpContext, access, thumbnails, cancellationToken))
            .WithName("GetSharedAlbumPoster").RequireAuthorization();

        // Adaptive playback. Mirrors /api/files/{id}/video: the master playlist
        // when the ladder is published, 202 while it is being prepared, 404
        // otherwise — the gate inside VideoHlsServingService is unchanged and
        // still owner-scoped, it is simply asked on the album owner's behalf.
        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/video", async (
            Guid albumId,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var grant = await access.ResolveMediaAsync(
                albumId, actorUserId, fileId, SharedMediaAccess.Derived, cancellationToken);
            if (grant is null || grant.Kind != SharedMediaKind.Video)
            {
                return Results.NotFound();
            }

            // Without the HLS provider the only alternative is streaming the
            // ORIGINAL bytes, which is exactly what `allowDownload` gates. A
            // share must not hand over the untouched original through a
            // playback URL, so this route is HLS-only: with the provider off it
            // is a 404 and the client falls back to the poster.
            if (!hlsServing.Enabled)
            {
                return Results.NotFound();
            }

            var master = await hlsServing.GetMasterAsync(
                fileId, grant.MediaOwnerUserId, cancellationToken);
            SetNoStore(httpContext);
            return master.Status switch
            {
                VideoHlsMasterStatus.Ready => Results.Text(
                    RewriteSharedMasterUris(master.MasterPlaylist!),
                    VideoHlsServingService.MasterContentType),
                VideoHlsMasterStatus.Preparing =>
                    VideoHlsServingService.Preparing(httpContext.Response),
                _ => Results.NotFound(),
            };
        }).WithName("GetSharedAlbumVideo").RequireAuthorization();

        // Ladder child files. Re-runs the FULL grant + membership + availability
        // check on every segment request, so a revoke mid-playback stops the
        // next segment. `file` is untrusted URL input and is whitelisted inside
        // HlsDerivativeStorage, unchanged.
        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/video/{rendition}/{file}", async (
            Guid albumId,
            Guid fileId,
            string rendition,
            string file,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            if (!hlsServing.Enabled)
            {
                return Results.NotFound();
            }

            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var grant = await access.ResolveMediaAsync(
                albumId, actorUserId, fileId, SharedMediaAccess.Derived, cancellationToken);
            if (grant is null || grant.Kind != SharedMediaKind.Video)
            {
                return Results.NotFound();
            }

            var content = await hlsServing.OpenLadderFileAsync(
                fileId, grant.MediaOwnerUserId, $"{rendition}/{file}", cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetNoStore(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetSharedAlbumVideoHlsFile").RequireAuthorization();

        // The ORIGINAL bytes. Requires the membership's allowOriginalDownload;
        // the check lives in ResolveMediaAsync, so "download is off" and "not a
        // member of this album" are the same 404.
        app.MapGet("/api/shared-albums/{albumId:guid}/media/{fileId:guid}/content", async (
            Guid albumId,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumAccessResolver access,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var actorUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var grant = await access.ResolveMediaAsync(
                albumId, actorUserId, fileId, SharedMediaAccess.Original, cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            var content = await files.OpenContentAsync(
                fileId, grant.MediaOwnerUserId, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            // The ACTOR is the downloader, not the media owner. Recording the
            // owner here would make a share indistinguishable from the owner's
            // own download in the audit trail.
            await audit.LogAsync(actorUserId, AuditActions.AlbumShareDownload,
                AuditEntityTypes.File, fileId, ip,
                new { albumId, sizeBytes = content.SizeBytes }, cancellationToken);

            SetNoStore(httpContext);
            return Results.File(
                content.Content,
                SafeContentType.ForServing(content.DetectedContentType),
                content.FileName);
        }).WithName("DownloadSharedAlbumOriginal").RequireAuthorization();
    }

    // Resolve → pick the derivative that actually exists for this media kind →
    // hand the ALBUM OWNER's id to the unchanged owner-scoped thumbnail service.
    //
    // `imageSize` is the derivative wanted for a STILL image. A video has no
    // small/medium image derivative — its grid tile and its viewer image are
    // both the poster — so a video resolves to the poster whichever of
    // /thumbnail and /preview was asked for. /poster passes SharedMediaKind.Video
    // as `only`, which makes it 404 on a still image rather than quietly serving
    // that image's thumbnail under a poster URL.
    private static async Task<IResult> ServeDerivativeAsync(
        Guid albumId,
        Guid fileId,
        string imageSize,
        SharedMediaKind? only,
        HttpContext httpContext,
        IAlbumAccessResolver access,
        IFileThumbnailService thumbnails,
        CancellationToken cancellationToken)
    {
        var actorUserId = httpContext.GetCurrentUserId()!.Value;

        var grant = await access.ResolveMediaAsync(
            albumId, actorUserId, fileId, SharedMediaAccess.Derived, cancellationToken);
        if (grant is null)
        {
            return Results.NotFound();
        }

        if (only is not null && grant.Kind != only)
        {
            return Results.NotFound();
        }

        var size = grant.Kind == SharedMediaKind.Video ? ThumbnailSizes.Poster : imageSize;

        var content = await thumbnails.EnsureAsync(
            fileId, grant.MediaOwnerUserId, size, cancellationToken);
        if (content is null)
        {
            return Results.NotFound();
        }

        SetNoStore(httpContext);
        return Results.File(content.Content, content.MimeType);
    }

    // The master playlist's rendition URIs are relative to the playlist URL's
    // directory. VideoHlsServingService already prefixes them with "video/" for
    // the owner route .../files/{id}/video; the shared route has the same
    // trailing "/video" segment, so the same prefix resolves correctly here and
    // the playlist needs no further rewriting. Kept as a named no-op so the
    // dependence is explicit rather than accidental.
    private static string RewriteSharedMasterUris(string master) => master;

    // Shared media is never stored by an intermediary or by the recipient's
    // browser cache: a revoke has to be effective on the next request, and a
    // cached 200 would not be. See the class comment.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

// SHARE-ALBUM-02 request body. The file must be one the ACTOR owns; the server
// re-checks that rather than trusting the id.
public sealed record ContributeAlbumItemRequest(Guid FileItemId);

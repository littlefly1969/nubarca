using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// SHARE-COPY-01: one-time DETACHED album copies.
//
// TWO ROUTE FAMILIES, BOTH FULLY AUTHENTICATED:
//
//   /api/albums/{id}/transfers…   the SENDER: preview what would be copied and
//                                 send it. Album ownership is the requirement.
//   /api/album-transfers/…        both parties' own lists, plus the four
//                                 lifecycle verbs. Every route re-derives the
//                                 caller and matches it against the transfer's
//                                 sender or recipient — the transfer id is
//                                 never itself an authorization token.
//
// A transfer id grants NO media access. There is deliberately no route that
// serves bytes, thumbnails or a manifest for a PENDING transfer: before
// acceptance the recipient sees a count, a size, a title and who sent it, and
// nothing else. After acceptance the media is theirs and is served by the
// ordinary owner-scoped /api/files/* endpoints, unchanged.
//
// Every "not yours" answer is 404, never 403: a transfer must not confirm its
// own existence to somebody it was not addressed to.
public static class AlbumTransferEndpoints
{
    public static IEndpointRouteBuilder MapAlbumTransferEndpoints(this IEndpointRouteBuilder app)
    {
        MapSenderRoutes(app);
        MapRecipientRoutes(app);
        return app;
    }

    // ── Sender ──────────────────────────────────────────────────────────────

    private static void MapSenderRoutes(IEndpointRouteBuilder app)
    {
        // What WOULD be copied, and what would stop it. Lets the owner see a
        // refusal before committing to it, using the same predicate the send
        // itself uses.
        app.MapGet("/api/albums/{id:guid}/transfer-preview", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var preview = await transfers.PreviewAsync(ownerUserId, id, cancellationToken);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).WithName("PreviewAlbumTransfer").RequireAuthorization();

        // POST so the recipient's address never lands in a URL, a server log or
        // a Referer header — same reasoning as the invite flow.
        app.MapPost("/api/albums/{id:guid}/transfers", async (
            Guid id,
            [FromBody] SendAlbumTransferRequest request,
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var (result, transfer, blockers) = await transfers.SendAsync(
                ownerUserId, id, request.Email, cancellationToken);

            return result switch
            {
                AlbumTransferSendResult.Ok => Results.Ok(transfer),
                AlbumTransferSendResult.AlbumNotFound => Results.NotFound(),
                AlbumTransferSendResult.RecipientNotFound =>
                    Results.BadRequest(new { error = "recipient_not_found" }),
                AlbumTransferSendResult.RecipientIsSender =>
                    Results.BadRequest(new { error = "recipient_is_sender" }),
                // 409, not 400: the album is fine, its CURRENT CONTENT is what
                // conflicts. The blockers say what and how many, never which
                // files — naming a contributor's item would leak across the
                // ownership boundary the refusal exists to protect.
                AlbumTransferSendResult.ContainsIneligibleItems =>
                    Results.Conflict(new { error = "contains_ineligible_items", blockers }),
                AlbumTransferSendResult.EmptyAlbum =>
                    Results.BadRequest(new { error = "empty_album" }),
                AlbumTransferSendResult.AlreadyPending =>
                    Results.Conflict(new { error = "already_pending" }),
                _ => Results.BadRequest(),
            };
        }).WithName("SendAlbumTransfer").RequireAuthorization();

        app.MapGet("/api/album-transfers/sent", async (
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await transfers.ListSentAsync(userId, cancellationToken));
        }).WithName("ListSentAlbumTransfers").RequireAuthorization();

        // Withdraw a PENDING offer. An accepted copy is the recipient's and is
        // never recallable — that path answers 409, not 200.
        app.MapPost("/api/album-transfers/{id:guid}/cancel", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var result = await transfers.CancelAsync(userId, id, cancellationToken);
            return ToResponse(result);
        }).WithName("CancelAlbumTransfer").RequireAuthorization();
    }

    // ── Recipient ───────────────────────────────────────────────────────────

    private static void MapRecipientRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/album-transfers/received", async (
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await transfers.ListReceivedAsync(userId, cancellationToken));
        }).WithName("ListReceivedAlbumTransfers").RequireAuthorization();

        app.MapPost("/api/album-transfers/{id:guid}/accept", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var acceptance = await transfers.AcceptAsync(userId, id, cancellationToken);

            return acceptance.Result switch
            {
                // Idempotent: a repeat accept returns the SAME album id.
                AlbumTransferResponseResult.Ok =>
                    Results.Ok(new { albumId = acceptance.CreatedAlbumId }),
                AlbumTransferResponseResult.NotFound => Results.NotFound(),
                AlbumTransferResponseResult.Expired =>
                    Results.Conflict(new { error = "expired" }),
                AlbumTransferResponseResult.Cancelled =>
                    Results.Conflict(new { error = "cancelled" }),
                AlbumTransferResponseResult.AlreadyResolved =>
                    Results.Conflict(new { error = "already_resolved" }),
                // Deliberately says only that the offer is no longer available:
                // whether a particular account is disabled is not the
                // recipient's business.
                AlbumTransferResponseResult.SenderUnavailable =>
                    Results.Conflict(new { error = "sender_unavailable" }),
                // Logical byte figures for the RECIPIENT's own account only —
                // their own quota is not a leak, and they need the numbers to
                // act on the refusal.
                AlbumTransferResponseResult.QuotaExceeded =>
                    Results.Json(
                        new
                        {
                            error = "quota_exceeded",
                            requiredBytes = acceptance.RequiredBytes,
                            remainingBytes = acceptance.RemainingBytes,
                        },
                        statusCode: StatusCodes.Status409Conflict),
                _ => Results.BadRequest(),
            };
        }).WithName("AcceptAlbumTransfer").RequireAuthorization();

        app.MapPost("/api/album-transfers/{id:guid}/decline", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            var result = await transfers.DeclineAsync(userId, id, cancellationToken);
            return ToResponse(result);
        }).WithName("DeclineAlbumTransfer").RequireAuthorization();
    }

    private static IResult ToResponse(AlbumTransferResponseResult result) => result switch
    {
        AlbumTransferResponseResult.Ok => Results.NoContent(),
        AlbumTransferResponseResult.NotFound => Results.NotFound(),
        AlbumTransferResponseResult.Expired => Results.Conflict(new { error = "expired" }),
        AlbumTransferResponseResult.Cancelled => Results.Conflict(new { error = "cancelled" }),
        AlbumTransferResponseResult.AlreadyResolved =>
            Results.Conflict(new { error = "already_resolved" }),
        AlbumTransferResponseResult.SenderUnavailable =>
            Results.Conflict(new { error = "sender_unavailable" }),
        _ => Results.BadRequest(),
    };
}

public sealed record SendAlbumTransferRequest(string? Email);

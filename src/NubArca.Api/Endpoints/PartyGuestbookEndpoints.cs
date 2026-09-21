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
/// <para>The public half rides the party's VIEW token — the one on the QR —
/// rather than the upload token. Reading the book is part of looking at the
/// party, and a host may keep a book while accepting no photographs at all: the
/// three contributions are independent, so the book must not hang off the
/// upload capability's switch. No new token model was introduced; this is the
/// same capability, the same seam, the same guest session cookie and the same
/// rate-limit family every other public Party surface uses.</para>
///
/// <para>The owner half is PARTY-scoped rather than album-scoped, because the
/// book is. A party may be re-minted, re-linked, or hold no album at all, and
/// its book survives every one of those.</para>
///
/// <para><b>Nothing here projects anything.</b> There is no route that promotes
/// a dedication, none that a television calls, and no shape shared with
/// <c>PartyMessage</c>. That is the invariant, expressed as an absence.</para>
/// </summary>
public static class PartyGuestbookEndpoints
{
    /// <summary>
    /// What a guest sends. No party id, no participant id, no owner: the only
    /// authority in the request is the token in the route.
    /// </summary>
    public sealed record SubmitGuestbookEntryRequest(string? AuthorDisplayName, string? Body);

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
            var page = await guestbook.GetPublicPageAsync(access, reader, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).WithName("GetPartyGuestbook").RequireRateLimiting(PartyEndpoints.PublicRateLimitPolicy);

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
                access, body.AuthorDisplayName, body.Body, participantId, cancellationToken);

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

            if (result.Error is PartyGuestbookSubmissionError error)
            {
                // Safe, machine-readable codes with the limits beside them. The
                // client enforces the same limits from the same contract, so
                // this is the backstop rather than the copy a guest usually
                // reads.
                return Results.BadRequest(new
                {
                    error = error == PartyGuestbookSubmissionError.InvalidAuthorDisplayName
                        ? "guestbook_invalid_author"
                        : "guestbook_invalid_body",
                    maxAuthorDisplayNameLength = PartyGuestbookLimits.MaxAuthorDisplayNameLength,
                    maxBodyLength = PartyGuestbookLimits.MaxBodyLength,
                });
            }

            var entry = result.Entry!;
            // The line records that somebody wrote in the book and what it
            // became. The BODY is never logged anywhere, exactly as a
            // greeting's is not.
            await audit.LogAsync(
                actor: null,
                action: AuditActions.PartyGuestbookSubmit,
                entityType: AuditEntityTypes.PartyGuestbookEntry,
                entityId: entry.Id,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { status = entry.Status },
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
            var list = await guestbook.ListForManagerAsync(partyId, actorUserId, cancellationToken);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("ListPartyGuestbook").RequirePermission(Permissions.PartyAccess);

        MapModeration(app, "approve", PartyMessageModeration.Approve, AuditActions.PartyGuestbookApprove);
        MapModeration(app, "reject", PartyMessageModeration.Reject, AuditActions.PartyGuestbookReject);
        MapModeration(app, "hide", PartyMessageModeration.Hide, AuditActions.PartyGuestbookHide);
        MapModeration(app, "restore", PartyMessageModeration.Restore, AuditActions.PartyGuestbookRestore);

        return app;
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

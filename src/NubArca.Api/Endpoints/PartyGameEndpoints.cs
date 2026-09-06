using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Party Game runtime surface: one snapshot the owner reads, one command the
/// owner sends, and one snapshot every display reads.
///
/// There is no realtime transport here and that is deliberate. NubArca has no
/// SignalR, WebSocket or SSE anywhere in application code — the paired
/// television, the TV browser, the pairing screen and the party page all poll a
/// snapshot — so the game polls the same way. A client is notified that
/// something changed by seeing a higher <c>version</c>, and it recovers from a
/// refresh, a backgrounded tab or a lost network by reading the snapshot again.
/// That is the whole reconnection story, and it needs no reconnection code.
/// </summary>
public static class PartyGameEndpoints
{
    private const string PartyPublicRateLimitPolicy = "party-public";

    // A vote is a write from a phone at a party, so it is limited on the same
    // policy as a guest greeting rather than on the read policy.
    private const string PartyMessageRateLimitPolicy = "party-message";

    public static IEndpointRouteBuilder MapPartyGameEndpoints(this IEndpointRouteBuilder app)
    {
        // Owner: the complete state of their own game. 404 for a missing or
        // foreign album, a party that is off, and a party whose game switch is
        // off — one generic answer, as everywhere else in Party.
        app.MapGet("/api/albums/{albumId:guid}/party-game", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] IPartyGameService game,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            var snapshot = await game.GetOwnerSnapshotAsync(ownerId, albumId, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithName("GetPartyGameSnapshot").RequireAuthorization();

        // Owner: one command, quoting the version it believes it is acting on.
        //
        // A refusal is a 409 carrying the CURRENT snapshot, not an empty error:
        // the control room re-renders from the body it already has instead of
        // firing a second request, which is what stops a double tap from
        // becoming a double advance.
        app.MapPost("/api/albums/{albumId:guid}/party-game/commands", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] IPartyGameService game,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyGameCommandRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body?.Command is null || body.ExpectedVersion is null) return Results.BadRequest();
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            var result = await game.ExecuteAsync(
                ownerId, albumId, body.Command, body.ExpectedVersion.Value, cancellationToken);

            if (result.Error is PartyGameCommandError error)
            {
                return error switch
                {
                    PartyGameCommandError.NotFound => Results.NotFound(),
                    PartyGameCommandError.UnknownCommand => Results.BadRequest(),
                    _ => Results.Json(new PartyGameCommandRefusalDto(
                        Code(error), result.Snapshot), statusCode: StatusCodes.Status409Conflict),
                };
            }

            // Only the two commands that BOUND a party are audited. A round-by
            // round trail would be a log of an evening, not a security record.
            if (body.Command is PartyGameCommands.Start or PartyGameCommands.Finish)
            {
                await audit.LogAsync(
                    userId: ownerId,
                    action: body.Command == PartyGameCommands.Start
                        ? AuditActions.PartyGameStart : AuditActions.PartyGameFinish,
                    entityType: AuditEntityTypes.PartyAlbum,
                    entityId: albumId,
                    ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    metadata: new { rounds = result.Snapshot!.PlayedRounds },
                    cancellationToken: cancellationToken);
            }

            return Results.Ok(result.Snapshot);
        }).WithName("ExecutePartyGameCommand").RequireAuthorization();

        // Public: what a guest phone or a television may know. Anonymous,
        // token-scoped, re-validated on every request, and rate limited on the
        // same policy as the rest of the public party reads.
        //
        // It resolves an EXISTING guest session but never mints one. A
        // television polls this endpoint too, and minting here would turn every
        // display into a participant — inflating the very count ("8 of 12 have
        // voted") the scene is showing. Refreshing an existing session's
        // presence IS a write, deliberately: it is what keeps a guest holding
        // the voting screen open counted as being in the room.
        app.MapGet("/api/party/{token}/game", async (
            string token, HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyParticipantService participants,
            [FromServices] IPartyGameService game,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await ResolveExistingGuestAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            var snapshot = await game.GetPublicSnapshotAsync(access, participantId, cancellationToken);
            if (snapshot is null) return Results.NotFound();

            return Results.Ok(WithTokenMedia(snapshot, token));
        }).WithName("GetPartyGamePublicSnapshot").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // Joining is the guest saying "I am here". It is what mints the
        // participant cookie, so a phone is counted in the room from the moment
        // it opens the game rather than from the moment it first votes — and it
        // is a POST, so a television polling the read endpoint can never do it
        // by accident.
        app.MapPost("/api/party/{token}/game/join", async (
            string token, HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyParticipantService participants,
            [FromServices] IPartyGameService game,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await PartyEndpoints.ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is null) return Results.NotFound();
            var snapshot = await game.GetPublicSnapshotAsync(access, participantId, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(WithTokenMedia(snapshot, token));
        }).WithName("JoinPartyGame").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // One tap. The body says which round it is answering and what it says;
        // everything else — who is voting, whether voting is open, whether this
        // activity is even voted on — is decided here from persisted state.
        app.MapPost("/api/party/{token}/game/vote", async (
            string token, HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyParticipantService participants,
            [FromServices] IPartyGameService game,
            [FromBody] PartyGameVoteRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest();
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var participantId = await PartyEndpoints.ResolvePartyParticipantAsync(
                httpContext, participants, access.PartyAlbumLinkId, token, cancellationToken);
            if (participantId is null) return Results.NotFound();

            var result = await game.VoteAsync(
                access, participantId.Value, body.RoundId, body.Value, cancellationToken);
            if (result.Error is PartyGameVoteError error)
            {
                return error switch
                {
                    PartyGameVoteError.NotFound => Results.NotFound(),
                    PartyGameVoteError.UnknownValue => Results.BadRequest(),
                    // A refused vote hands back the state it was measured
                    // against, so a phone that fell behind re-renders correctly
                    // instead of showing an error and staying wrong.
                    _ => Results.Json(new PartyGameVoteRefusalDto(
                        VoteCode(error),
                        result.Snapshot is null ? null : WithTokenMedia(result.Snapshot, token)),
                        statusCode: StatusCodes.Status409Conflict),
                };
            }
            return Results.Ok(WithTokenMedia(result.Snapshot!, token));
        }).WithName("SubmitPartyGameVote").RequireRateLimiting(PartyMessageRateLimitPolicy);

        return app;
    }

    /// The service returns a token-less media sentinel so it never has to know
    /// which token asked; the addressable URL is built here, against the
    /// caller's own token, exactly as the guest challenge list does it.
    private static PartyGamePublicSnapshotDto WithTokenMedia(
        PartyGamePublicSnapshotDto snapshot, string token)
    {
        if (snapshot.Challenge?.MediaUrl is null) return snapshot;
        var enc = Uri.EscapeDataString(token);
        return snapshot with
        {
            Challenge = snapshot.Challenge with
            {
                MediaUrl = $"/api/party/{enc}/challenges/{snapshot.Challenge.Id}/media",
            },
        };
    }

    private static async Task<Guid?> ResolveExistingGuestAsync(
        HttpContext context, IPartyParticipantService participants,
        Guid? partyAlbumLinkId, CancellationToken cancellationToken)
    {
        if (partyAlbumLinkId is not Guid linkId) return null;
        return context.Request.Cookies.TryGetValue(PartyEndpoints.PartyParticipantCookieName, out var raw)
            ? await participants.ResolveAsync(linkId, raw, cancellationToken)
            : null;
    }

    private static string VoteCode(PartyGameVoteError error) => error switch
    {
        PartyGameVoteError.VotingClosed => "voting_closed",
        PartyGameVoteError.StaleRound => "stale_round",
        _ => "conflict",
    };

    private static string Code(PartyGameCommandError error) => error switch
    {
        PartyGameCommandError.GameDisabled => "game_disabled",
        PartyGameCommandError.VersionConflict => "version_conflict",
        PartyGameCommandError.IllegalTransition => "illegal_transition",
        PartyGameCommandError.NoChallenges => "no_challenges",
        _ => "conflict",
    };

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

/// <summary>
/// A refused command. The code is a machine value the client maps to its own
/// localized copy; the snapshot is the state the refusal was measured against,
/// so the caller ends the request correct rather than merely told off.
/// </summary>
public sealed record PartyGameCommandRefusalDto(string Code, PartyGameSnapshotDto? Snapshot);

/// A refused vote, with the state it was measured against.
public sealed record PartyGameVoteRefusalDto(string Code, PartyGamePublicSnapshotDto? Snapshot);

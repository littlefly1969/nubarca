using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;
using NubArca.Api.Access;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Party Game runtime surface: one snapshot the owner reads, one command the
/// owner sends, and one snapshot every display reads.
///
/// There is no realtime transport here and that is deliberate. NubArca has no
/// SignalR, WebSocket or SSE anywhere in application code — the paired
/// television, the TV browser, the pairing screen and the party page all poll a
/// snapshot — so the game polls the same way. Every successful poll IS the
/// current truth and is consumed as such; a client recovers from a refresh, a
/// backgrounded tab or a lost network by reading it again. That is the whole
/// reconnection story, and it needs no reconnection code.
///
/// <para><c>version</c> is not a change feed. It is the owner's
/// optimistic-concurrency token, and it moves when an owner command moves the
/// game — never because somebody voted, which would make the host's next
/// command fail as stale. A client that skipped a response because the number
/// had not changed would sit on a stale vote count.</para>
/// </summary>
public static class PartyGameEndpoints
{
    // The live game has its own two policies, partitioned by the guest rather
    // than by the address. The generic party buckets are per-IP, and a party is
    // one Wi-Fi network: on those, the room's own size is what breaks it. See
    // PartyGameRateLimits.
    private const string ReadPolicy = PartyGameRateLimits.ReadPolicy;
    private const string VotePolicy = PartyGameRateLimits.VotePolicy;

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
        }).WithName("GetPartyGameSnapshot").RequirePartyGames();

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
            var ownerId = httpContext.GetCurrentUserId()!.Value;
            return await ExecuteAsync(
                game, audit, ownerId, ownerId, albumId, body,
                httpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        }).WithName("ExecutePartyGameCommand").RequirePartyGames();

        // Owner: one edit to the PLAN — move a remaining activity, or decide it
        // is not being played tonight.
        //
        // A sibling of the command route rather than part of it: a command moves
        // the game's PHASE and a plan edit moves the order it will walk, and
        // collapsing the two into one verb would give the control room a
        // vocabulary in which "next" and "third from now" are the same kind of
        // word. It quotes a version and refuses like every other owner write.
        app.MapPost("/api/albums/{albumId:guid}/party-game/plan", async (
            Guid albumId, HttpContext httpContext,
            [FromServices] IPartyGameService game,
            [FromBody] PartyGamePlanRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            return await PlanAsync(
                game, httpContext.GetCurrentUserId()!.Value, albumId, body, cancellationToken);
        }).WithName("PlanPartyGame").RequirePartyGames();

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
            [FromQuery] int? display,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            var participantId = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            // A caller SAYS it is a television; the server does not guess from
            // the absence of a guest cookie, which a guest who has not joined
            // would also satisfy. Saying so grants nothing — it only lets the
            // control room answer "is a screen showing this" honestly.
            var snapshot = await game.GetPublicSnapshotAsync(
                access, participantId, display == 1, cancellationToken);
            if (snapshot is null) return Results.NotFound();

            return Results.Ok(WithTokenMedia(snapshot, token));
        }).WithName("GetPartyGamePublicSnapshot").RequireRateLimiting(ReadPolicy);

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
            if (access is null || !access.Capabilities.Games) return Results.NotFound();
            var participantId = await PartyGuestSession.ResolveOrCreateAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            if (participantId is null) return Results.NotFound();
            var snapshot = await game.GetPublicSnapshotAsync(access, participantId, false, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(WithTokenMedia(snapshot, token));
        }).WithName("JoinPartyGame").RequireRateLimiting(ReadPolicy);

        // One preference, added or removed. BEFORE the match, never during it.
        //
        // It is a different endpoint from the vote for the same reason it is a
        // different feature: this one names an ACTIVITY and says "I would like
        // to see this", and the vote names a ROUND and says "they did it". They
        // share the anonymous participant and nothing else — no budget, no
        // table, no phase and no consequence — and one endpoint taking both
        // would be the first step back towards them feeding each other.
        //
        // Identity is resolved, never minted, exactly as the vote does it.
        app.MapPost("/api/party/{token}/game/preferences", async (
            string token, HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyParticipantService participants,
            [FromServices] IPartyGameService game,
            [FromBody] PartyGamePreferenceRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body?.ChallengeId is null || body.Selected is null) return Results.BadRequest();
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null || !access.Capabilities.Games) return Results.NotFound();

            var participantId = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);
            var result = await game.SetPreferenceAsync(
                access, participantId, body.ChallengeId, body.Selected.Value, cancellationToken);
            if (result.Error is PartyGamePreferenceError error)
            {
                return error switch
                {
                    PartyGamePreferenceError.NotFound => Results.NotFound(),
                    _ => Results.Json(new PartyGamePreferenceRefusalDto(
                            PreferenceCode(error),
                            result.Preferences is null
                                ? null : WithTokenPreferenceMedia(result.Preferences, token)),
                        statusCode: StatusCodes.Status409Conflict),
                };
            }
            return Results.Ok(WithTokenPreferenceMedia(result.Preferences!, token));
        }).WithName("SetPartyGamePreference").RequireRateLimiting(VotePolicy);

        // One tap. The body says which round it is answering and what it says;
        // everything else — who is voting, whether voting is open, whether this
        // activity is even voted on — is decided here from persisted state.
        //
        // JOIN MINTS IDENTITY. VOTE NEVER DOES. This endpoint resolves an
        // EXISTING participant and refuses when there is none: presenting a
        // cookie is a claim, and a claim the server never issued must not become
        // a vote. Minting here — which it used to — meant a fresh cookie was a
        // fresh voter, so the right to change a party's result was available to
        // anyone who could set a header.
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
            if (access is null || !access.Capabilities.Games) return Results.NotFound();

            // The same resolve-only path the snapshot read uses, scoped to THIS
            // link: a session minted at another party hashes fine and matches no
            // row here. Nothing is created, and no cookie is issued.
            var participantId = await PartyGuestSession.ResolveAsync(
                httpContext, participants, access.PartyAlbumLinkId, cancellationToken);

            var result = await game.VoteAsync(
                access, participantId, body.RoundId, body.Value, cancellationToken);
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
        }).WithName("SubmitPartyGameVote").RequireRateLimiting(VotePolicy);

        return app;
    }

    /// The service returns a token-less media sentinel so it never has to know
    /// which token asked; the addressable URL is built here, against the
    /// caller's own token, exactly as the guest challenge list does it.
    private static PartyGamePublicSnapshotDto WithTokenMedia(
        PartyGamePublicSnapshotDto snapshot, string token)
    {
        var addressed = snapshot.Preferences is null
            ? snapshot
            : snapshot with { Preferences = WithTokenPreferenceMedia(snapshot.Preferences, token) };
        if (addressed.Challenge?.MediaUrl is null) return addressed;
        var enc = Uri.EscapeDataString(token);
        return addressed with
        {
            Challenge = addressed.Challenge with
            {
                MediaUrl = $"/api/party/{enc}/challenges/{addressed.Challenge.Id}/media",
            },
        };
    }

    /// The same rewrite for the pre-game list: the service never learns which
    /// token asked, and the address is built here against the caller's own.
    private static PartyGamePreferencesDto WithTokenPreferenceMedia(
        PartyGamePreferencesDto preferences, string token)
    {
        if (preferences.Items.All(x => x.MediaUrl is null)) return preferences;
        var enc = Uri.EscapeDataString(token);
        return preferences with
        {
            Items = preferences.Items
                .Select(x => x.MediaUrl is null
                    ? x
                    : x with { MediaUrl = $"/api/party/{enc}/challenges/{x.Id}/media" })
                .ToList(),
        };
    }

    /// The audit action for a command that bounds a game, or null for the ones
    /// that merely move it along inside one.
    private static string? AuditedCommand(string command) => command switch
    {
        PartyGameCommands.Start => AuditActions.PartyGameStart,
        PartyGameCommands.Finish => AuditActions.PartyGameFinish,
        PartyGameCommands.RestartGame => AuditActions.PartyGameRestart,
        _ => null,
    };

    private static string VoteCode(PartyGameVoteError error) => error switch
    {
        PartyGameVoteError.VotingClosed => "voting_closed",
        PartyGameVoteError.StaleRound => "stale_round",
        PartyGameVoteError.NotJoined => "not_joined",
        _ => "conflict",
    };

    private static string Code(PartyGameCommandError error) => error switch
    {
        PartyGameCommandError.GameDisabled => "game_disabled",
        PartyGameCommandError.VersionConflict => "version_conflict",
        PartyGameCommandError.IllegalTransition => "illegal_transition",
        PartyGameCommandError.NoChallenges => "no_challenges",
        PartyGameCommandError.InvalidPlan => "invalid_plan",
        _ => "conflict",
    };

    private static string PreferenceCode(PartyGamePreferenceError error) => error switch
    {
        PartyGamePreferenceError.Closed => "preferences_closed",
        PartyGamePreferenceError.NotJoined => "not_joined",
        PartyGamePreferenceError.UnknownChallenge => "unknown_challenge",
        PartyGamePreferenceError.LimitReached => "limit_reached",
        _ => "conflict",
    };

    /// <summary>
    /// Running the hosted game, shared with the Party Crew façade.
    ///
    /// <para>The DIRECTOR is the role this exists for: someone standing by the
    /// screen with the host's phone nowhere near them. <c>ownerUserId</c> is
    /// still whose album it is — a collaborator owns nothing — while
    /// <c>actor</c> is who pressed the button, which is what the audit records.
    /// </para>
    /// </summary>
    internal static async Task<IResult> ExecuteAsync(
        IPartyGameService game,
        IAuditLogger audit,
        Guid ownerUserId,
        AuditActor actor,
        Guid albumId,
        PartyGameCommandRequest? body,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (body?.Command is null || body.ExpectedVersion is null) return Results.BadRequest();

        var discarded = body.Command == PartyGameCommands.RestartGame
            ? (await game.GetOwnerSnapshotAsync(ownerUserId, albumId, cancellationToken))?.PlayedRounds ?? 0
            : 0;
        var result = await game.ExecuteAsync(
            ownerUserId, albumId, body.Command, body.ExpectedVersion.Value, cancellationToken);

        if (result.Error is PartyGameCommandError error) return Refusal(error, result);

        if (AuditedCommand(body.Command) is string auditedAction)
        {
            await audit.LogAsync(
                actor: actor,
                action: auditedAction,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: albumId,
                ipAddress: ip,
                metadata: new
                {
                    rounds = body.Command == PartyGameCommands.RestartGame
                        ? discarded : result.Snapshot!.PlayedRounds,
                },
                cancellationToken: cancellationToken);
        }

        return Results.Ok(result.Snapshot);
    }

    /// <summary>Queueing what comes next. Shared with the Party Crew façade; nothing here is audited.</summary>
    internal static async Task<IResult> PlanAsync(
        IPartyGameService game,
        Guid ownerUserId,
        Guid albumId,
        PartyGamePlanRequest? body,
        CancellationToken cancellationToken)
    {
        if (body?.Action is null || body.ChallengeId is null || body.ExpectedVersion is null)
            return Results.BadRequest();
        var result = await game.PlanAsync(ownerUserId, albumId, body, cancellationToken);
        return result.Error is PartyGameCommandError error
            ? Refusal(error, result)
            : Results.Ok(result.Snapshot);
    }

    private static IResult Refusal(PartyGameCommandError error, PartyGameCommandResult result) => error switch
    {
        PartyGameCommandError.NotFound => Results.NotFound(),
        PartyGameCommandError.UnknownCommand => Results.BadRequest(),
        _ => Results.Json(new PartyGameCommandRefusalDto(
            Code(error), result.Snapshot), statusCode: StatusCodes.Status409Conflict),
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

/// A refused preference, with the surface it was measured against — so a phone
/// that tapped one too many, or tapped after the host started, re-renders
/// correctly instead of showing an error and staying wrong.
public sealed record PartyGamePreferenceRefusalDto(string Code, PartyGamePreferencesDto? Preferences);

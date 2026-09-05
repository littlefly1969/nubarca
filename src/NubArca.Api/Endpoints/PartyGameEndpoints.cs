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
        app.MapGet("/api/party/{token}/game", async (
            string token, HttpContext httpContext,
            [FromServices] IPartyLinkService party,
            [FromServices] IPartyGameService game,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await party.ResolvePublicAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var snapshot = await game.GetPublicSnapshotAsync(access, cancellationToken);
            if (snapshot is null) return Results.NotFound();

            // The service returns a token-less sentinel so it never has to know
            // which token asked; the addressable URL is built here, against the
            // caller's own token, exactly as the guest challenge list does it.
            if (snapshot.Challenge?.MediaUrl is not null)
            {
                var enc = Uri.EscapeDataString(token);
                snapshot = snapshot with
                {
                    Challenge = snapshot.Challenge with
                    {
                        MediaUrl = $"/api/party/{enc}/challenges/{snapshot.Challenge.Id}/media",
                    },
                };
            }
            return Results.Ok(snapshot);
        }).WithName("GetPartyGamePublicSnapshot").RequireRateLimiting(PartyPublicRateLimitPolicy);

        return app;
    }

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

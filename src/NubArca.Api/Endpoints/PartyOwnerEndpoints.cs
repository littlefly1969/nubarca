using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The Party aggregate root's own owner surface: reading a party, and moving it
/// through its lifecycle.
///
/// <para>Deliberately four routes and no more. The Party UI is not built in
/// this slice — the owner still reaches Party through Album settings, which is
/// where the party id in <c>AlbumPartyStatusDto</c> comes from — so what lives
/// here is exactly what makes the lifecycle SERVER-authoritative rather than a
/// column a client could set: an action per legal transition, a version to
/// state, and one place that refuses everything else.</para>
///
/// <para>Party status is not an access gate. Nothing here opens or closes a
/// guest capability; that stays the link's <c>Enabled</c>/<c>RevokedAt</c> and
/// the owner's <c>party.access</c> permission, so there is one answer to "why
/// is this party closed" rather than two.</para>
/// </summary>
public static class PartyOwnerEndpoints
{
    public sealed record PartyTransitionRequest(int Version);

    public static IEndpointRouteBuilder MapPartyOwnerEndpoints(this IEndpointRouteBuilder app)
    {
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

        // One route per ACTION rather than a status the caller chooses: `publish`
        // and `start-live` are different decisions that happen to leave the party
        // in adjacent states, and a PATCH taking a status would let a client
        // invent a fifth one.
        Map(app, "publish", PartyLifecycleAction.Publish, AuditActions.PartyPublish);
        Map(app, "start-live", PartyLifecycleAction.StartLive, AuditActions.PartyStartLive);
        Map(app, "end-live", PartyLifecycleAction.EndLive, AuditActions.PartyEndLive);

        return app;
    }

    private static void Map(
        IEndpointRouteBuilder app, string segment, PartyLifecycleAction action, string auditAction)
    {
        app.MapPost($"/api/parties/{{partyId:guid}}/{segment}", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyService parties,
            [FromServices] IAuditLogger audit,
            [FromBody] PartyTransitionRequest? body,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing request body." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await parties.TransitionAsync(
                ownerUserId, partyId, action, body.Version, cancellationToken);

            switch (result.Outcome)
            {
                case PartyTransitionOutcome.Ok:
                    await audit.LogAsync(
                        ownerUserId, auditAction, AuditEntityTypes.Party, partyId,
                        httpContext.Connection.RemoteIpAddress?.ToString(),
                        new { status = result.Party!.Status }, cancellationToken);
                    return Results.Ok(result.Party);

                case PartyTransitionOutcome.InvalidTransition:
                    // The party's CURRENT state travels with the refusal, so a
                    // client can say what actually happened rather than guess.
                    return Results.BadRequest(new
                    {
                        error = "invalid_transition",
                        party = result.Party,
                    });

                case PartyTransitionOutcome.VersionConflict:
                    return Results.Json(new
                    {
                        error = "version_conflict",
                        party = result.Party,
                    }, statusCode: StatusCodes.Status409Conflict);

                default:
                    return Results.NotFound();
            }
        }).WithName($"Party{action}").RequirePermission(Permissions.PartyAccess);
    }
}

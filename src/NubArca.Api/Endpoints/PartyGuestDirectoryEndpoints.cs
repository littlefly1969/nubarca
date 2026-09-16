using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The host's guest console: the directory, one group, the questions.
///
/// <para>Reads only, all owner routes under <c>/api/parties/{partyId}</c>
/// requiring <c>party.access</c>, owner-scoped in every query and
/// <c>no-store</c>. A foreign party or group is the same 404 as a missing one.
/// Nothing here is reachable from the party's QR, a personal invitation, the TV,
/// the game, print or a link preview.</para>
///
/// <para>The search travels in the query string, like every other search in
/// NubArca; the cursor that continues it is sealed and holds only a hash of it.
/// Neither is logged or audited here.</para>
/// </summary>
public static class PartyGuestDirectoryEndpoints
{
    public static IEndpointRouteBuilder MapPartyGuestDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parties/{partyId:guid}/guest-directory", async (
            Guid partyId,
            [FromQuery] string? q,
            [FromQuery] string? state,
            [FromQuery] string? cursor,
            [FromQuery] int? take,
            HttpContext httpContext,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var result = await directory.PageAsync(
                httpContext.GetCurrentUserId()!.Value, partyId,
                new PartyGuestDirectoryQuery(q, state, cursor, take), cancellationToken);
            return result.Outcome switch
            {
                PartyGuestDirectoryOutcome.Ok => Results.Ok(result.Page),
                PartyGuestDirectoryOutcome.NotFound => Results.NotFound(),
                _ => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
            };
        }).WithName("GetPartyGuestDirectory").RequirePermission(Permissions.PartyAccess);

        app.MapGet("/api/parties/{partyId:guid}/invitation-groups/{groupId:guid}", async (
            Guid partyId,
            Guid groupId,
            HttpContext httpContext,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var detail = await directory.GroupAsync(
                httpContext.GetCurrentUserId()!.Value, partyId, groupId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetPartyInvitationGroup").RequirePermission(Permissions.PartyAccess);

        app.MapGet("/api/parties/{partyId:guid}/rsvp-questions", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var questions = await directory.QuestionsAsync(
                httpContext.GetCurrentUserId()!.Value, partyId, cancellationToken);
            return questions is null ? Results.NotFound() : Results.Ok(new { questions });
        }).WithName("GetPartyRsvpQuestions").RequirePermission(Permissions.PartyAccess);

        return app;
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

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
/// <para>THE SEARCH TRAVELS IN A POST BODY, which is why reading the directory
/// is the one read here that is not a GET. A host searching their own guest
/// list types a name, a surname, an address or a phone number — so the needle
/// IS personal data about a third party, and a query string is the one part of
/// a request that leaks by default: it is written to the browser's address bar
/// and history, to a Referer sent to whatever the host opens next, and to the
/// access log of every proxy in between. A body is none of those places. The
/// cursor that continues the search is sealed and carries only a hash of it.</para>
///
/// <para>Nothing here logs or audits the needle, the body, or any name, address
/// or number read back — a search is not an event in the party's history, and a
/// log line would put back exactly what the POST took out.</para>
///
/// <para>Being an unsafe method on <c>/api</c>, the query passes through the
/// same-origin Origin/Referer check in <see cref="Security.CsrfOriginValidation"/>
/// like every other write. It is deliberately NOT exempted: the cookie is
/// ambient, so a cross-site page must not be able to read a host's guest list.</para>
/// </summary>
public static class PartyGuestDirectoryEndpoints
{
    public static IEndpointRouteBuilder MapPartyGuestDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/parties/{partyId:guid}/guest-directory/query", async (
            Guid partyId,
            PartyGuestDirectoryQuery? query,
            HttpContext httpContext,
            [FromServices] IPartyGuestDirectoryService directory,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            // An absent body is the first page of everything, which is what a
            // console asks for before the host has typed or filtered anything.
            var result = await directory.PageAsync(
                httpContext.GetCurrentUserId()!.Value, partyId,
                query ?? new PartyGuestDirectoryQuery(null, null, null, null), cancellationToken);
            return result.Outcome switch
            {
                PartyGuestDirectoryOutcome.Ok => Results.Ok(result.Page),
                PartyGuestDirectoryOutcome.NotFound => Results.NotFound(),
                _ => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
            };
        }).WithName("QueryPartyGuestDirectory").RequirePermission(Permissions.PartyAccess);

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

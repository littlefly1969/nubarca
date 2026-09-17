using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The HOST's side of Party Crew: who is helping, as what, and on which
/// devices.
///
/// <para>Owner-only, every route, and owner-scoped in every query — a party id
/// that is not this user's party answers not-found rather than forbidden.
/// Managing collaborators is deliberately NOT delegable: a co-organizer cannot
/// add another co-organizer, because authority that can extend itself is not
/// bounded by anything.</para>
///
/// <para><b>Collaborator addresses are owner-private.</b> They appear in this
/// family and nowhere else in the product: not in a guest API, not on the TV,
/// not on the public party page, not in an audit entry, not in a log. The
/// pairing surface shows a masked form so the person can recognise their own
/// inbox, and that is the only other place the address is referred to at
/// all.</para>
///
/// <para><b>A link is returned exactly once</b>, by the mutation that minted
/// it, and never by a read — a list endpoint that re-issued links would make
/// every owner session a way to re-open everyone's access. Asking for a new one
/// is an explicit action that invalidates the old.</para>
/// </summary>
public static class PartyCrewOwnerEndpoints
{
    public static IEndpointRouteBuilder MapPartyCrewOwnerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parties/{partyId:guid}/crew", async (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            CancellationToken ct) =>
        {
            NoStore(http);
            var overview = await crew.OverviewAsync(Owner(http), partyId, ct);
            return overview is null ? Results.NotFound() : Results.Ok(overview);
        }).WithName("GetPartyCrew").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/parties/{partyId:guid}/crew", async (
            Guid partyId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            [FromBody] PartyCollaboratorWriteDto? body,
            CancellationToken ct) =>
        {
            NoStore(http);
            if (body is null) return Results.BadRequest(new { error = "invalid" });
            var result = await crew.CreateAsync(Owner(http), partyId, body, Ip(http), ct);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(result.Error!.Value);
        }).WithName("CreatePartyCollaborator").RequirePermission(Permissions.PartyAccess);

        app.MapPut("/api/parties/{partyId:guid}/crew/{collaboratorId:guid}", async (
            Guid partyId,
            Guid collaboratorId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            [FromBody] PartyCollaboratorUpdateDto? body,
            CancellationToken ct) =>
        {
            NoStore(http);
            if (body is null) return Results.BadRequest(new { error = "invalid" });
            var result = await crew.UpdateAsync(Owner(http), partyId, collaboratorId, body, Ip(http), ct);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(result.Error!.Value);
        }).WithName("UpdatePartyCollaborator").RequirePermission(Permissions.PartyAccess);

        // A NEW link, which is also how the old one stops working.
        app.MapPost("/api/parties/{partyId:guid}/crew/{collaboratorId:guid}/invite", async (
            Guid partyId,
            Guid collaboratorId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            CancellationToken ct) =>
        {
            NoStore(http);
            var result = await crew.RotateInviteAsync(Owner(http), partyId, collaboratorId, Ip(http), ct);
            return result.Succeeded ? Results.Ok(result.Value) : Problem(result.Error!.Value);
        }).WithName("RotatePartyCollaboratorInvite").RequirePermission(Permissions.PartyAccess);

        app.MapDelete("/api/parties/{partyId:guid}/crew/{collaboratorId:guid}", async (
            Guid partyId,
            Guid collaboratorId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            CancellationToken ct) =>
        {
            NoStore(http);
            var error = await crew.RevokeAsync(Owner(http), partyId, collaboratorId, Ip(http), ct);
            return error is null ? Results.NoContent() : Problem(error.Value);
        }).WithName("RevokePartyCollaborator").RequirePermission(Permissions.PartyAccess);

        // One device, not the person. "They lost their phone" is a different
        // decision from "they are no longer helping", and the product must not
        // make the host pick the bigger one to express the smaller.
        app.MapDelete("/api/parties/{partyId:guid}/crew/{collaboratorId:guid}/devices/{grantId:guid}", async (
            Guid partyId,
            Guid collaboratorId,
            Guid grantId,
            HttpContext http,
            [FromServices] IPartyCrewService crew,
            CancellationToken ct) =>
        {
            NoStore(http);
            var error = await crew.RevokeDeviceAsync(Owner(http), partyId, collaboratorId, grantId, Ip(http), ct);
            return error is null ? Results.NoContent() : Problem(error.Value);
        }).WithName("RevokePartyCollaboratorDevice").RequirePermission(Permissions.PartyAccess);

        return app;
    }

    private static IResult Problem(PartyCrewError error) => error switch
    {
        PartyCrewError.InvalidName => Results.BadRequest(new { error = "invalid_name" }),
        PartyCrewError.InvalidEmail => Results.BadRequest(new { error = "invalid_email" }),
        PartyCrewError.InvalidRole => Results.BadRequest(new { error = "invalid_role" }),
        PartyCrewError.RoleNotAssignable => Results.BadRequest(new { error = "role_not_assignable" }),
        PartyCrewError.EmailInUse => Results.Conflict(new { error = "email_in_use" }),
        PartyCrewError.VersionConflict => Results.Conflict(new { error = "version_conflict" }),
        PartyCrewError.MailUnavailable => Results.BadRequest(new { error = "mail_unavailable" }),
        _ => Results.NotFound(),
    };

    private static Guid Owner(HttpContext http) => http.GetCurrentUserId()!.Value;

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    // Addresses, device lists and a freshly minted link. Never cached.
    private static void NoStore(HttpContext http) =>
        http.Response.Headers.CacheControl = "no-store";
}

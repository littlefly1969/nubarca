using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The HOST's print settings for one party.
///
/// Deliberately a different file from PartyPrintEndpoints, which is the guest's
/// surface: these two have opposite audiences and opposite rules. Everything
/// here requires the owner's own session, is scoped to an album they own, and
/// answers a foreign or missing album with the same 404 — a host learns nothing
/// about anyone else's party.
///
/// It is also a SEPARATE route from party-settings, for the same reason the
/// slideshow settings are: saving a print budget must not be able to rotate a
/// token, flip party mode, or change moderation as a side effect.
/// </summary>
public static class PartyPrintOwnerEndpoints
{
    public static void MapPartyPrintOwnerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/{albumId:guid}/party-print-settings", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IPartyPrintProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var profile = await profiles.GetAsync(ownerUserId, albumId, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }).WithName("GetPartyPrintSettings").RequireAuthorization();

        app.MapMethods("/api/albums/{albumId:guid}/party-print-settings", ["PATCH"], async (
            Guid albumId,
            HttpContext httpContext,
            [FromBody] PartyPrintProfileRequest? body,
            [FromServices] IPartyPrintProfileService profiles,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (body is null) return Results.BadRequest(new { error = "invalid" });

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await profiles.SaveAsync(ownerUserId, albumId, body, cancellationToken);
            if (result.Error == "not_found") return Results.NotFound();
            if (result.Error is not null) return Results.BadRequest(new { error = result.Error });

            var saved = result.Profile!;
            // Printing spends the host's own consumables and puts a line of
            // their text on paper, so the switch, the budgets and the chosen
            // printer are all worth a trail. The footer TEXT is not recorded:
            // what was configured is a security question, what it said is not.
            await audit.LogAsync(
                ownerUserId, AuditActions.PartyPrintConfigure, AuditEntityTypes.PartyAlbum,
                albumId, httpContext.Connection.RemoteIpAddress?.ToString(),
                new
                {
                    albumId,
                    enabled = saved.Enabled,
                    printStationId = saved.PrintStationId,
                    printerDeviceId = saved.PrinterDeviceId,
                    photoEnabled = saved.Photo.Enabled,
                    photoMaxPrints = saved.Photo.MaxPrints,
                    stripEnabled = saved.Strip.Enabled,
                    stripMaxPrints = saved.Strip.MaxPrints,
                    hasFooterText = saved.FooterText is not null,
                },
                cancellationToken);

            return Results.Ok(saved);
        }).WithName("SetPartyPrintSettings").RequireAuthorization();
    }
}

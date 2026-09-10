using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Party;
using QRCoder;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The DISPLAY surface: what a paired television may read about the party it
/// has been assigned to, and nothing else.
///
/// <para>It is a separate route family from <c>/api/party/…</c> on purpose, and
/// the separation is structural rather than decorative. The guest cookie is
/// path-scoped to <c>/api/party</c>, so a browser standing in front of these
/// routes never sends it and these routes can never set it: a display cannot
/// acquire a guest identity even by accident. Nothing here accepts a party
/// token, and nothing here mints a participant.</para>
///
/// <para>Authentication is <see cref="PartyDisplayService.GrantHeader"/> — a
/// header, never a query parameter, because a URL reaches access logs, browser
/// history and referrers and a credential must not. The grant is resolved on
/// every request against the whole chain (session live, still assigned, still
/// THIS party, party still resolvable), so an owner changing the assignment
/// closes these routes within one poll rather than at expiry.</para>
/// </summary>
public static class PartyDisplayEndpoints
{
    public static IEndpointRouteBuilder MapPartyDisplayEndpoints(this IEndpointRouteBuilder app)
    {
        // The party as a display may see it.
        //
        // It is the SAME snapshot a guest television gets, from the same
        // service, with participantId null and isDisplay true — so the secrecy
        // table (participation always, the split only when the phase allows)
        // is not reimplemented here and cannot drift. Reusing it is the point:
        // a second projection would be a second place to get "who may know the
        // result" wrong.
        app.MapGet("/api/party-display/game", async (
            HttpContext httpContext,
            [FromServices] IPartyDisplayService display,
            [FromServices] IPartyGameService game,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await display.ResolveAsync(Grant(httpContext), cancellationToken);
            if (access is null) return Results.Unauthorized();

            // isDisplay: true stamps the party's display heartbeat, which is
            // what lets the control room say honestly that a screen is showing
            // the game. participantId: null because a display is not a guest —
            // one that joined would inflate the very count it is showing.
            var snapshot = await game.GetPublicSnapshotAsync(
                access, participantId: null, isDisplay: true, cancellationToken);
            if (snapshot is null) return Results.NotFound();

            // The activity's media sentinel is rewritten onto the DISPLAY
            // route, never onto a party token — the display has none.
            return Results.Ok(WithDisplayMedia(snapshot));
        }).WithName("GetPartyDisplayGame");

        // The activity's photograph. A separate fetch rather than a URL the
        // browser can load on its own, because an <img> cannot carry a header
        // and the alternative — a credential in the query string — is the thing
        // this whole surface exists to avoid.
        app.MapGet("/api/party-display/challenges/{challengeId:guid}/media", async (
            Guid challengeId,
            HttpContext httpContext,
            [FromServices] IPartyDisplayService display,
            [FromServices] AppDbContext db,
            [FromServices] IPartyMediaService partyMedia,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            var access = await display.ResolveAsync(Grant(httpContext), cancellationToken);
            if (access is null) return Results.Unauthorized();
            if (!access.Capabilities.Games) return Results.NotFound();

            var fileId = await db.PartyChallenges.AsNoTracking()
                .Where(x => x.Id == challengeId && x.AlbumId == access.MainAlbumId && x.IsEnabled)
                .Select(x => x.MediaFileItemId).FirstOrDefaultAsync(cancellationToken);
            if (fileId is not Guid id) return Results.NotFound();

            // The same derived, metadata-stripped preview every other party
            // surface serves. Never an original.
            return await PartyEndpoints.ServeMediaCoreAsync(
                access.OwnerUserId, access.MainAlbumId, id, "preview", httpContext,
                partyMedia, thumbnails, stripper, cancellationToken);
        }).WithName("GetPartyDisplayChallengeMedia");

        // The way in, as PIXELS.
        //
        // The lobby has to show guests a code they can scan, and that code
        // necessarily encodes the party's own join URL. But the display must
        // not HOLD that URL: a token in the page is a guest capability sitting
        // on a television. So the server encodes it and returns an image. The
        // room can scan it; the television cannot use it.
        app.MapGet("/api/party-display/join-qr", async (
            HttpContext httpContext,
            [FromServices] IPartyDisplayService display,
            [FromServices] IPartyLinkService links,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await display.ResolveAsync(Grant(httpContext), cancellationToken);
            if (access is null) return Results.Unauthorized();

            // Derived here and consumed here. It is never serialized into a
            // response body, a log line or a DTO.
            var joinUrl = PartyLinkService.BuildGameUrl(links.DeriveViewToken(access.PartyAlbumLinkId));
            var origin = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode($"{origin}{joinUrl}", QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(
                pixelsPerModule: 8,
                darkColorHex: "#0b1220",
                lightColorHex: "#ffffff",
                drawQuietZones: true);
            return Results.Text(svg, "image/svg+xml");
        }).WithName("GetPartyDisplayJoinQr");

        return app;
    }

    /// The activity's media, addressed on the display surface. The service
    /// hands back a token-less sentinel exactly as it does for a guest; only
    /// the address it is rewritten to differs.
    private static PartyGamePublicSnapshotDto WithDisplayMedia(PartyGamePublicSnapshotDto snapshot)
    {
        if (snapshot.Challenge?.MediaUrl is null) return snapshot;
        return snapshot with
        {
            Challenge = snapshot.Challenge with
            {
                MediaUrl = $"/api/party-display/challenges/{snapshot.Challenge.Id}/media",
            },
        };
    }

    private static string? Grant(HttpContext context) =>
        context.Request.Headers[PartyDisplayService.GrantHeader].ToString() is { Length: > 0 } value
            ? value
            : null;

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

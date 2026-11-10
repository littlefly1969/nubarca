using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

/// <summary>
/// An album's own logo for printed sheets: the owner sets it, and each print
/// surface shows a small derived preview of it — the owner's studio, a party's
/// guests through their print token, a verified share visitor through theirs.
/// No surface ever serves the logo's original, and the public ones never name
/// its file.
/// </summary>
public static class AlbumPrintLogoEndpoints
{
    private const string PartyPublicRateLimitPolicy = "party-public";
    private const string AlbumSharePublicRateLimitPolicy = PartyEndpoints.PublicRateLimitPolicy;

    public static void MapAlbumPrintLogoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/{id:guid}/print-logo", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumPrintLogoService logos,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var settings = await logos.GetAsync(id, ownerUserId, cancellationToken);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        }).WithName("GetAlbumPrintLogo").RequireAuthorization();

        app.MapPut("/api/albums/{id:guid}/print-logo", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumPrintLogoService logos,
            [FromServices] IAuditLogger audit,
            [FromBody] SetAlbumPrintLogoRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (body is null) return Results.BadRequest(new { error = "invalid" });
            var result = await logos.SetAsync(id, ownerUserId, body.FileItemId, body.Optional, cancellationToken);
            if (result.Error == AlbumPrintLogoResult.NotFound) return Results.NotFound();
            if (result.Error is not null) return Results.BadRequest(new { error = result.Error });

            await audit.LogAsync(ownerUserId, AuditActions.AlbumUpdate, AuditEntityTypes.Album,
                id, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { printLogo = body.FileItemId is not null, printLogoOptional = body.Optional },
                cancellationToken);
            return Results.Ok(result.Settings);
        }).WithName("SetAlbumPrintLogo").RequireAuthorization();

        // The owner's own preview: the album's logo as it is set now.
        app.MapGet("/api/albums/{id:guid}/print-logo/image", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumPrintLogoService logos,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var logo = await logos.UsableLogoAsync(id, ownerUserId, cancellationToken);
            return await ServeAsync(httpContext, logos, ownerUserId, logo, cancellationToken);
        }).WithName("GetAlbumPrintLogoImage").RequireAuthorization();

        // A party guest's preview, through the print token: the same logo the
        // sheet will carry, offered only while the studio itself is open.
        app.MapGet("/api/party/{printToken}/print/logo", async (
            string printToken,
            HttpContext httpContext,
            [FromServices] IPartyPrintAccessResolver resolver,
            [FromServices] IAlbumPrintLogoService logos,
            CancellationToken cancellationToken) =>
        {
            var access = await resolver.ResolveAsync(printToken, cancellationToken);
            if (access is null) return Results.NotFound();
            return await ServeAsync(httpContext, logos, access.OwnerUserId, access.LogoFileItemId, cancellationToken);
        }).WithName("GetPartyPrintLogo").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // A verified share visitor's preview, under the same grant that lets them print.
        app.MapGet("/api/album-share/{token}/print/logo", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] IAlbumPrintLogoService logos,
            CancellationToken cancellationToken) =>
        {
            var resolved = await shares.ResolveAsync(token, AlbumShareLinkEndpoints.Device(httpContext, token), cancellationToken);
            if (resolved.Access is not { CanPrint: true } access) return Results.NotFound();
            var logo = await logos.UsableLogoAsync(access.AlbumId, access.OwnerUserId, cancellationToken);
            return await ServeAsync(httpContext, logos, access.OwnerUserId, logo, cancellationToken);
        }).WithName("GetAlbumSharePrintLogo").RequireRateLimiting(AlbumSharePublicRateLimitPolicy);
    }

    private static async Task<IResult> ServeAsync(
        HttpContext httpContext, IAlbumPrintLogoService logos, Guid ownerUserId, Guid? logo,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (logo is not Guid id) return Results.NotFound();
        var png = await logos.PreviewAsync(ownerUserId, id, cancellationToken);
        return png is null ? Results.NotFound() : Results.File(png, "image/png");
    }
}

public sealed record SetAlbumPrintLogoRequest(Guid? FileItemId, bool Optional);

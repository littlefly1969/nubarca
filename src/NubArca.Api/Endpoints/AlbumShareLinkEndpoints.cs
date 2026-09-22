using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

/// <summary>
/// SHARE BY LINK. One album, one address, anybody holding it.
///
/// <para>Two route families, and the split is the whole design:</para>
/// <list type="bullet">
/// <item><c>/api/albums/{id}/share-link…</c> — the owner's, behind
/// authentication, and the only place a raw token is ever produced.</item>
/// <item><c>/api/album-share/{token}…</c> — the public surface, anonymous and
/// rate-limited, where EVERY request resolves the token through the one seam
/// before it does anything at all.</item>
/// </list>
///
/// <para><b>These routes never mention a party.</b> An album with an evening
/// attached has two links that open two different things, and following one has
/// never been a way to reach the other. That is not enforced by a check here —
/// it is true because the tokens are derived under different purpose strings
/// and their digests cannot collide.</para>
///
/// <para><b>There is no delete route.</b> Not hidden, not gated: absent. A
/// visitor may add to the album and take from it, and the one power an owner
/// cannot lend by accident is the power to destroy.</para>
///
/// <para>Public media is <c>no-store</c>, for the reason the member-sharing
/// surface is: a revoke has to take effect immediately, and a response already
/// in somebody's HTTP cache would outlive it.</para>
/// </summary>
public static class AlbumShareLinkEndpoints
{
    /// <summary>The cookie a verified visitor carries. Scoped to the share routes.</summary>
    private const string DeviceCookie = "NubArca.AlbumShare";

    private const string PublicRateLimitPolicy = PartyEndpoints.PublicRateLimitPolicy;

    public static IEndpointRouteBuilder MapAlbumShareLinkEndpoints(this IEndpointRouteBuilder app)
    {
        MapOwner(app);
        MapPublic(app);
        return app;
    }

    // ── Owner ───────────────────────────────────────────────────────────────

    private static void MapOwner(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/{albumId:guid}/share-link", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var link = await shares.GetAsync(ownerUserId, albumId, cancellationToken);
            // No link and a foreign album answer the same: a 404 that says
            // nothing about which album ids exist.
            return link is null ? Results.NotFound() : Results.Ok(link);
        }).WithName("GetAlbumShareLink").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/share-link", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var link = await shares.CreateAsync(ownerUserId, albumId, ownerUserId, cancellationToken);
            if (link is null) return Results.NotFound();
            await audit.LogAsync(
                AuditActor.User(ownerUserId), AuditActions.AlbumShareLinkCreate,
                AuditEntityTypes.Album, albumId, Ip(httpContext), null, cancellationToken);
            return Results.Ok(link);
        }).WithName("CreateAlbumShareLink").RequireAuthorization();

        // ROTATION IS ITS OWN VERB. Creating reuses what is there, because an
        // owner opening the panel twice must not invalidate the address thirty
        // people hold; rotating deliberately does invalidate it, which is why
        // somebody has to ask for it by name.
        app.MapPost("/api/albums/{albumId:guid}/share-link/rotate", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var link = await shares.RotateAsync(ownerUserId, albumId, ownerUserId, cancellationToken);
            if (link is null) return Results.NotFound();
            await audit.LogAsync(
                AuditActor.User(ownerUserId), AuditActions.AlbumShareLinkRotate,
                AuditEntityTypes.Album, albumId, Ip(httpContext), null, cancellationToken);
            return Results.Ok(link);
        }).WithName("RotateAlbumShareLink").RequireAuthorization();

        app.MapPatch("/api/albums/{albumId:guid}/share-link", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromBody] AlbumShareUpdateRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var link = await shares.UpdateAsync(
                ownerUserId, albumId, body ?? new AlbumShareUpdateRequest(), cancellationToken);
            return link is null ? Results.NotFound() : Results.Ok(link);
        }).WithName("UpdateAlbumShareLink").RequireAuthorization();

        app.MapDelete("/api/albums/{albumId:guid}/share-link", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (!await shares.RevokeAsync(ownerUserId, albumId, cancellationToken))
                return Results.NotFound();
            await audit.LogAsync(
                AuditActor.User(ownerUserId), AuditActions.AlbumShareLinkRevoke,
                AuditEntityTypes.Album, albumId, Ip(httpContext), null, cancellationToken);
            return Results.NoContent();
        }).WithName("RevokeAlbumShareLink").RequireAuthorization();

        app.MapPost("/api/albums/{albumId:guid}/share-link/guests", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromBody] AlbumShareGuestRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var guest = await shares.AddGuestAsync(
                ownerUserId, albumId, body ?? new AlbumShareGuestRequest(null, null),
                cancellationToken);
            return guest is null ? Results.BadRequest() : Results.Ok(guest);
        }).WithName("AddAlbumShareGuest").RequireAuthorization();

        app.MapDelete("/api/albums/{albumId:guid}/share-link/guests/{guestId:guid}", async (
            Guid albumId, Guid guestId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return await shares.RemoveGuestAsync(ownerUserId, albumId, guestId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }).WithName("RemoveAlbumShareGuest").RequireAuthorization();
    }

    // ── Public ──────────────────────────────────────────────────────────────

    private static void MapPublic(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/album-share/{token}", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] NubArca.Api.Party.IPartyMediaService media,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var resolved = await shares.ResolveAsync(token, Device(httpContext), cancellationToken);
            if (resolved.Kind == AlbumShareResolution.NeedsSecondFactor)
            {
                // The ONE place this surface says more than "no". The caller is
                // holding a live link and is expected; a 404 would tell them to
                // give up on something that is about to work.
                return Results.Json(
                    new { error = AlbumShareErrors.SecondFactorRequired },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            if (resolved.Access is not { } access) return Results.NotFound();

            var header = await media.GetAlbumAsync(
                access.OwnerUserId, access.AlbumId, cancellationToken);
            if (header is null) return Results.NotFound();

            var enc = Uri.EscapeDataString(token);
            return Results.Ok(new AlbumSharePublicDto(
                header.Name,
                header.CoverFileItemId is Guid cover
                    ? $"/api/album-share/{enc}/media/{cover}/preview"
                    : null,
                header.ItemCount,
                access.UploadEnabled && access.HasUploadRoom,
                access.AllowOriginalDownload,
                access.MaxUploads == 0 ? null : Math.Max(0, access.MaxUploads - access.UploadCount)));
        }).WithName("GetAlbumShare").RequireRateLimiting(PublicRateLimitPolicy);

        app.MapGet("/api/album-share/{token}/items", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] NubArca.Api.Party.IPartyMediaService media,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var access = await GrantAsync(shares, token, httpContext, cancellationToken);
            if (access is null) return Results.NotFound();

            var items = await media.ListItemsAsync(
                access.OwnerUserId, access.AlbumId, cancellationToken);
            if (items is null) return Results.NotFound();

            var enc = Uri.EscapeDataString(token);
            return Results.Ok(new AlbumShareItemsDto(
                // An id, a picture and whether it moves. No file name, no date
                // taken, no hash — the party surface leaks none of those and a
                // share is not a reason to start.
                items.Select(i => new AlbumShareItemDto(
                    i.FileItemId,
                    $"/api/album-share/{enc}/media/{i.FileItemId}/thumbnail",
                    $"/api/album-share/{enc}/media/{i.FileItemId}/preview",
                    $"/api/album-share/{enc}/media/{i.FileItemId}/download",
                    i.Kind == NubArca.Api.Party.PartyMediaKind.Video)).ToList(),
                NextCursor: null));
        }).WithName("GetAlbumShareItems").RequireRateLimiting(PublicRateLimitPolicy);

        foreach (var variant in new[] { "thumbnail", "preview" })
        {
            var v = variant;
            app.MapGet($"/api/album-share/{{token}}/media/{{fileId:guid}}/{v}", async (
                string token, Guid fileId,
                HttpContext httpContext,
                [FromServices] IAlbumShareService shares,
                [FromServices] NubArca.Api.Party.IPartyMediaService media,
                [FromServices] IFileThumbnailService thumbnails,
                [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
                CancellationToken cancellationToken) =>
            {
                NoStore(httpContext);
                var access = await GrantAsync(shares, token, httpContext, cancellationToken);
                if (access is null) return Results.NotFound();
                // The SAME album-scoped authorization and the SAME derivative
                // pipeline the party surface uses. There is one way bytes leave
                // this server, and sharing does not get a second one.
                var kind = await media.GetVisibleMediaKindAsync(
                    access.OwnerUserId, access.AlbumId, fileId, cancellationToken);
                if (kind is null) return Results.NotFound();
                return await PartyEndpoints.ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, fileId, kind.Value, v, httpContext,
                    thumbnails, stripper, cancellationToken);
            }).WithName($"GetAlbumShareMedia{v}").RequireRateLimiting(PublicRateLimitPolicy);
        }

        // THE DOWNLOAD. What a visitor actually came for, and the one route that
        // can hand over an original — only when the owner said so, because an
        // original carries the EXIF the camera wrote and a link is a public
        // share. With the switch off this is the medium rendition, stripped,
        // which is a photograph you can keep and not a map of where it was taken.
        app.MapGet("/api/album-share/{token}/media/{fileId:guid}/download", async (
            string token, Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] NubArca.Api.Party.IPartyMediaService media,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] IFileItemService files,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var access = await GrantAsync(shares, token, httpContext, cancellationToken);
            if (access is null) return Results.NotFound();
            var kind = await media.GetVisibleMediaKindAsync(
                access.OwnerUserId, access.AlbumId, fileId, cancellationToken);
            if (kind is null) return Results.NotFound();

            // ANONYMOUS by construction: nobody signed in, and a link has no
            // person behind it. The row names the file and the album, never a
            // token and never an address.
            await audit.LogAsync(
                AuditActor.Anonymous, AuditActions.AlbumShareLinkDownload,
                AuditEntityTypes.File, fileId, Ip(httpContext),
                new { albumId = access.AlbumId }, cancellationToken);

            if (!access.AllowOriginalDownload)
            {
                return await PartyEndpoints.ServeAuthorizedDerivativeAsync(
                    access.OwnerUserId, fileId, kind.Value, "download", httpContext,
                    thumbnails, stripper, cancellationToken);
            }

            var original = await files.OpenContentAsync(
                fileId, access.OwnerUserId, cancellationToken);
            if (original is null) return Results.NotFound();
            return Results.File(
                original.Content,
                NubArca.Api.Security.SafeContentType.ForServing(original.DetectedContentType),
                original.FileName);
        }).WithName("GetAlbumShareDownload").RequireRateLimiting(PublicRateLimitPolicy);

        app.MapPost("/api/album-share/{token}/upload", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareService shares,
            [FromServices] NubArca.Api.Party.IPartyUploadService uploads,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var access = await GrantAsync(shares, token, httpContext, cancellationToken);
            if (access is null) return Results.NotFound();
            if (!access.UploadEnabled)
            {
                return Results.Json(
                    new { error = AlbumShareErrors.UploadsDisabled },
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (!httpContext.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected a multipart form upload." });

            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            if (form.Files.Count == 0)
                return Results.BadRequest(new { error = "No files were uploaded." });

            var accepted = 0;
            var rejected = 0;
            string? stopped = null;
            foreach (var file in form.Files)
            {
                // THE SLOT IS CLAIMED FIRST, in one conditional statement, so two
                // phones finishing together cannot both take the last one. The
                // ceiling is the link's and not a person's: a share has no people
                // in it, only an address anybody may be holding.
                var slot = await shares.TryClaimUploadSlotAsync(access.LinkId, cancellationToken);
                if (slot.Outcome != AlbumShareUploadOutcome.Accepted)
                {
                    stopped = slot.ErrorCode;
                    break;
                }

                NubArca.Api.Party.PartyUploadOutcome outcome;
                try
                {
                    await using var stream = file.OpenReadStream();
                    // Every party-shaped argument omitted: no moderation, no
                    // per-person quota, no link to an evening. The service was
                    // already written to work without one.
                    outcome = await uploads.UploadAsync(
                        access.OwnerUserId, access.AlbumId,
                        file.FileName, file.ContentType, file.Length, stream,
                        cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch { outcome = NubArca.Api.Party.PartyUploadOutcome.Failed; }

                if (outcome is NubArca.Api.Party.PartyUploadOutcome.AcceptedPhoto
                    or NubArca.Api.Party.PartyUploadOutcome.AcceptedVideo)
                {
                    accepted++;
                }
                else
                {
                    rejected++;
                    // A refused file did not arrive, so the slot it claimed goes
                    // back. A ceiling that counted failures would close a share
                    // over photographs nobody can see.
                    await shares.ReleaseUploadSlotAsync(access.LinkId, cancellationToken);
                }
            }

            // Counts only. No file names, no token, no storage internals.
            await audit.LogAsync(
                AuditActor.Anonymous, AuditActions.AlbumShareLinkUpload,
                AuditEntityTypes.Album, access.AlbumId, Ip(httpContext),
                new { accepted, rejected }, cancellationToken);

            return Results.Ok(new { accepted, rejected, stopped });
        }).WithName("UploadToAlbumShare").RequireRateLimiting(PublicRateLimitPolicy);

        MapSecondFactor(app);
    }

    private static void MapSecondFactor(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/album-share/{token}/challenge", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareAuth auth,
            [FromBody] AlbumShareChallengeRequest? body,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var outcome = await auth.ChallengeAsync(token, body?.Email, cancellationToken);
            return outcome switch
            {
                // ACCEPTED whether or not the address is on the list, and
                // whether or not the mail went out. Any other answer would make
                // this link a way to enumerate the owner's guest list.
                AlbumShareChallengeOutcome.Accepted => Results.Accepted(),
                AlbumShareChallengeOutcome.TooSoon => Results.Json(
                    new { error = AlbumShareErrors.ResendTooSoon },
                    statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.NotFound(),
            };
        }).WithName("ChallengeAlbumShare").RequireRateLimiting(PublicRateLimitPolicy);

        app.MapPost("/api/album-share/{token}/verify", async (
            string token,
            HttpContext httpContext,
            [FromServices] IAlbumShareAuth auth,
            [FromBody] AlbumShareVerifyRequest? body,
            CancellationToken cancellationToken) =>
        {
            NoStore(httpContext);
            var result = await auth.VerifyAsync(
                token, body?.Email, body?.Code,
                httpContext.Request.Headers.UserAgent.ToString(), cancellationToken);
            if (!result.Verified || result.DeviceToken is null)
            {
                return Results.Json(
                    new { error = result.ErrorCode ?? AlbumShareErrors.InvalidCode },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // HTTP-only, so no script on the page can read it, and scoped to the
            // share routes so it is not offered to anything else on the origin.
            httpContext.Response.Cookies.Append(DeviceCookie, result.DeviceToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/api/album-share",
                MaxAge = AlbumShareLimits.DeviceLifetime,
            });
            return Results.NoContent();
        }).WithName("VerifyAlbumShare").RequireRateLimiting(PublicRateLimitPolicy);
    }

    // ── Shared plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// The grant, or null. Every public route starts here, so none of them can
    /// disagree about what a token opens or forget to ask.
    /// </summary>
    private static async Task<AlbumShareAccess?> GrantAsync(
        IAlbumShareService shares, string token, HttpContext httpContext, CancellationToken ct)
    {
        var resolved = await shares.ResolveAsync(token, Device(httpContext), ct);
        return resolved.Access;
    }

    private static string? Device(HttpContext httpContext) =>
        httpContext.Request.Cookies.TryGetValue(DeviceCookie, out var value) ? value : null;

    private static string? Ip(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();

    private static void NoStore(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = "no-store";
}

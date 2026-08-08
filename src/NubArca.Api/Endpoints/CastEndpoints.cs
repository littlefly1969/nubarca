using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Cast;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Security;

namespace NubArca.Api.Endpoints;

// NUBARCA-GOOGLE-CAST-01: the Cast surface, in two clearly separated halves.
//
// AUTHENTICATED (cookie + cast.access + normal owner authorization): minting and
// revoking a grant. These are ordinary NubArca APIs and are covered by the
// existing CSRF policy like every other state-changing /api call.
//
// GRANT-SCOPED (no cookie): the bytes a Google Default Media Receiver fetches.
// The television cannot hold the owner's session — that is the whole reason this
// route family exists — so authorization is the grant itself: a row located by
// path id, a secret compared in constant time, an expiry, a revocation flag, a
// live account, a live permission and a live file, ALL re-established on every
// single request including every HLS segment. "Anonymous" here means "no
// cookie", never "public": the existing owner endpoints are untouched and stay
// cookie-only.
//
// Every failure on the grant-scoped half collapses to the same bare 404. Whether
// the grant never existed, expired, was revoked, or belongs to an account that
// was disabled ten seconds ago is not something a caller learns from us.
public static class CastEndpoints
{
    // Conservative per-user limit on grant CREATION only. Segment fetches are
    // deliberately NOT limited by it: a two-hour film pulls hundreds of them,
    // and the bearer URL is already scoped to one video and one expiry.
    public const string GrantCreateRateLimitPolicy = "cast-grant-create";

    // The dedicated CORS policy. Applied to the grant-scoped media routes and
    // to nothing else in NubArca.
    public const string MediaCorsPolicy = "cast-media";

    private const string TokenQueryParameter = "token";

    public static IEndpointRouteBuilder MapCastEndpoints(this IEndpointRouteBuilder app)
    {
        MapGrantLifecycle(app);
        MapGrantScopedMedia(app);
        return app;
    }

    // ── authenticated: mint / revoke ────────────────────────────────────────

    private static void MapGrantLifecycle(IEndpointRouteBuilder app)
    {
        // 201 with the grant, 202 while the HLS ladder is being produced, 404
        // for anything this caller could not play anyway. The permission is
        // enforced by the policy; ownership by the service.
        app.MapPost("/api/cast/videos/{fileId:guid}/grant", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] CastGrantService grants,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            SetNoStore(httpContext);

            var creation = await grants.CreateAsync(userId, fileId, cancellationToken);
            switch (creation.Status)
            {
                case CastGrantCreationStatus.Preparing:
                    return VideoHlsServingService.Preparing(httpContext.Response);
                case CastGrantCreationStatus.Created:
                    break;
                default:
                    return Results.NotFound();
            }

            var grant = creation.Grant!;
            await audit.LogAsync(
                userId: userId,
                action: AuditActions.CastGrantCreate,
                entityType: AuditEntityTypes.File,
                entityId: fileId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                // Grant id, expiry and mode. Never the secret, never the URL.
                metadata: new { grantId = grant.GrantId, expiresAt = grant.ExpiresAt, mode = grant.Mode },
                cancellationToken: cancellationToken);

            return Results.Created(
                CastGrantService.MediaBasePath(grant.GrantId), CastGrantResponse.From(grant));
        })
            .WithName("CreateCastGrant")
            .RequirePermission(Permissions.CastAccess)
            .RequireRateLimiting(GrantCreateRateLimitPolicy);

        // Idempotent, owner-scoped. Deliberately NOT behind cast.access: a user
        // whose permission was withdrawn mid-session must still be able to clean
        // up after themselves, and revoking is only ever a reduction of access.
        app.MapDelete("/api/cast/grants/{grantId:guid}", async (
            Guid grantId,
            HttpContext httpContext,
            [FromServices] CastGrantService grants,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.GetCurrentUserId()!.Value;
            SetNoStore(httpContext);

            var existed = await grants.RevokeAsync(grantId, userId, cancellationToken);
            if (existed)
            {
                await audit.LogAsync(
                    userId: userId,
                    action: AuditActions.CastGrantRevoke,
                    entityType: AuditEntityTypes.File,
                    entityId: null,
                    ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    metadata: new { grantId, reason = "explicit" },
                    cancellationToken: cancellationToken);
            }

            // 204 either way: "already gone" and "just revoked" are the same
            // post-condition, and a caller must not learn that somebody else's
            // grant id exists.
            return Results.NoContent();
        }).WithName("RevokeCastGrant").RequireAuthorization();
    }

    // ── grant-scoped: the bytes the receiver plays ──────────────────────────

    private static void MapGrantScopedMedia(IEndpointRouteBuilder app)
    {
        // The playable entry point. Speaks whichever contract this installation
        // is configured for — the rewritten HLS master, or the original bytes
        // with Range support — exactly as /api/files/{id}/video does for the
        // owner's own browser.
        app.MapMethods("/api/cast/media/{grantId:guid}/video", ["GET", "HEAD"], async (
            Guid grantId,
            HttpContext httpContext,
            [FromServices] CastGrantService grants,
            [FromServices] VideoHlsServingService hlsServing,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var token = ReadToken(httpContext);
            var grant = await grants.ResolveAsync(grantId, token, cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            SetNoStore(httpContext);

            if (hlsServing.Enabled)
            {
                // RAW, not the owner form: the URIs are rebuilt here into signed
                // grant-scoped ones, and unpicking somebody else's rewrite first
                // would be exactly the unchecked string surgery this route must
                // not do.
                var master = await hlsServing.GetMasterAsync(
                    grant.FileItemId, grant.UserId, cancellationToken, VideoHlsMasterForm.Raw);
                if (master.Status != VideoHlsMasterStatus.Ready)
                {
                    // A ladder that vanished under a live grant is a 404, not a
                    // 202: the receiver has no preparation UI to show and the
                    // sender re-mints when the user asks again.
                    return Results.NotFound();
                }

                var rewritten = CastHlsPlaylist.RewriteMaster(
                    master.MasterPlaylist!, CastGrantService.MediaBasePath(grantId), token!);
                return rewritten is null
                    ? Results.NotFound()
                    : Results.Text(rewritten, VideoHlsServingService.MasterContentType);
            }

            // Progressive: the ORIGINAL bytes, streamed. Range processing on the
            // seekable FileStream is what lets a receiver seek without pulling
            // the whole file, and nothing is buffered into memory. No download
            // file name — this is a playback URL, not a download.
            if (!SafeContentType.IsTrustedVideo(grant.DetectedContentType))
            {
                return Results.NotFound();
            }
            var content = await files.OpenContentAsync(
                grant.FileItemId, grant.UserId, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            return Results.File(
                content.Content,
                SafeContentType.ForServingVideo(grant.DetectedContentType),
                fileDownloadName: null,
                enableRangeProcessing: true);
        })
            .WithName("StreamCastVideo")
            .AllowAnonymous()
            .RequireCors(MediaCorsPolicy);

        // Variant playlists, init segments and media segments. The rendition and
        // file name are untrusted URL input and are whitelisted inside
        // HlsDerivativeStorage; the grant is re-resolved here first, so a revoke
        // mid-film stops the very next segment.
        app.MapMethods(
            "/api/cast/media/{grantId:guid}/hls/{rendition}/{file}", ["GET", "HEAD"], async (
            Guid grantId,
            string rendition,
            string file,
            HttpContext httpContext,
            [FromServices] CastGrantService grants,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            if (!hlsServing.Enabled)
            {
                return Results.NotFound();
            }

            var token = ReadToken(httpContext);
            var grant = await grants.ResolveAsync(grantId, token, cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            var relative = $"{rendition}/{file}";
            var content = await hlsServing.OpenLadderFileAsync(
                grant.FileItemId, grant.UserId, relative, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetNoStore(httpContext);

            // A variant playlist has to be rewritten too: HLS resolves a
            // segment URI against the playlist's URL and DROPS the query, so an
            // untouched variant would send the receiver at token-less segment
            // URLs and stall on the first one.
            if (relative.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                await using var stream = content.Content;
                using var reader = new StreamReader(stream);
                var rewritten = CastHlsPlaylist.RewriteVariant(
                    await reader.ReadToEndAsync(cancellationToken),
                    CastGrantService.MediaBasePath(grantId),
                    rendition,
                    token!);
                return rewritten is null
                    ? Results.NotFound()
                    : Results.Text(rewritten, VideoHlsServingService.MasterContentType);
            }

            return Results.File(content.Content, content.ContentType);
        })
            .WithName("StreamCastVideoHlsFile")
            .AllowAnonymous()
            .RequireCors(MediaCorsPolicy);

        // The artwork the receiver shows while it buffers, and behind audio-only
        // stretches. Same poster derivative the owner's player uses.
        app.MapMethods("/api/cast/media/{grantId:guid}/poster", ["GET", "HEAD"], async (
            Guid grantId,
            HttpContext httpContext,
            [FromServices] CastGrantService grants,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var grant = await grants.ResolveAsync(grantId, ReadToken(httpContext), cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                grant.FileItemId, grant.UserId, ThumbnailSizes.Poster, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetNoStore(httpContext);
            return Results.File(content.Content, content.MimeType);
        })
            .WithName("GetCastPoster")
            .AllowAnonymous()
            .RequireCors(MediaCorsPolicy);
    }

    private static string? ReadToken(HttpContext httpContext)
    {
        var value = httpContext.Request.Query[TokenQueryParameter].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Cast URLs carry a secret and address a capability that can be withdrawn at
    // any moment, so nothing on this surface may be stored by an intermediary.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Vault;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Private Vault (v0) — owner-private. Locked state reveals NOTHING about
// content. Wrong password and expired/missing/foreign tokens all return the
// SAME generic failure. Browse + move require a valid unlock token (header,
// never query string). Vault media is DERIVED-only (small/medium thumbnail,
// medium preview, or video poster) — NEVER original bytes, downloads, Range
// streams, or HLS.
public static class PrivateVaultEndpoints
{
    private const string VaultUnlockRateLimitPolicy = "vault-unlock";

    public static IEndpointRouteBuilder MapPrivateVaultEndpoints(this IEndpointRouteBuilder app)
    {
        // Private Vault unlock proof: Authorization: Bearer <token> or X-Vault-Token
        // header. NEVER accepted in the query string (would leak into logs/history).
        static string? VaultTokenFrom(HttpContext httpContext)
        {
            var auth = httpContext.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return auth["Bearer ".Length..].Trim();
            }
            var x = httpContext.Request.Headers["X-Vault-Token"].ToString();
            return string.IsNullOrWhiteSpace(x) ? null : x;
        }

        // ── Private Vault (v0) ──────────────────────────────────────────────────────
        // Owner-private. Locked state reveals NOTHING about content. Wrong password and
        // expired/missing/foreign tokens all return the SAME generic failure. Browse +
        // move require a valid unlock token (header, never query string).

        // Status: owner-only. Reveals ONLY whether a password is configured (for the
        // create-vs-unlock UI) + non-secret label/mode. No counts, no content signal.
        app.MapGet("/api/private-vault", async (
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await vault.GetStatusAsync(ownerUserId, cancellationToken);
            return Results.Ok(status);
        }).WithName("GetPrivateVaultStatus").RequireAuthorization();

        app.MapPost("/api/private-vault/setup", async (
            HttpContext httpContext,
            [FromBody] VaultSetupRequest? body,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var outcome = await vault.SetupAsync(ownerUserId, body?.Password ?? string.Empty, cancellationToken);
            switch (outcome)
            {
                case PrivateVaultSetupOutcome.Created:
                    await audit.LogAsync(ownerUserId, AuditActions.PrivateVaultSetup,
                        AuditEntityTypes.PrivateVault, null, ip, null, cancellationToken);
                    return Results.Created("/api/private-vault", new { configured = true });
                case PrivateVaultSetupOutcome.AlreadyConfigured:
                    return Results.Conflict(new { error = "The private area is already configured." });
                default:
                    return Results.BadRequest(new { error = "Password must be at least 8 characters." });
            }
        }).WithName("SetupPrivateVault").RequireAuthorization().RequireRateLimiting(VaultUnlockRateLimitPolicy);

        app.MapPost("/api/private-vault/unlock", async (
            HttpContext httpContext,
            [FromBody] VaultUnlockRequest? body,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var result = await vault.UnlockAsync(ownerUserId, body?.Password ?? string.Empty, cancellationToken);
            if (result is null)
            {
                // Generic failure: identical for missing vault AND wrong password.
                return Results.Json(new { error = "Unable to unlock the private area." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            await audit.LogAsync(ownerUserId, AuditActions.PrivateVaultUnlock,
                AuditEntityTypes.PrivateVault, null, ip, null, cancellationToken);
            return Results.Ok(result);
        }).WithName("UnlockPrivateVault").RequireAuthorization().RequireRateLimiting(VaultUnlockRateLimitPolicy);

        app.MapPost("/api/private-vault/lock", async (
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            await vault.LockAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            await audit.LogAsync(ownerUserId, AuditActions.PrivateVaultLock,
                AuditEntityTypes.PrivateVault, null, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("LockPrivateVault").RequireAuthorization();

        app.MapGet("/api/private-vault/root", async (
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            var listing = await vault.ListRootAsync(ownerUserId, vaultId.Value, cancellationToken);
            return Results.Ok(listing);
        }).WithName("ListPrivateVaultRoot").RequireAuthorization();

        app.MapGet("/api/private-vault/folders/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            var listing = await vault.ListFolderAsync(ownerUserId, vaultId.Value, id, cancellationToken);
            return listing is null ? Results.NotFound() : Results.Ok(listing);
        }).WithName("ListPrivateVaultFolder").RequireAuthorization();

        app.MapPost("/api/private-vault/move-in", async (
            HttpContext httpContext,
            [FromBody] VaultMoveRequest? body,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            var result = await vault.MoveInAsync(
                ownerUserId, vaultId.Value,
                body?.FileIds ?? Array.Empty<Guid>(), body?.FolderIds ?? Array.Empty<Guid>(),
                cancellationToken);
            await audit.LogAsync(ownerUserId, AuditActions.PrivateVaultMoveIn,
                AuditEntityTypes.PrivateVault, null, ip,
                new { movedFiles = result.MovedFiles, movedFolders = result.MovedFolders }, cancellationToken);
            return Results.Ok(result);
        }).WithName("PrivateVaultMoveIn").RequireAuthorization();

        app.MapPost("/api/private-vault/move-out", async (
            HttpContext httpContext,
            [FromBody] VaultMoveRequest? body,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            var result = await vault.MoveOutAsync(
                ownerUserId, vaultId.Value,
                body?.FileIds ?? Array.Empty<Guid>(), body?.FolderIds ?? Array.Empty<Guid>(),
                cancellationToken);
            await audit.LogAsync(ownerUserId, AuditActions.PrivateVaultMoveOut,
                AuditEntityTypes.PrivateVault, null, ip,
                new { movedFiles = result.MovedFiles, movedFolders = result.MovedFolders }, cancellationToken);
            return Results.Ok(result);
        }).WithName("PrivateVaultMoveOut").RequireAuthorization();

        // ── Private Vault media (slice 4) ───────────────────────────────────────────
        // Owner-private DERIVED-media only: small/medium thumbnail, medium preview, or
        // video poster — NEVER original bytes, downloads, Range streams, or HLS. Each
        // request re-checks (1) the authenticated session, (2) a valid unlock token
        // that (3) belongs to the current owner, then (4/5) that the file is currently
        // inside THAT owner's vault, and (6) opens only the derivative tied to that
        // file's blob. Missing derivative → generic 404 (never a fallback to the
        // original, never any generation/enqueue). Every response is no-store; the
        // global middleware adds nosniff. A locked/expired/foreign token → 401.

        // Shared serving path for the byte endpoints. `allowedSizes` restricts which
        // derivative a given endpoint may open (thumbnail: small|medium; preview:
        // medium; poster: poster). Any other case collapses to an indistinguishable 404.
        static async Task<IResult> ServeVaultDerivativeAsync(
            HttpContext httpContext,
            IPrivateVaultService vault,
            IFileThumbnailService thumbnails,
            Guid fileId,
            string size,
            CancellationToken cancellationToken)
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var content = await thumbnails.OpenVaultAsync(fileId, ownerUserId, vaultId.Value, size, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            return Results.File(content.Content, content.MimeType);
        }

        // Small grid thumbnail / medium viewer preview. Only these two sizes are
        // accepted; anything else is a generic 404 (no signal about the file).
        app.MapGet("/api/private-vault/media/{fileId:guid}/thumbnail", async (
            Guid fileId,
            string? size,
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var requested = string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size.Trim().ToLowerInvariant();
            if (requested != ThumbnailSizes.Small && requested != ThumbnailSizes.Medium)
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }
            return await ServeVaultDerivativeAsync(httpContext, vault, thumbnails, fileId, requested, cancellationToken);
        }).WithName("GetPrivateVaultMediaThumbnail").RequireAuthorization();

        // Medium photo preview (viewer/lightbox). A non-photo file simply has no medium
        // derivative → generic 404. No original fallback.
        app.MapGet("/api/private-vault/media/{fileId:guid}/preview", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
            await ServeVaultDerivativeAsync(
                httpContext, vault, thumbnails, fileId, ThumbnailSizes.Medium, cancellationToken))
            .WithName("GetPrivateVaultMediaPreview").RequireAuthorization();

        // Video poster. A non-video file has no poster derivative → generic 404.
        app.MapGet("/api/private-vault/media/{fileId:guid}/poster", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
            await ServeVaultDerivativeAsync(
                httpContext, vault, thumbnails, fileId, ThumbnailSizes.Poster, cancellationToken))
            .WithName("GetPrivateVaultMediaPoster").RequireAuthorization();

        // Sanitized read-only detail for the viewer / info panel.
        app.MapGet("/api/private-vault/media/{fileId:guid}/info", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IPrivateVaultService vault,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var vaultId = await vault.ResolveVaultAsync(ownerUserId, VaultTokenFrom(httpContext), cancellationToken);
            if (vaultId is null)
            {
                return Results.Json(new { error = "Locked." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            var info = await vault.GetMediaInfoAsync(ownerUserId, vaultId.Value, fileId, cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        }).WithName("GetPrivateVaultMediaInfo").RequireAuthorization();

        return app;
    }

    // Duplicated from Program.cs's local SetNoStore helper (used by dozens of
    // other still-inline endpoints there, so it stays put) — same logic.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

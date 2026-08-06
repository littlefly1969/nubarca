using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Security;
using NubArca.Api.ShareLinks;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Share Links — owner-side create/list/revoke plus the public, anonymous,
// rate-limited short-URL download. Owner-side routes are authenticated and
// owner-scoped; the public route is token-scoped only (no Private Vault
// path exists here — a share is created against a specific FileItem, and
// Vault content is never share-able). Revoked/expired/exhausted/unknown
// tokens all collapse to the same generic 404 (no-leak). Raw tokens are
// never logged; token hashes are never exposed in any response.
public static class ShareLinkEndpoints
{
    private const string SharePublicRateLimitPolicy = "share-public";

    public static IEndpointRouteBuilder MapShareLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/share-links", async (
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            [FromQuery] string? status,
            HttpContext httpContext,
            [FromServices] IShareLinkService shareLinks,
            CancellationToken cancellationToken) =>
        {
            const int DefaultLimit = 50;
            const int MaxLimit = 200;

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var effectiveLimit = limit ?? DefaultLimit;
            if (effectiveLimit < 1) effectiveLimit = 1;
            if (effectiveLimit > MaxLimit) effectiveLimit = MaxLimit;

            var effectiveOffset = offset ?? 0;
            if (effectiveOffset < 0) effectiveOffset = 0;

            if (!ShareLinkStatus.TryParse(status, out var statusFilter))
            {
                return Results.BadRequest(new { error = "'status' must be one of: all, active, expired, revoked." });
            }

            var response = await shareLinks.ListForOwnerAsync(
                ownerUserId, statusFilter, effectiveLimit, effectiveOffset, cancellationToken);
            return Results.Ok(response);
        }).WithName("ListShareLinks").RequireAuthorization();

        app.MapGet("/api/files/{fileId:guid}/share-links", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IShareLinkService shareLinks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var summaries = await shareLinks.ListByFileAsync(ownerUserId, fileId, cancellationToken);
            if (summaries is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(summaries);
        }).WithName("ListShareLinksForFile").RequireAuthorization();

        app.MapPost("/api/files/{fileId:guid}/share-links", async (
            Guid fileId,
            HttpContext httpContext,
            CreateShareLinkRequest? body,
            [FromServices] IShareLinkService shareLinks,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body?.ExpiresAt is DateTime when && when <= DateTime.UtcNow)
            {
                return Results.BadRequest(new { error = "'expiresAt' must be in the future." });
            }
            if (body?.MaxDownloads is int max && max <= 0)
            {
                return Results.BadRequest(new { error = "'maxDownloads' must be positive." });
            }

            var result = await shareLinks.CreateAsync(
                ownerUserId, fileId, body?.ExpiresAt, body?.MaxDownloads, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.ShareCreate,
                entityType: AuditEntityTypes.ShareLink,
                entityId: result.Id,
                ipAddress: ip,
                metadata: new { fileItemId = fileId, expiresAt = result.ExpiresAt, maxDownloads = result.MaxDownloads },
                cancellationToken: cancellationToken);

            var url = $"/s/{result.Token}";
            var response = new ShareLinkCreatedResponse(result.Id, result.Token, url, result.ExpiresAt, result.MaxDownloads);
            return Results.Created($"/api/share-links/{result.Id}", response);
        }).WithName("CreateShareLink").RequireAuthorization();

        app.MapPost("/api/share-links/{id:guid}/revoke", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IShareLinkService shareLinks,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var revoked = await shareLinks.RevokeAsync(ownerUserId, id, cancellationToken);
            if (!revoked)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.ShareRevoke,
                entityType: AuditEntityTypes.ShareLink,
                entityId: id,
                ipAddress: ip,
                metadata: null,
                cancellationToken: cancellationToken);

            return Results.NoContent();
        }).WithName("RevokeShareLink").RequireAuthorization();

        // Public, no auth: shareable short URL. Returns 404 indistinguishably for
        // unknown / revoked / expired / exhausted tokens to avoid leaking which one
        // a probe hit.
        app.MapGet("/s/{token}", async (
            string token,
            HttpContext httpContext,
            [FromServices] IShareLinkService shareLinks,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var consumed = await shareLinks.ConsumeAsync(token, cancellationToken);
            if (consumed is null)
            {
                return Results.NotFound();
            }

            var content = await files.OpenContentAsync(consumed.FileItemId, consumed.OwnerUserId, cancellationToken);
            if (content is null)
            {
                // The file was soft-deleted between the increment and the open.
                // The download counter is "burned" for this attempt; acceptable.
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: null,
                action: AuditActions.SharePublicDownload,
                entityType: AuditEntityTypes.ShareLink,
                entityId: null,
                ipAddress: ip,
                metadata: new { fileItemId = consumed.FileItemId },
                cancellationToken: cancellationToken);

            // Same untrusted-MIME hardening as the authenticated download path.
            return Results.File(
                content.Content, SafeContentType.ForServing(content.DetectedContentType), content.FileName);
        }).WithName("PublicDownloadByShareLink").RequireRateLimiting(SharePublicRateLimitPolicy);

        return app;
    }
}

using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums;
using NubArca.Api.Audit;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization, status codes, DTOs, and
// audit behavior are unchanged from the original inline mappings.
//
// Albums (slice 67). Owner-scoped collections of `FileItem` references.
// Deliberately excludes the album-nested PARTY routes
// (`/api/albums/{id}/party-settings`, `/api/albums/{albumId}/party-uploads/*`)
// — those stay in Program.cs as Party feature endpoints that happen to be
// path-nested under `/api/albums` for REST resource shape; extracting them
// here would violate this slice's explicit Party exclusion. Registration
// order relative to those routes does not affect matching: none of the
// album templates below overlap the party-settings/party-uploads templates.
public static class AlbumEndpoints
{
    // Bulk add/remove of many gallery-selected items to/from an album. Metadata
    // carries safe counts only (requested/succeeded/skipped), never file ids en masse.
    private const int MaxBulkAlbumItems = 1000;

    public static IEndpointRouteBuilder MapAlbumEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums", async (
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var list = await albums.ListAsync(ownerUserId, cancellationToken);
            return Results.Ok(list);
        }).WithName("ListAlbums").RequireAuthorization();

        app.MapPost("/api/albums", async (
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            [FromBody] CreateAlbumRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "Missing 'name'." });

            try
            {
                var detail = await albums.CreateAsync(ownerUserId, body.Name, body.Description, cancellationToken);
                await audit.LogAsync(ownerUserId, AuditActions.AlbumCreate, AuditEntityTypes.Album,
                    detail.Id, ip, new { name = detail.Name }, cancellationToken);
                return Results.Created($"/api/albums/{detail.Id}", detail);
            }
            catch (DuplicateAlbumNameException)
            {
                return Results.Conflict(new { error = "An album with this name already exists." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("CreateAlbum").RequireAuthorization();

        app.MapGet("/api/albums/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var detail = await albums.GetByIdAsync(id, ownerUserId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetAlbum").RequireAuthorization();

        app.MapMethods("/api/albums/{id:guid}", ["PATCH"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            [FromBody] UpdateAlbumRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "Missing 'name'." });

            try
            {
                var detail = await albums.UpdateAsync(id, ownerUserId, body.Name, body.Description, cancellationToken);
                if (detail is null)
                    return Results.NotFound();
                await audit.LogAsync(ownerUserId, AuditActions.AlbumUpdate, AuditEntityTypes.Album,
                    id, ip, new { name = detail.Name }, cancellationToken);
                return Results.Ok(detail);
            }
            catch (DuplicateAlbumNameException)
            {
                return Results.Conflict(new { error = "An album with this name already exists." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("UpdateAlbum").RequireAuthorization();

        app.MapDelete("/api/albums/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var deleted = await albums.DeleteAsync(id, ownerUserId, cancellationToken);
            if (!deleted)
                return Results.NotFound();

            await audit.LogAsync(ownerUserId, AuditActions.AlbumDelete, AuditEntityTypes.Album,
                id, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("DeleteAlbum").RequireAuthorization();

        // Owner-only toggle of an album's "Show on TV" allowlist flag. Requires the
        // normal authenticated user cookie (never the TV session). Owner-scoped: a
        // foreign / missing album is a generic 404. This is deliberately separate from
        // public sharing — it grants visibility only to the owner's own paired TVs.
        app.MapMethods("/api/albums/{id:guid}/tv-settings", ["PATCH"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            [FromBody] SetAlbumTvVisibilityRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
                return Results.BadRequest(new { error = "Missing request body." });

            var detail = await albums.SetTvVisibilityAsync(id, ownerUserId, body.ShowOnTv, cancellationToken);
            if (detail is null)
                return Results.NotFound();

            await audit.LogAsync(ownerUserId, AuditActions.AlbumUpdate, AuditEntityTypes.Album,
                id, ip, new { showOnTv = body.ShowOnTv }, cancellationToken);
            return Results.Ok(detail);
        }).WithName("SetAlbumTvVisibility").RequireAuthorization();

        app.MapGet("/api/albums/{id:guid}/items", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var items = await albums.ListItemsAsync(id, ownerUserId, cancellationToken);
            return items is null ? Results.NotFound() : Results.Ok(items);
        }).WithName("ListAlbumItems").RequireAuthorization();

        app.MapPost("/api/albums/{id:guid}/items", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            [FromBody] AddAlbumItemRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
                return Results.BadRequest(new { error = "Missing request body." });

            var added = await albums.AddItemAsync(id, ownerUserId, body.FileItemId, cancellationToken);
            if (!added)
                return Results.NotFound();

            await audit.LogAsync(ownerUserId, AuditActions.AlbumItemAdd, AuditEntityTypes.Album,
                id, ip, new { fileItemId = body.FileItemId }, cancellationToken);
            return Results.NoContent();
        }).WithName("AddAlbumItem").RequireAuthorization();

        app.MapDelete("/api/albums/{id:guid}/items/{fileId:guid}", async (
            Guid id,
            Guid fileId,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var removed = await albums.RemoveItemAsync(id, ownerUserId, fileId, cancellationToken);
            if (!removed)
                return Results.NotFound();

            await audit.LogAsync(ownerUserId, AuditActions.AlbumItemRemove, AuditEntityTypes.Album,
                id, ip, new { fileItemId = fileId }, cancellationToken);
            await moderation.MarkRemovedFromAlbumAsync(
                ownerUserId, id, fileId, ownerUserId, cancellationToken);
            return Results.NoContent();
        }).WithName("RemoveAlbumItem").RequireAuthorization();

        // Bulk add many gallery-selected items to an album. Owner-authenticated;
        // album + files must belong to the caller. Idempotent (duplicates skipped);
        // foreign/missing ids are silently skipped (no existence leak). Returns a safe
        // counts-only summary. Never touches storage/blob internals.
        app.MapPost("/api/albums/{id:guid}/items/bulk", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            [FromBody] BulkAlbumItemsRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body?.FileItemIds is null)
                return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
            if (body.FileItemIds.Count > MaxBulkAlbumItems)
                return Results.BadRequest(new { error = $"At most {MaxBulkAlbumItems} items per request." });

            var result = await albums.AddItemsAsync(id, ownerUserId, body.FileItemIds, cancellationToken);
            if (result is null)
                return Results.NotFound();

            await audit.LogAsync(ownerUserId, AuditActions.AlbumItemsBulkAdd, AuditEntityTypes.Album,
                id, ip, new { result.Requested, result.Succeeded, result.Skipped }, cancellationToken);
            return Results.Ok(result);
        }).WithName("BulkAddAlbumItems").RequireAuthorization();

        // Bulk remove many items from an album. Album membership only — never deletes
        // the underlying FileItem/blob. Owner-authenticated; idempotent.
        app.MapMethods("/api/albums/{id:guid}/items/bulk", ["DELETE"], async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IAlbumService albums,
            [FromServices] NubArca.Api.Party.IPartyModerationService moderation,
            [FromServices] IAuditLogger audit,
            [FromBody] BulkAlbumItemsRequest? body,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body?.FileItemIds is null)
                return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
            if (body.FileItemIds.Count > MaxBulkAlbumItems)
                return Results.BadRequest(new { error = $"At most {MaxBulkAlbumItems} items per request." });

            var result = await albums.RemoveItemsAsync(id, ownerUserId, body.FileItemIds, cancellationToken);
            if (result is null)
                return Results.NotFound();

            // Keep party-upload provenance consistent (no-op for owner-added content).
            foreach (var fileId in body.FileItemIds.Distinct())
            {
                await moderation.MarkRemovedFromAlbumAsync(ownerUserId, id, fileId, ownerUserId, cancellationToken);
            }

            await audit.LogAsync(ownerUserId, AuditActions.AlbumItemsBulkRemove, AuditEntityTypes.Album,
                id, ip, new { result.Requested, result.Succeeded, result.Skipped }, cancellationToken);
            return Results.Ok(result);
        }).WithName("BulkRemoveAlbumItems").RequireAuthorization();

        return app;
    }
}

// Album request bodies (slice 67). Moved from Program.cs's top-level
// records — used exclusively by the album endpoints above.
// SetAlbumPartyModeRequest stays in Program.cs: it belongs to the Party
// party-settings endpoint, which this slice deliberately does not extract.
public sealed record CreateAlbumRequest(string Name, string? Description = null);
public sealed record UpdateAlbumRequest(string Name, string? Description = null);
public sealed record AddAlbumItemRequest(Guid FileItemId);
public sealed record SetAlbumTvVisibilityRequest(bool ShowOnTv);

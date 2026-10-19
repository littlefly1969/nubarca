using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Http;
using NubArca.Api.MediaLists;

namespace NubArca.Api.Endpoints;

// A member's personal selection inside one album shared with them.
//
//   GET    /api/shared-albums/{albumId}/selection          { version, count }
//   GET    /api/shared-albums/{albumId}/selection/ids      id page in pick order (sync)
//   POST   /api/shared-albums/{albumId}/selection/items    add { fileItemIds } — idempotent
//   DELETE /api/shared-albums/{albumId}/selection/items    remove { fileItemIds } — idempotent
//   POST   /api/shared-albums/{albumId}/selection/clear    empty it
//
// Every request resolves the album grant first, exactly as the album's own
// reads do: a revoked, declined or missing membership is the same 404 as an
// album that does not exist, and validation runs only after it, so a malformed
// body from a non-member still learns nothing. Nobody else's selection is
// addressable at all — the selection is the caller's, by construction.
public static class AlbumSelectionEndpoints
{
    public sealed record AlbumSelectionItemsRequest(IReadOnlyList<Guid>? FileItemIds);

    public static IEndpointRouteBuilder MapAlbumSelectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/shared-albums/{albumId:guid}/selection", async (
            Guid albumId, HttpContext http,
            [FromServices] IAlbumAccessResolver access, [FromServices] IAlbumSelectionService selections,
            CancellationToken ct) =>
        {
            var grant = await access.ResolveAsync(albumId, http.GetCurrentUserId()!.Value, ct);
            if (grant is null) return Results.NotFound();
            NoStore(http);
            return Results.Ok(await selections.GetAsync(grant, ct));
        }).WithName("GetSharedAlbumSelection").RequireAuthorization();

        app.MapGet("/api/shared-albums/{albumId:guid}/selection/ids", async (
            Guid albumId, [FromQuery] string? cursor, [FromQuery] int? limit, HttpContext http,
            [FromServices] IAlbumAccessResolver access, [FromServices] IAlbumSelectionService selections,
            CancellationToken ct) =>
        {
            var grant = await access.ResolveAsync(albumId, http.GetCurrentUserId()!.Value, ct);
            if (grant is null) return Results.NotFound();
            var page = await selections.ListIdsAsync(
                grant, cursor, Math.Clamp(limit ?? 500, 1, MediaListService.MaxIdsPageSize), ct);
            if (page is null) return Results.BadRequest(new { error = "'cursor' is malformed." });
            NoStore(http);
            return Results.Ok(page);
        }).WithName("ListSharedAlbumSelectionIds").RequireAuthorization();

        app.MapPost("/api/shared-albums/{albumId:guid}/selection/items", async (
            Guid albumId, HttpContext http,
            [FromServices] IAlbumAccessResolver access, [FromServices] IAlbumSelectionService selections,
            [FromBody] AlbumSelectionItemsRequest? body, CancellationToken ct) =>
        {
            var grant = await access.ResolveAsync(albumId, http.GetCurrentUserId()!.Value, ct);
            if (grant is null) return Results.NotFound();
            if (Invalid(body) is { } error) return error;
            return Results.Ok(await selections.AddAsync(grant, body!.FileItemIds!, ct));
        }).WithName("AddSharedAlbumSelectionItems").RequireAuthorization();

        app.MapMethods("/api/shared-albums/{albumId:guid}/selection/items", ["DELETE"], async (
            Guid albumId, HttpContext http,
            [FromServices] IAlbumAccessResolver access, [FromServices] IAlbumSelectionService selections,
            [FromBody] AlbumSelectionItemsRequest? body, CancellationToken ct) =>
        {
            var grant = await access.ResolveAsync(albumId, http.GetCurrentUserId()!.Value, ct);
            if (grant is null) return Results.NotFound();
            if (Invalid(body) is { } error) return error;
            return Results.Ok(await selections.RemoveAsync(grant, body!.FileItemIds!, ct));
        }).WithName("RemoveSharedAlbumSelectionItems").RequireAuthorization();

        app.MapPost("/api/shared-albums/{albumId:guid}/selection/clear", async (
            Guid albumId, HttpContext http,
            [FromServices] IAlbumAccessResolver access, [FromServices] IAlbumSelectionService selections,
            CancellationToken ct) =>
        {
            var grant = await access.ResolveAsync(albumId, http.GetCurrentUserId()!.Value, ct);
            if (grant is null) return Results.NotFound();
            return Results.Ok(await selections.ClearAsync(grant, ct));
        }).WithName("ClearSharedAlbumSelection").RequireAuthorization();

        return app;
    }

    private static IResult? Invalid(AlbumSelectionItemsRequest? body)
    {
        if (body?.FileItemIds is null) return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
        if (body.FileItemIds.Count > MediaListService.MaxItemsPerRequest)
            return Results.BadRequest(new { error = $"At most {MediaListService.MaxItemsPerRequest} items per request." });
        return null;
    }

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

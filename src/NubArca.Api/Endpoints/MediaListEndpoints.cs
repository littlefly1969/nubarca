using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.MediaLists;

namespace NubArca.Api.Endpoints;

// Owner media lists and the account's current selection.
//
//   GET    /api/lists                         named lists (summary + visible count)
//   POST   /api/lists                         create { name, fileItemIds? }
//   GET    /api/lists/{id}                    summary (also the selection, by its id)
//   PATCH  /api/lists/{id}                    rename { name, expectedVersion? } → 409 on a stale version
//   DELETE /api/lists/{id}                    delete a named list (never a file)
//   POST   /api/lists/{id}/duplicate          { name? } — also "save the selection as a list"
//   POST   /api/lists/{id}/items              add { fileItemIds } — idempotent, deduplicated
//   DELETE /api/lists/{id}/items              remove { fileItemIds } — idempotent
//   POST   /api/lists/{id}/clear              empty the list
//   GET    /api/lists/{id}/items              MediaItem page in the list's own order
//   GET    /api/lists/{id}/ids                id page in the list's own order (sync)
//   GET    /api/lists/{id}/media              the gallery contract over the list (filters, sort, cursor)
//   GET    /api/selection                     the current selection's summary (created on first use)
//
// Every route is owner-only: a foreign or missing list is a 404, never a 403,
// so nobody learns that a list id exists.
public static class MediaListEndpoints
{
    public sealed record CreateMediaListRequest(string? Name, IReadOnlyList<Guid>? FileItemIds);
    public sealed record RenameMediaListRequest(string? Name, int? ExpectedVersion);
    public sealed record DuplicateMediaListRequest(string? Name);
    public sealed record MediaListItemsRequest(IReadOnlyList<Guid>? FileItemIds);

    public static IEndpointRouteBuilder MapMediaListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/selection", async (
            HttpContext http, [FromServices] IMediaListService lists, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await lists.GetSelectionAsync(http.GetCurrentUserId()!.Value, ct));
        }).WithName("GetCurrentSelection").RequireAuthorization();

        app.MapGet("/api/lists", async (
            HttpContext http, [FromServices] IMediaListService lists, CancellationToken ct) =>
        {
            NoStore(http);
            return Results.Ok(await lists.ListAsync(http.GetCurrentUserId()!.Value, ct));
        }).WithName("ListMediaLists").RequireAuthorization();

        app.MapPost("/api/lists", async (
            HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            [FromBody] CreateMediaListRequest? body, CancellationToken ct) =>
        {
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.CreateAsync(owner, body?.Name, body?.FileItemIds, ct);
            if (result.Outcome != MediaListOutcome.Ok) return Map(result);
            await audit.LogAsync(owner, AuditActions.MediaListCreate, AuditEntityTypes.MediaList,
                result.Value!.Id, Ip(http), new { result.Value.Count }, ct);
            return Results.Created($"/api/lists/{result.Value.Id}", result.Value);
        }).WithName("CreateMediaList").RequireAuthorization();

        app.MapGet("/api/lists/{id:guid}", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, CancellationToken ct) =>
        {
            NoStore(http);
            var list = await lists.GetAsync(http.GetCurrentUserId()!.Value, id, ct);
            return list is null ? Results.NotFound() : Results.Ok(list);
        }).WithName("GetMediaList").RequireAuthorization();

        app.MapPatch("/api/lists/{id:guid}", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            [FromBody] RenameMediaListRequest? body, CancellationToken ct) =>
        {
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.RenameAsync(owner, id, body?.Name, body?.ExpectedVersion, ct);
            if (result.Outcome == MediaListOutcome.Ok)
                await audit.LogAsync(owner, AuditActions.MediaListRename, AuditEntityTypes.MediaList, id, Ip(http), null, ct);
            return Map(result);
        }).WithName("RenameMediaList").RequireAuthorization();

        app.MapDelete("/api/lists/{id:guid}", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
        {
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.DeleteAsync(owner, id, ct);
            if (result.Outcome != MediaListOutcome.Ok) return Map(result);
            await audit.LogAsync(owner, AuditActions.MediaListDelete, AuditEntityTypes.MediaList, id, Ip(http), null, ct);
            return Results.NoContent();
        }).WithName("DeleteMediaList").RequireAuthorization();

        app.MapPost("/api/lists/{id:guid}/duplicate", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            [FromBody] DuplicateMediaListRequest? body, CancellationToken ct) =>
        {
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.DuplicateAsync(owner, id, body?.Name, ct);
            if (result.Outcome != MediaListOutcome.Ok) return Map(result);
            await audit.LogAsync(owner, AuditActions.MediaListDuplicate, AuditEntityTypes.MediaList,
                result.Value!.Id, Ip(http), new { result.Value.Count }, ct);
            return Results.Created($"/api/lists/{result.Value.Id}", result.Value);
        }).WithName("DuplicateMediaList").RequireAuthorization();

        app.MapPost("/api/lists/{id:guid}/items", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            [FromBody] MediaListItemsRequest? body, CancellationToken ct) =>
        {
            if (body?.FileItemIds is null) return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.AddItemsAsync(owner, id, body.FileItemIds, ct);
            if (result.Outcome == MediaListOutcome.Ok && !await IsSelectionAsync(lists, owner, id, ct))
            {
                await audit.LogAsync(owner, AuditActions.MediaListItemsAdd, AuditEntityTypes.MediaList, id, Ip(http),
                    new { result.Value!.Requested, result.Value.Succeeded, result.Value.Skipped }, ct);
            }
            return Map(result);
        }).WithName("AddMediaListItems").RequireAuthorization();

        app.MapMethods("/api/lists/{id:guid}/items", ["DELETE"], async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            [FromBody] MediaListItemsRequest? body, CancellationToken ct) =>
        {
            if (body?.FileItemIds is null) return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.RemoveItemsAsync(owner, id, body.FileItemIds, ct);
            if (result.Outcome == MediaListOutcome.Ok && !await IsSelectionAsync(lists, owner, id, ct))
            {
                await audit.LogAsync(owner, AuditActions.MediaListItemsRemove, AuditEntityTypes.MediaList, id, Ip(http),
                    new { result.Value!.Requested, result.Value.Succeeded, result.Value.Skipped }, ct);
            }
            return Map(result);
        }).WithName("RemoveMediaListItems").RequireAuthorization();

        app.MapPost("/api/lists/{id:guid}/clear", async (
            Guid id, HttpContext http, [FromServices] IMediaListService lists, [FromServices] IAuditLogger audit,
            CancellationToken ct) =>
        {
            var owner = http.GetCurrentUserId()!.Value;
            var result = await lists.ClearAsync(owner, id, ct);
            if (result.Outcome == MediaListOutcome.Ok && !await IsSelectionAsync(lists, owner, id, ct))
            {
                await audit.LogAsync(owner, AuditActions.MediaListItemsRemove, AuditEntityTypes.MediaList, id, Ip(http),
                    new { result.Value!.Requested, result.Value.Succeeded, result.Value.Skipped }, ct);
            }
            return Map(result);
        }).WithName("ClearMediaList").RequireAuthorization();

        app.MapGet("/api/lists/{id:guid}/items", async (
            Guid id, [FromQuery] string? cursor, [FromQuery] int? limit,
            HttpContext http, [FromServices] IMediaListService lists, CancellationToken ct) =>
        {
            NoStore(http);
            return Map(await lists.ListItemsAsync(http.GetCurrentUserId()!.Value, id, cursor, limit ?? 60, ct));
        }).WithName("ListMediaListItems").RequireAuthorization();

        app.MapGet("/api/lists/{id:guid}/ids", async (
            Guid id, [FromQuery] string? cursor, [FromQuery] int? limit,
            HttpContext http, [FromServices] IMediaListService lists, CancellationToken ct) =>
        {
            NoStore(http);
            return Map(await lists.ListIdsAsync(http.GetCurrentUserId()!.Value, id, cursor, limit ?? 500, ct));
        }).WithName("ListMediaListIds").RequireAuthorization();

        return app;
    }

    private static async Task<bool> IsSelectionAsync(IMediaListService lists, Guid owner, Guid id, CancellationToken ct) =>
        (await lists.GetAsync(owner, id, ct))?.Kind == "selection";

    private static IResult Map<T>(MediaListResult<T> result) => result.Outcome switch
    {
        MediaListOutcome.Ok => Results.Ok(result.Value),
        MediaListOutcome.NotFound => Results.NotFound(),
        MediaListOutcome.Conflict => Results.Conflict(new { error = result.Error }),
        _ => Results.BadRequest(new { error = result.Error }),
    };

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

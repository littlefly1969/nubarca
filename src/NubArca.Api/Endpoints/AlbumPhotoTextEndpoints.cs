using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Http;
using NubArca.Api.Data;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

public static class AlbumPhotoTextEndpoints
{
    public static void MapAlbumPhotoTextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/{albumId:guid}/photos/{fileId:guid}/{variant}", async (
            Guid albumId, Guid fileId, string variant, HttpContext http, [FromServices] AppDbContext db,
            [FromServices] NubArca.Api.Files.IFileThumbnailService thumbnails, [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper, CancellationToken ct) =>
        {
            if (variant is not ("thumbnail" or "preview")) return Results.NotFound();
            var owner = http.GetCurrentUserId()!.Value;
            if (!await IsOwnedPhotoAsync(db, owner, albumId, fileId, ct)) return Results.NotFound();
            return await PartyEndpoints.ServeAlbumDerivativeAsync(owner, albumId, fileId, PartyMediaKind.Image,
                variant, http, thumbnails, stripper, ct);
        }).RequireAuthorization();
        app.MapGet("/api/party/{token}/photos/{fileId:guid}/text", async (
            string token, Guid fileId, HttpContext http, [FromServices] AppDbContext db,
            [FromServices] IPartyLinkService links, [FromServices] IPartyMediaService media, [FromServices] IPartyParticipantService participants, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var access = await links.ResolvePublicAsync(token, ct);
            if (access is null || !access.Experience.AllowsAlbumMedia
                || await media.GetVisibleMediaKindAsync(access.OwnerUserId, access.MainAlbumId, fileId, ct) != PartyMediaKind.Image)
                return Results.NotFound();
            var guest = await PartyGuestSession.ResolveAsync(http, participants, access.PartyAlbumLinkId, ct);
            if (http.GetCurrentUserId() != access.OwnerUserId
                && (!access.Capabilities.Contributions || !await AlbumPhotoText.IsUploaderAsync(db, access.MainAlbumId, fileId, guest, null, ct)))
                return Results.NotFound();
            return Results.Ok(await AlbumPhotoText.ReadAsync(db, access.MainAlbumId, fileId, ct));
        }).RequireRateLimiting("party-public");

        app.MapPut("/api/party/{token}/photos/{fileId:guid}/text", async (
            string token, Guid fileId, [FromBody] AlbumPhotoTextWrite write, HttpContext http, [FromServices] AppDbContext db,
            [FromServices] IPartyLinkService links, [FromServices] IPartyMediaService media, [FromServices] IPartyParticipantService participants, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var access = await links.ResolvePublicAsync(token, ct);
            if (access is null || !access.Experience.AllowsAlbumMedia
                || await media.GetVisibleMediaKindAsync(access.OwnerUserId, access.MainAlbumId, fileId, ct) != PartyMediaKind.Image)
                return Results.NotFound();
            var guest = await PartyGuestSession.ResolveAsync(http, participants, access.PartyAlbumLinkId, ct);
            if (http.GetCurrentUserId() != access.OwnerUserId
                && (!access.Capabilities.Contributions || !await AlbumPhotoText.IsUploaderAsync(db, access.MainAlbumId, fileId, guest, null, ct)))
                return Results.NotFound();
            return await Save(db, access.MainAlbumId, fileId, write, ct);
        }).RequireRateLimiting("party-upload");

        app.MapGet("/api/albums/{albumId:guid}/photo-text", async (
            Guid albumId, HttpContext http, [FromServices] AppDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var owner = http.GetCurrentUserId()!.Value;
            if (!await db.Albums.AnyAsync(a => a.Id == albumId && a.OwnerUserId == owner, ct)) return Results.NotFound();
            return Results.Ok(await db.AlbumItems.AsNoTracking().Where(i => i.AlbumId == albumId && i.PhotoTextVersion > 0
                && db.FileItems.Any(f => f.Id == i.FileItemId && f.OwnerUserId == owner && f.DeletedAt == null
                    && f.MediaLibraryState == NubArca.Api.Domain.MediaLibraryState.Active))
                .Select(i => new { i.FileItemId, Text = i.PhotoText, Layout = i.PhotoTextLayout, Version = i.PhotoTextVersion }).ToListAsync(ct));
        }).RequireAuthorization();

        app.MapGet("/api/albums/{albumId:guid}/photos/{fileId:guid}/text", async (
            Guid albumId, Guid fileId, HttpContext http, [FromServices] AppDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var owner = http.GetCurrentUserId()!.Value;
            if (!await IsOwnedPhotoAsync(db, owner, albumId, fileId, ct)) return Results.NotFound();
            return Results.Ok(await AlbumPhotoText.ReadAsync(db, albumId, fileId, ct));
        }).RequireAuthorization();

        app.MapPut("/api/albums/{albumId:guid}/photos/{fileId:guid}/text", async (
            Guid albumId, Guid fileId, [FromBody] AlbumPhotoTextWrite write, HttpContext http, [FromServices] AppDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!await IsOwnedPhotoAsync(db, http.GetCurrentUserId()!.Value, albumId, fileId, ct)) return Results.NotFound();
            return await Save(db, albumId, fileId, write, ct);
        }).RequirePermission(Permissions.PartyAccess);
    }

    private static Task<bool> IsOwnedPhotoAsync(AppDbContext db, Guid owner, Guid albumId, Guid fileId, CancellationToken ct) =>
        db.AlbumItems.AnyAsync(i => i.AlbumId == albumId && i.FileItemId == fileId
            && db.Albums.Any(a => a.Id == albumId && a.OwnerUserId == owner)
            && db.FileItems.Any(f => f.Id == fileId && f.OwnerUserId == owner && f.DeletedAt == null
                && f.MediaLibraryState == NubArca.Api.Domain.MediaLibraryState.Active
                && db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId && m.MediaCategory == NubArca.Api.Domain.MediaCategories.Image)), ct);

    private static async Task<IResult> Save(AppDbContext db, Guid albumId, Guid fileId, AlbumPhotoTextWrite write, CancellationToken ct)
    {
        var error = await AlbumPhotoText.WriteAsync(db, albumId, fileId, write, ct);
        return error switch
        {
            null => Results.Ok(await AlbumPhotoText.ReadAsync(db, albumId, fileId, ct)),
            "not_found" => Results.NotFound(),
            "photo_changed" => Results.Conflict(new { error }),
            _ => Results.BadRequest(new { error }),
        };
    }
}

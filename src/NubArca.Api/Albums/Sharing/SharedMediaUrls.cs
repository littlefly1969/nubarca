using NubArca.Api.Files;

namespace NubArca.Api.Albums.Sharing;

// The one place shared-album media URLs are built.
//
// Every URL is ALBUM-SCOPED — the album id is a path segment, not a query
// parameter — because the album is what the caller's grant is attached to. A
// bare FileItemId is never enough: /api/files/{id}/thumbnail stays owner-only
// and unchanged, and these routes re-resolve the grant on every request.
//
// A URL is therefore a ROUTE, not a capability: it carries no token, no
// signature and no expiry, and pasting one into another browser session gets a
// 404 unless that session independently holds an accepted membership. That is
// also why they are safe to put in an <img src> — there is no bearer credential
// to leak through a Referer header, an analytics beacon, or a shared link.
public static class SharedMediaUrls
{
    private static string Base(Guid albumId, Guid fileItemId) =>
        $"/api/shared-albums/{albumId}/media/{fileItemId}";

    public static string Thumbnail(Guid albumId, Guid fileItemId) =>
        Base(albumId, fileItemId) + "/thumbnail";

    // The curation-list icon (ThumbnailSizes.Micro) on the same route. For a
    // video the route answers with an icon of its poster, so one URL shape
    // serves every row of the content manager.
    public static string MicroThumbnail(Guid albumId, Guid fileItemId) =>
        Thumbnail(albumId, fileItemId) + "?size=" + ThumbnailSizes.Micro;

    public static string Preview(Guid albumId, Guid fileItemId) =>
        Base(albumId, fileItemId) + "/preview";

    public static string Poster(Guid albumId, Guid fileItemId) =>
        Base(albumId, fileItemId) + "/poster";

    public static string Video(Guid albumId, Guid fileItemId) =>
        Base(albumId, fileItemId) + "/video";

    public static string Content(Guid albumId, Guid fileItemId) =>
        Base(albumId, fileItemId) + "/content";
}

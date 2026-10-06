using NubArca.Api.Files;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// A VIDEO OF AN ALBUM, PLAYED BY SOMEBODY WHO IS NOT ITS OWNER — a party
/// guest, or a visitor holding an album's link. The adaptive HLS ladder the
/// owner's own player uses, served under the surface's own token:
/// <c>…/media/{fileId}/video</c> is the master playlist (or 202 while the
/// ladder is prepared), and its rendition URIs resolve under
/// <c>…/media/{fileId}/video/{rendition}/{file}</c>.
///
/// <para>Who may watch is the CALLER's decision, made before it gets here: the
/// party's lifecycle, the share's grant and second factor. This only asks what
/// every public picture asks — is the file visible in that album, and is it a
/// video — and never hands over the original: a transcoded rendition is all a
/// public surface plays.</para>
/// </summary>
internal static class PublicAlbumVideo
{
    public static async Task<IResult> MasterAsync(
        Guid ownerUserId, Guid albumId, Guid fileId, HttpContext httpContext,
        IPartyMediaService media, VideoHlsServingService hlsServing, CancellationToken cancellationToken)
    {
        if (!hlsServing.Enabled) return Results.NotFound();
        var kind = await media.GetVisibleMediaKindAsync(ownerUserId, albumId, fileId, cancellationToken);
        if (kind != PartyMediaKind.Video) return Results.NotFound();
        var master = await hlsServing.GetMasterAsync(fileId, ownerUserId, cancellationToken);
        return master.Status switch
        {
            VideoHlsMasterStatus.Ready => Results.Text(
                master.MasterPlaylist!, VideoHlsServingService.MasterContentType),
            VideoHlsMasterStatus.Preparing =>
                VideoHlsServingService.Preparing(httpContext.Response),
            _ => Results.NotFound(),
        };
    }

    public static async Task<IResult> LadderFileAsync(
        Guid ownerUserId, Guid albumId, Guid fileId, string rendition, string file,
        IPartyMediaService media, VideoHlsServingService hlsServing, CancellationToken cancellationToken)
    {
        if (!hlsServing.Enabled) return Results.NotFound();
        var kind = await media.GetVisibleMediaKindAsync(ownerUserId, albumId, fileId, cancellationToken);
        if (kind != PartyMediaKind.Video) return Results.NotFound();
        var content = await hlsServing.OpenLadderFileAsync(
            fileId, ownerUserId, $"{rendition}/{file}", cancellationToken);
        return content is null
            ? Results.NotFound()
            : Results.File(content.Content, content.ContentType);
    }
}

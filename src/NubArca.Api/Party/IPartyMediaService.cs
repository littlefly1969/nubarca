namespace NubArca.Api.Party;

// Read-only, album-scoped media surfacing for a resolved party token. The
// (ownerUserId, albumId) pair is already validated by IPartyLinkService before
// any of these are called. Every query joins FileItems (Private-Vault global
// filter) so vaulted/vault-only files never appear, and re-checks owner + album
// membership so a file cannot be addressed through a token for a different
// album.
public interface IPartyMediaService
{
    // Album header (name + displayable item count). Null when the album is
    // missing/foreign/not-ShowOnTv (generic 404 upstream).
    Task<PartyAlbumHeader?> GetAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    // The album's displayable (image/video) members, oldest-added first. Ids +
    // media kind only; the endpoint builds token-scoped URLs.
    Task<IReadOnlyList<PartyMediaItem>?> ListItemsAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    // Is this file a displayable member of THIS party album (owner-owned,
    // active, non-vault)? Returns the media kind, or null when not visible.
    Task<PartyMediaKind?> GetVisibleMediaKindAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default);
}

public sealed record PartyAlbumHeader(string Name, int ItemCount);

public enum PartyMediaKind { Image, Video }

public sealed record PartyMediaItem(Guid FileItemId, PartyMediaKind Kind);

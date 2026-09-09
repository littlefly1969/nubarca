namespace NubArca.Api.Party;

// Read-only, album-scoped media surfacing for a resolved party token. The
// (ownerUserId, albumId) pair is the party's MAIN media source, already
// resolved and validated by IPartyLinkService before any of these are called —
// which is why nothing here knows what a Party is. Every query joins FileItems (Private-Vault global
// filter) so vaulted/vault-only files never appear, and re-checks owner + album
// membership so a file cannot be addressed through a token for a different
// album.
public interface IPartyMediaService
{
    // Album header (name + displayable item count). Null when the album is
    // missing or foreign (generic 404 upstream).
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

/// <summary>
/// The album as a party surface sees it.
///
/// <para><c>CoverFileItemId</c> is the album's cover the way every other surface
/// resolves it — the host's choice, or the first displayable member when they
/// made none. <c>ChosenCoverFileItemId</c> is only ever the CHOICE, and it is
/// what the invitation is allowed to show: before the party the whole gallery is
/// closed, and a photograph the host explicitly nominated to represent the album
/// is a different thing from whichever one happens to sort first.</para>
/// </summary>
public sealed record PartyAlbumHeader(
    string Name,
    int ItemCount,
    Guid? CoverFileItemId = null,
    Guid? ChosenCoverFileItemId = null);

public enum PartyMediaKind { Image, Video }

public sealed record PartyMediaItem(Guid FileItemId, PartyMediaKind Kind);

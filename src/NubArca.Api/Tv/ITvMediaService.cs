namespace NubArca.Api.Tv;

// Owner-private TV browsing over the ShowOnTv album allowlist. Every method is
// scoped to the resolved TV-session owner and re-checks the ShowOnTv flag, so
// disabling an album removes it from the TV on the next call. Private Vault is
// excluded automatically by the FileItem global query filter.
public interface ITvMediaService
{
    // Albums the owner has enabled for TV (ShowOnTv = true), with a display item
    // count and an optional cover thumbnail URL. Never returns other owners' data.
    Task<IReadOnlyList<TvAlbumDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Display items of one allowlisted album. Returns null (→ 404) when the album
    // is missing, foreign, or not currently enabled for TV.
    Task<TvAlbumItemsDto?> ListItemsAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    // True only when the file belongs to the owner, is active/non-vault, and is a
    // member of at least one of the owner's ShowOnTv albums. Gate for media bytes.
    Task<bool> IsMediaVisibleAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);
}

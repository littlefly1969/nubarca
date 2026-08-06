namespace NubArca.Api.Albums;

public interface IAlbumService
{
    Task<AlbumDetail> CreateAsync(
        Guid ownerUserId, string name, string? description,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlbumSummary>> ListAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<AlbumDetail?> GetByIdAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default);

    // Returns null when the album is missing/foreign.
    Task<AlbumDetail?> UpdateAsync(
        Guid albumId, Guid ownerUserId, string name, string? description,
        CancellationToken cancellationToken = default);

    // Owner-scoped toggle of the TV allowlist flag. Returns the updated album,
    // or null when the album is missing/foreign.
    Task<AlbumDetail?> SetTvVisibilityAsync(
        Guid albumId, Guid ownerUserId, bool showOnTv,
        CancellationToken cancellationToken = default);

    // Returns false when missing/foreign.
    Task<bool> DeleteAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlbumItemSummary>?> ListItemsAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default);

    // Idempotent: adding a file already in the album returns true without error.
    // Returns false when album or file is missing/foreign.
    Task<bool> AddItemAsync(
        Guid albumId, Guid ownerUserId, Guid fileItemId,
        CancellationToken cancellationToken = default);

    // Returns false when the album is missing/foreign (membership may or may not exist).
    Task<bool> RemoveItemAsync(
        Guid albumId, Guid ownerUserId, Guid fileItemId,
        CancellationToken cancellationToken = default);

    // Bulk add. Idempotent: files already in the album (or duplicated in the
    // request) count as skipped, not errors. Only the owner's own active files
    // are added; foreign/missing ids are silently skipped (no existence leak).
    // Returns null when the album is missing/foreign.
    Task<BulkAlbumItemsResult?> AddItemsAsync(
        Guid albumId, Guid ownerUserId, IReadOnlyList<Guid> fileItemIds,
        CancellationToken cancellationToken = default);

    // Bulk remove (album membership only — never deletes FileItems/blobs).
    // Idempotent: ids not currently members count as skipped. Returns null when
    // the album is missing/foreign.
    Task<BulkAlbumItemsResult?> RemoveItemsAsync(
        Guid albumId, Guid ownerUserId, IReadOnlyList<Guid> fileItemIds,
        CancellationToken cancellationToken = default);
}

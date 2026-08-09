using NubArca.Api.Media;

namespace NubArca.Api.Tv;

// Page XOR failure: on anything but Ok the endpoint maps `Status` to a single
// HTTP code and returns `Error` verbatim (it is already client-safe, built by
// the shared query service).
public sealed record TvPersonalMediaListResult(
    TvPersonalMediaPageDto? Page,
    MediaCollectionStatus Status,
    string? Error);

public interface ITvPersonalMediaService
{
    // The query is bound by the SHARED MediaCollectionQueryBinder before it gets
    // here, so the TV cannot express a filter combination the web could not.
    Task<TvPersonalMediaListResult> QueryAsync(
        Guid ownerUserId, MediaCollectionQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TvPersonalAlbumCardDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Null for a foreign or missing album — the endpoint answers a generic 404
    // so album existence never leaks across owners.
    Task<TvPersonalAlbumCardDto?> GetAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);
}

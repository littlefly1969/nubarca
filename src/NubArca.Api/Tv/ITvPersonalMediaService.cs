using NubArca.Api.Media;

namespace NubArca.Api.Tv;

// Page XOR failure: on anything but Ok the endpoint maps `Status` to a single
// HTTP code and returns `Error` verbatim (it is already client-safe, built by
// the shared query service).
public sealed record TvPersonalMediaListResult(
    TvPersonalMediaPageDto? Page,
    MediaCollectionStatus Status,
    string? Error);

// Page XOR unavailability. `Available=false` carries only a sanitized reason
// token — never a provider name, model name, stack trace or vector.
public sealed record TvPersonalMediaSemanticResult(
    TvPersonalMediaPageDto? Page,
    bool Available,
    string? UnavailableReason,
    bool StillIndexing);

public interface ITvPersonalMediaService
{
    // The query is bound by the SHARED MediaCollectionQueryBinder before it gets
    // here, so the TV cannot express a filter combination the web could not.
    Task<TvPersonalMediaListResult> QueryAsync(
        Guid ownerUserId, MediaCollectionQuery query,
        CancellationToken cancellationToken = default);

    // SEMANTIC retrieval for the same workspace. It is a separate method rather
    // than a flag on QueryAsync because it is a different canonical service with
    // its own relevance cursor — not a filter on the structural list. What it
    // shares is the PROJECTION: the results are the same MediaItem values, so a
    // semantic card is indistinguishable from an ordinary one in the same grid.
    //
    // `Unavailable` is a first-class outcome, never an empty page: a semantic
    // search that cannot run must say so rather than look like "no matches",
    // and must never degrade into substring search.
    Task<TvPersonalMediaSemanticResult> SearchSemanticAsync(
        Guid ownerUserId,
        string query,
        NubArca.Api.Files.MediaKindScope kind,
        int limit,
        string? cursor,
        NubArca.Api.Files.ImageFilters filters,
        // The album the workspace is browsing, ALREADY owner-validated by the
        // endpoint. Only the photo route can honour it — the unified media
        // semantic route is library-scoped — so the panel does not offer a
        // visual query on the other tabs inside an album.
        Guid? albumId,
        int semanticTopK,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TvPersonalAlbumCardDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Null for a foreign or missing album — the endpoint answers a generic 404
    // so album existence never leaks across owners.
    Task<TvPersonalAlbumCardDto?> GetAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);
}

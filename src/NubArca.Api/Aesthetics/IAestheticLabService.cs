namespace NubArca.Api.Aesthetics;

// Owner-private lifecycle of Aesthetics Lab items: add from a gallery FileItem
// (acquire a blob reference, no byte copy), add by direct upload (reuse the
// bounded hash/dedup store), list, detail, remove (release references + purge
// analysis data), and serve derived-only renditions. NEVER touches Gallery /
// Files / the source file tree beyond reading provenance.
public interface IAestheticLabService
{
    Task<AestheticLabItemDto> AddFromGalleryAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);

    Task<AestheticLabItemDto> AddFromUploadAsync(
        Guid ownerUserId, string? fileName, string? clientContentType, Stream content,
        CancellationToken cancellationToken = default);

    Task<AestheticLabPageDto> ListAsync(
        Guid ownerUserId, string? cursor, int limit, CancellationToken cancellationToken = default);

    Task<AestheticLabItemDetailDto?> GetDetailAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // Serve a derived small/medium rendition (persists the derivative row on
    // first request so its blob reference is accounted). Null for an unknown
    // size or missing/foreign item.
    Task<AestheticDerivativeContent?> RenderDerivativeAsync(
        Guid ownerUserId, Guid id, string size, CancellationToken cancellationToken = default);
}

public sealed record AestheticDerivativeContent(byte[] Content, string ContentType);

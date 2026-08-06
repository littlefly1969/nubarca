namespace NubArca.Api.Plates.Redaction;

// Owner-private service that resolves a face-redacted plate media rendition for
// serving, backed by a derived-media cache so redaction is not recomputed every
// request. Every method is owner-scoped; a foreign/missing image resolves to
// null so the endpoint returns a generic 404. It NEVER silently returns the
// unredacted image: when redaction is unavailable it throws
// PlateFaceRedactionUnavailableException (→ 409), and an oversized source throws
// PlateRedactionImageTooLargeException (→ 413).
public interface IPlateRedactedMediaService
{
    Task<PlateRedactedContent?> GetAsync(
        Guid ownerUserId,
        Guid plateImageId,
        PlateRedactionSourceKind kind,
        CancellationToken cancellationToken = default);
}

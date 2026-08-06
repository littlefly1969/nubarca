using NubArca.Api.Domain;

namespace NubArca.Api.Storage;

public interface IBlobService
{
    Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default);

    // Slice 82: same as StoreAsync but also returns per-phase ingest timings
    // (read / hash / write / blob-DB) for the admin import diagnostics. The
    // normal upload path keeps using StoreAsync and pays no extra cost beyond
    // the negligible always-on Stopwatch reads in the write loop.
    Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default);

    Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default);

    // Slice 72: store DERIVED media bytes (thumbnails / previews / posters)
    // into the derived storage root (Storage:DerivedRootPath, or the original
    // root when unset). Shares the BlobObject dedup/refcount table with
    // originals; the physical bytes always land in the derived root. Used only
    // by FileThumbnailService — never the upload path.
    Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default);

    // Slice 72: open a derived artifact's bytes from the derived storage root.
    // Returns null when the physical bytes are absent there (e.g. the derived
    // cache was wiped, or a pre-slice-72 artifact still lives only in the
    // original root) so the caller can regenerate. This is NOT a corruption
    // signal — derived artifacts are regenerable cache, not source data.
    Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default);

    // Atomically decrements ReferenceCount when it is > 0. When this releases
    // the final owner, starts the janitor grace window at the current time.
    // No-op when the blob is missing or already at 0. Never lets the count go
    // negative and never removes physical bytes itself.
    Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default);

    // Starts (or restarts) the janitor grace window for a zero-reference blob
    // whose retained owner row has just been hard-deleted. This is used by the
    // Trash sweeper/permanent-delete path: soft-delete releases the active
    // refcount but deliberately does not make restorable bytes purge-eligible.
    Task MarkPurgeEligibleIfUnreferencedAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken = default);

    // Acquires ONE additional reference to an EXISTING blob by id, WITHOUT
    // copying or re-hashing any bytes (atomic ReferenceCount++). Used when a new
    // owner-row references bytes already in the store — e.g. adding a gallery
    // image to the Aesthetics Lab reuses the FileItem's blob. Returns the blob;
    // throws InvalidOperationException if the blob id does not exist (the caller
    // must have verified it inside the same transaction). Pairs with
    // ReleaseAsync on every failure/removal path.
    Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default);

    // Slice 96: cheap placement repair for a derived artifact whose bytes are
    // absent from the derived root but still present in the original root
    // (pre-split-root artifact). Streams the bytes across (re-hashed, temp
    // file + atomic rename) into the derived root — no decode, no DB mutation.
    // Returns true when the derived bytes are present afterwards (including
    // "another request already restored them"); false when the original root
    // doesn't have them either (caller falls back to regeneration) or the
    // source bytes no longer hash to their storage key (corrupt source).
    Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default);
}

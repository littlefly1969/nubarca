namespace NubArca.Api.Storage;

public interface IBlobStorage
{
    Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);

    // Removes the physical blob at `storageKey`. Idempotent: a missing file is
    // not an error. Validates the storage key with the same regex + path-
    // traversal defence as OpenReadAsync, so a malformed key throws before any
    // filesystem call. Never deletes anything outside the configured root.
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    // Slice 65: enumerates every physical object currently present under the
    // storage root, as well-formed storage keys ("objects/{a}/{b}/{sha256}").
    // Used only by the operator reconciliation CLI to find on-disk objects
    // with no BlobObject row. Files that do not match the sharded storage-key
    // shape are skipped (they are not blobs this layer wrote).
    IAsyncEnumerable<string> EnumerateStorageKeysAsync(CancellationToken cancellationToken = default);
}

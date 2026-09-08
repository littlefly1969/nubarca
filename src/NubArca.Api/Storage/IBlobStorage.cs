namespace NubArca.Api.Storage;

public interface IBlobStorage
{
    // Read + hash + write the bytes into the store's temp area WITHOUT
    // publishing them under their content-addressed key. Safe to call outside a
    // transaction and expected to be slow for large inputs.
    //
    // The returned stage carries the content identity, which is what a caller
    // locks on before deciding to publish or reuse. Dispose it to abandon the
    // bytes; publishing consumes it.
    Task<StagedBlobWrite> StageAsync(Stream content, CancellationToken cancellationToken = default);

    // Make staged bytes visible under their content-addressed key, or observe
    // that identical content is already published and reuse it.
    //
    // MUST be called while holding a SHARED StorageMutationLock on
    // `staged.Sha256`, inside the same transaction that will commit ownership.
    // This call embeds the reuse decision — "the object is already there, I
    // will not write" — and that decision is only safe if a purge of the same
    // content cannot run between it and the ownership commit.
    Task<BlobWriteResult> PublishAsync(
        StagedBlobWrite staged, CancellationToken cancellationToken = default);

    // Stage and publish in one step, WITHOUT the protection above.
    //
    // Only for callers that establish no durable ownership of the result. Any
    // caller that will record the storage key somewhere must use
    // StageAsync/PublishAsync so the publish decision and the ownership commit
    // share one locked transaction.
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

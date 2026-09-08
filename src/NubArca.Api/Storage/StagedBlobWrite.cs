namespace NubArca.Api.Storage;

// Bytes that are fully written and hashed but NOT yet visible under their
// content-addressed key.
//
// This type exists to give writers a place to stand between "I know the
// content identity" and "I have decided whether to publish or reuse". Hashing
// a multi-gigabyte upload can take a long time and must NOT happen inside a
// database transaction; the publish/reuse decision is a single filesystem
// operation and MUST happen inside one, under a shared StorageMutationLock, so
// a concurrent purge of the same content cannot slip between that decision and
// the commit of ownership.
//
// Staging writes to the store's temp area, so an abandoned stage costs a temp
// file and nothing else — no content-addressed key is ever occupied by bytes
// nobody owns. Dispose removes the temp file when the stage was never
// published; publishing consumes it.
public sealed class StagedBlobWrite : IAsyncDisposable
{
    private readonly Func<StagedBlobWrite, ValueTask> _discard;
    private bool _consumed;

    public StagedBlobWrite(
        string sha256,
        string storageKey,
        long sizeBytes,
        string stagedPath,
        Func<StagedBlobWrite, ValueTask> discard,
        long readMillis = 0,
        long hashMillis = 0,
        long writeMillis = 0)
    {
        Sha256 = sha256;
        StorageKey = storageKey;
        SizeBytes = sizeBytes;
        StagedPath = stagedPath;
        _discard = discard;
        ReadMillis = readMillis;
        HashMillis = hashMillis;
        WriteMillis = writeMillis;
    }

    // The content identity. This is the StorageMutationLock key.
    public string Sha256 { get; }

    // Where the bytes WILL live once published.
    public string StorageKey { get; }

    public long SizeBytes { get; }

    // Internal placement of the staged bytes. Never logged, never surfaced.
    internal string StagedPath { get; }

    public long ReadMillis { get; }
    public long HashMillis { get; }
    public long WriteMillis { get; }

    // Set once the stage has been published (or deliberately dropped), so
    // Dispose knows there is nothing left to clean up.
    internal void MarkConsumed() => _consumed = true;

    public async ValueTask DisposeAsync()
    {
        if (_consumed)
        {
            return;
        }
        _consumed = true;
        await _discard(this);
    }
}

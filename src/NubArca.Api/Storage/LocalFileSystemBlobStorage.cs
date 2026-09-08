using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Storage;

// Not sealed: slice 72's DerivedFsBlobStorage subclasses this to provide a
// second, independently-rooted store for derived media artifacts.
public partial class LocalFileSystemBlobStorage : IBlobStorage
{
    private const int BufferSize = 81920;

    private static readonly Regex StorageKeyPattern = StorageKeyRegex();

    private readonly string _rootPath;
    private readonly string _objectsRoot;
    private readonly string _tempRoot;
    private readonly long _maxUploadBytes;

    public LocalFileSystemBlobStorage(IOptions<BlobStorageOptions> options)
        : this(options.Value.RootPath, options.Value.MaxUploadBytes)
    {
    }

    // Slice 72: explicit-root ctor so a second store (derived media) can be
    // built at a different path from the same options.
    public LocalFileSystemBlobStorage(string rootPath, long maxUploadBytes)
    {
        _maxUploadBytes = maxUploadBytes;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath is not configured. Set it via appsettings or the ConnectionStrings__Postgres-style environment variable Storage__RootPath.");
        }

        _rootPath = Path.GetFullPath(rootPath);
        _objectsRoot = Path.Combine(_rootPath, "objects");
        _tempRoot = Path.Combine(_rootPath, "tmp");

        Directory.CreateDirectory(_objectsRoot);
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task<StagedBlobWrite> StageAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Slice 72: re-ensure the temp dir exists. The ctor creates it once,
        // but the derived store is a regenerable cache that an operator may
        // wipe at runtime (e.g. `rm -rf` the derived root); recreating here
        // (idempotent, cheap) lets the next write/regenerate succeed instead
        // of failing on a missing temp directory.
        Directory.CreateDirectory(_tempRoot);

        var tempPath = Path.Combine(_tempRoot, $"{Guid.NewGuid():N}.part");
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;

        // Slice 82: accumulate per-phase wall-clock (raw Stopwatch ticks) so the
        // import diagnostics can split copy-read / SHA / copy-write. Per-chunk
        // GetTimestamp() calls are nanosecond-cheap relative to the I/O.
        long readTicks = 0, hashTicks = 0, writeTicks = 0;
        static long ToMs(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

        try
        {
            await using (var temp = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var tRead = Stopwatch.GetTimestamp();
                        int read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                        readTicks += Stopwatch.GetTimestamp() - tRead;
                        if (read <= 0) break;

                        size += read;
                        // Slice 65: enforce the app-level upload ceiling WHILE
                        // streaming, so an oversized upload is refused after a
                        // single buffer over the limit rather than after the
                        // whole (possibly multi-GiB) payload lands on disk. The
                        // outer catch deletes the partial temp file.
                        if (_maxUploadBytes > 0 && size > _maxUploadBytes)
                        {
                            throw new UploadTooLargeException(_maxUploadBytes);
                        }

                        var tHash = Stopwatch.GetTimestamp();
                        hasher.AppendData(buffer, 0, read);
                        hashTicks += Stopwatch.GetTimestamp() - tHash;

                        var tWrite = Stopwatch.GetTimestamp();
                        await temp.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        writeTicks += Stopwatch.GetTimestamp() - tWrite;
                    }

                    var tFlush = Stopwatch.GetTimestamp();
                    await temp.FlushAsync(cancellationToken);
                    writeTicks += Stopwatch.GetTimestamp() - tFlush;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            var storageKey = $"objects/{hash[..2]}/{hash[2..4]}/{hash}";

            // STOP HERE. The bytes are complete and their identity is known,
            // but they are NOT yet under their content-addressed key. The
            // publish/reuse decision belongs to PublishAsync, which the caller
            // makes under a shared StorageMutationLock in the same transaction
            // that commits ownership.
            return new StagedBlobWrite(
                hash,
                storageKey,
                size,
                tempPath,
                static staged =>
                {
                    TryDelete(staged.StagedPath);
                    return ValueTask.CompletedTask;
                },
                ToMs(readTicks),
                ToMs(hashTicks),
                ToMs(writeTicks));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task<BlobWriteResult> PublishAsync(
        StagedBlobWrite staged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        cancellationToken.ThrowIfCancellationRequested();

        var hash = staged.Sha256;
        var finalPath = Path.Combine(_objectsRoot, hash[..2], hash[2..4], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        BlobWriteResult Result(bool existed)
        {
            staged.MarkConsumed();
            return new BlobWriteResult(
                hash, staged.StorageKey, staged.SizeBytes, existed,
                staged.ReadMillis, staged.HashMillis, staged.WriteMillis);
        }

        // The reuse decision. Only sound because the caller holds a shared
        // StorageMutationLock on this content: an exclusive purge cannot be
        // between this check and the caller's ownership commit.
        if (File.Exists(finalPath))
        {
            TryDelete(staged.StagedPath);
            return Task.FromResult(Result(existed: true));
        }

        try
        {
            File.Move(staged.StagedPath, finalPath, overwrite: false);
            return Task.FromResult(Result(existed: false));
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Another writer published the same content first. Same outcome.
            TryDelete(staged.StagedPath);
            return Task.FromResult(Result(existed: true));
        }
    }

    public async Task<BlobWriteResult> WriteAsync(
        Stream content, CancellationToken cancellationToken = default)
    {
        var staged = await StageAsync(content, cancellationToken);
        await using (staged.ConfigureAwait(false))
        {
            return await PublishAsync(staged, cancellationToken);
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveAndValidate(storageKey);
        cancellationToken.ThrowIfCancellationRequested();

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveAndValidate(storageKey);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveAndValidate(storageKey);
        cancellationToken.ThrowIfCancellationRequested();
        // File.Delete is idempotent only for missing files within an existing
        // directory. For a never-stored storage key the {first2}/{next2}
        // sharding directory may not exist, which raises
        // DirectoryNotFoundException. We treat that the same as "file already
        // gone" — both are the desired post-condition of DeleteAsync.
        try
        {
            File.Delete(fullPath);
        }
        catch (DirectoryNotFoundException)
        {
        }
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLastWriteTimeUtcAsync(
        string storageKey, CancellationToken cancellationToken = default)
    {
        // Same validation/traversal defence as every other key-taking method:
        // a malformed key throws before any filesystem call.
        var path = ResolveAndValidate(storageKey);
        var info = new FileInfo(path);
        // WriteAsync stages into a temp file and renames it into place, and a
        // rename carries the temp file's write time across, so this is the
        // moment the bytes actually materialised under this key. A dedup hit
        // leaves the existing file untouched — correct, since that object
        // already has its owning row.
        return Task.FromResult<DateTimeOffset?>(
            info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null);
    }

    public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_objectsRoot))
        {
            yield break;
        }

        // Walk objects/{first2}/{next2}/{sha256}. We rebuild the storage key
        // from the path and re-validate against the same regex so only
        // genuine blob files are yielded; stray/partial files are skipped.
        foreach (var file in Directory.EnumerateFiles(_objectsRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(_rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
            if (StorageKeyPattern.IsMatch(relative))
            {
                yield return relative;
            }

            await Task.Yield();
        }
    }

    private string ResolveAndValidate(string storageKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);

        var match = StorageKeyPattern.Match(storageKey);
        if (!match.Success)
        {
            throw new ArgumentException($"Malformed storage key: '{storageKey}'.", nameof(storageKey));
        }

        var first2 = match.Groups[1].Value;
        var next2 = match.Groups[2].Value;
        var sha = match.Groups[3].Value;

        if (!sha.StartsWith(first2 + next2, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Storage key directory prefix does not match its sha256: '{storageKey}'.",
                nameof(storageKey));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Storage key escapes the storage root: '{storageKey}'.", nameof(storageKey));
        }

        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; do not mask the original exception.
        }
    }

    [GeneratedRegex("^objects/([0-9a-f]{2})/([0-9a-f]{2})/([0-9a-f]{64})$")]
    private static partial Regex StorageKeyRegex();
}

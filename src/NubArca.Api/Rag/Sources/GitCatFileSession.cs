using System.Diagnostics;
using System.Text;

namespace NubArca.Api.Rag.Sources;

/// One long-lived `git cat-file --batch`, fed object ids.
///
/// The alternative — `git show <rev>:<path>` per file — is one process per
/// blob, and this repository has two thousand of them. `--batch` is the plumbing
/// designed for exactly this: write an object id, read its header and its bytes.
///
/// Everything here is BYTES rather than text. A `StreamReader` would decode the
/// blob as it arrived, so a file that is not valid UTF-8 would come back full of
/// replacement characters instead of being recognised as binary and refused, and
/// the byte count in the header would no longer match what was read.
internal sealed class GitCatFileSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GitCatFileSession(Process process)
    {
        _process = process;
        _output = process.StandardOutput.BaseStream;
    }

    public static GitCatFileSession Start(string root)
    {
        var info = GitRepositorySnapshotReader.GitStartInfo(root, new[] { "cat-file", "--batch" });
        var process = Process.Start(info)
            ?? throw new RepositorySnapshotUnavailableException("git-unavailable");

        // stderr is drained on a background read and discarded: it names
        // filesystem paths, and leaving it unread would eventually block git on
        // a full pipe halfway through a large index run.
        _ = process.StandardError.ReadToEndAsync();
        return new GitCatFileSession(process);
    }

    /// The bytes of one object, or null when git does not have it.
    ///
    /// Serialized on a gate because the protocol is a single request/response
    /// stream: two concurrent reads would interleave their headers and each
    /// would return the other's bytes.
    public async Task<byte[]?> ReadAsync(string objectId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteAsync($"{objectId}\n".AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);

            var header = await ReadHeaderAsync(cancellationToken);
            if (header is null) throw new RepositorySnapshotUnavailableException("cat-file-closed");

            // "<oid> missing" — the only single-token response, and never
            // expected here because every id came from a tree git just read.
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            if (!long.TryParse(parts[2], out var size) || size < 0)
            {
                throw new RepositorySnapshotUnavailableException("cat-file-malformed");
            }

            var content = await ReadExactlyAsync(size, cancellationToken);
            // `--batch` writes a trailing newline after the object's bytes; it
            // is a framing artefact and is not part of the blob.
            await ReadExactlyAsync(1, cancellationToken);
            return content;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Byte-at-a-time up to the newline. The header is a few dozen bytes, and
    /// buffering ahead would consume the blob that follows it.
    private async Task<string?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(64);
        var one = new byte[1];
        while (true)
        {
            var read = await _output.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (one[0] == (byte)'\n') return builder.ToString();
            builder.Append((char)one[0]);
            if (builder.Length > 4096)
            {
                throw new RepositorySnapshotUnavailableException("cat-file-malformed");
            }
        }
    }

    private async Task<byte[]> ReadExactlyAsync(long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await _output.ReadAsync(
                buffer.AsMemory(offset, (int)(count - offset)), cancellationToken);
            if (read == 0) throw new RepositorySnapshotUnavailableException("cat-file-truncated");
            offset += read;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Closing stdin is how `--batch` is asked to stop; killing it first
            // would orphan a process on every index run.
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SystemException)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        finally
        {
            _process.Dispose();
            _gate.Dispose();
        }
    }
}

/// A revision's entries plus an open reader for their bytes.
internal sealed class GitRepositorySnapshot : IRepositorySnapshot
{
    private readonly GitCatFileSession _session;

    public GitRepositorySnapshot(
        string root, string revision,
        IReadOnlyList<RepositorySnapshotEntry> entries,
        GitCatFileSession session)
    {
        Root = root;
        Revision = revision;
        Entries = entries;
        _session = session;
    }

    public string Root { get; }

    public string Revision { get; }

    public IReadOnlyList<RepositorySnapshotEntry> Entries { get; }

    public async Task<byte[]> ReadAsync(
        RepositorySnapshotEntry entry, CancellationToken cancellationToken = default)
    {
        // A symlink's blob is its TARGET PATH. Reading it here would be
        // harmless, and refusing at the only place that can read bytes is what
        // makes "symlinks are never dereferenced" a property of the reader
        // rather than a rule some future caller has to remember.
        if (entry.IsSymbolicLink || entry.IsSubmodule)
        {
            throw new InvalidOperationException(
                $"Refusing to read a {(entry.IsSymbolicLink ? "symbolic link" : "submodule")} entry as content.");
        }

        return await _session.ReadAsync(entry.ObjectId, cancellationToken)
               ?? throw new RepositorySnapshotUnavailableException("object-missing");
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

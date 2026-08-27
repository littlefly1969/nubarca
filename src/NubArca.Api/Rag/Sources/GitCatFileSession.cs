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
///
/// THE STREAM IS A SINGLE CONVERSATION, and that is what shapes the failure
/// handling. A request writes one object id and the response is a header plus
/// exactly that many bytes plus a newline. Anything that stops a read part-way —
/// a timeout, a refusal to allocate, a cancelled run — leaves those bytes queued,
/// so the NEXT read would parse blob content as a header and every subsequent
/// object would come back as somebody else's. There is no way to resynchronise
/// without consuming what is left, and consuming it is exactly the work being
/// refused. So a session that stops mid-response is DEAD: it is faulted, the
/// process is killed, and every later call fails immediately rather than
/// returning the wrong file's bytes.
internal sealed class GitCatFileSession : IAsyncDisposable
{
    /// A hard allocation ceiling, read from the header BEFORE `new byte[size]`.
    ///
    /// Deliberately not the source policy's limit: this is plumbing, and the
    /// bound it owes its callers is "one object cannot exhaust this process's
    /// memory", not "this object is indexable". The provider refuses anything
    /// over RepositorySourcePolicy.MaximumBytes before asking, so reaching this
    /// means a caller that did not check — and a tracked multi-gigabyte blob
    /// would otherwise be allocated in full on the strength of a number git
    /// printed.
    public const long MaximumObjectBytes = 8L * 1024 * 1024;

    /// One object's worth of local pipe traffic. Generous — this is a process on
    /// the same machine reading its own object store — and bounded, because a
    /// `--batch` that stops answering would otherwise hang an index run forever
    /// with no reason code and no way out.
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly Stream _output;
    private readonly TimeSpan _readTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// Set once, by whichever read stopped mid-response. Null while healthy.
    private string? _faultReason;

    private GitCatFileSession(Process process, TimeSpan readTimeout)
    {
        _process = process;
        _output = process.StandardOutput.BaseStream;
        _readTimeout = readTimeout;
    }

    public static GitCatFileSession Start(string root) => Start(root, DefaultReadTimeout);

    internal static GitCatFileSession Start(string root, TimeSpan readTimeout)
        => StartProcess(
            GitRepositorySnapshotReader.GitStartInfo(root, new[] { "cat-file", "--batch" }),
            readTimeout);

    /// The process behind the session, supplied rather than assumed.
    ///
    /// A timeout and a half-consumed response are the two things this class
    /// exists to survive, and neither can be provoked from a healthy `git
    /// cat-file`: git answers correctly and fast, which is the problem. A test
    /// supplies a process that stalls, or one that announces a four-gigabyte
    /// object, and the behaviour under test becomes observable instead of
    /// argued for.
    internal static GitCatFileSession StartProcess(ProcessStartInfo info, TimeSpan readTimeout)
    {
        var process = Process.Start(info)
            ?? throw new RepositorySnapshotUnavailableException("git-unavailable");

        // stderr is drained on a background read and discarded: it names
        // filesystem paths, and leaving it unread would eventually block git on
        // a full pipe halfway through a large index run.
        _ = process.StandardError.ReadToEndAsync();
        return new GitCatFileSession(process, readTimeout);
    }

    /// True once this session has stopped mid-response and may no longer be used.
    public bool IsFaulted => _faultReason is not null;

    /// The bytes of one object, or null when git does not have it.
    ///
    /// Serialized on a gate because the protocol is a single request/response
    /// stream: two concurrent reads would interleave their headers and each
    /// would return the other's bytes.
    public async Task<byte[]?> ReadAsync(string objectId, CancellationToken cancellationToken = default)
    {
        // A dead session answers nothing. Checked before the gate so a queue of
        // waiting callers fails fast rather than one at a time.
        if (_faultReason is { } reason)
        {
            throw new RepositorySnapshotUnavailableException(reason);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_faultReason is { } raced)
            {
                throw new RepositorySnapshotUnavailableException(raced);
            }

            // The whole exchange is raced against the timeout as ONE operation.
            // Timing each stream read separately would let a source that dribbles
            // a byte at a time stay under the limit forever.
            //
            // The timer is linked to the caller's token and cancelled on the way
            // out, so a completed read does not leave a 30-second timer alive.
            // Twenty thousand blobs is twenty thousand of them otherwise.
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exchange = ExchangeAsync(objectId, cancellationToken);
            var expiry = Task.Delay(_readTimeout, timer.Token);
            var completed = await Task.WhenAny(exchange, expiry);

            if (completed != exchange)
            {
                // A CANCELLED TIMER IS NOT AN EXPIRED ONE. `Task.Delay` linked
                // to the caller's token completes the moment the run is
                // cancelled, so reading "the delay won" as "git was too slow"
                // reported every cancelled index as a repository timeout — a
                // permanent-looking failure for something the operator did on
                // purpose.
                if (cancellationToken.IsCancellationRequested)
                {
                    Observe(exchange);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // TIMEOUT. Killing the process is what unblocks the abandoned
                // read; its exception is observed and discarded rather than left
                // to surface later as an unhandled task fault.
                Fault("git-object-read-timeout");
                Observe(exchange);
                throw new RepositorySnapshotUnavailableException("git-object-read-timeout");
            }

            timer.Cancel();
            Observe(expiry);
            return await exchange;
        }
        catch (OperationCanceledException)
        {
            // CANCELLATION IS NOT A TIMEOUT. The operator stopped the run, which
            // is not a fact about git and gets no reason code — but the response
            // is just as half-consumed, so the session dies either way.
            Fault("cat-file-cancelled");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]?> ExchangeAsync(string objectId, CancellationToken cancellationToken)
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

        // BOUND BEFORE ALLOCATE. `size` is a number a subprocess printed, and
        // `new byte[size]` on the strength of it is the whole exposure: a
        // tracked 4 GB blob is an OutOfMemoryException in a service, from a file
        // nothing was ever going to index. The response cannot be skipped past —
        // discarding it means reading it — so the session dies here.
        if (size > MaximumObjectBytes)
        {
            Fault("git-object-too-large");
            throw new RepositorySnapshotUnavailableException("git-object-too-large");
        }

        var content = await ReadExactlyAsync(size, cancellationToken);
        // `--batch` writes a trailing newline after the object's bytes; it
        // is a framing artefact and is not part of the blob.
        await ReadExactlyAsync(1, cancellationToken);
        return content;
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

    /// Mark the session unusable and stop the process behind it.
    ///
    /// Kill rather than a polite stdin close: the point is to unblock whatever
    /// is still waiting on the pipe, and a `--batch` mid-response is not going to
    /// notice a closed stdin until it has finished writing the object nobody is
    /// going to read.
    private void Fault(string reason)
    {
        _faultReason ??= reason;
        try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// Swallow the outcome of a read nobody is waiting for any more. Without
    /// this, killing the process turns the abandoned task into an unobserved
    /// exception.
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static t => _ = t.Exception, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A faulted session's process was already killed; asking it to exit
            // politely would just wait out the timeout for nothing.
            if (_faultReason is null)
            {
                // Closing stdin is how `--batch` is asked to stop; killing it
                // first would orphan a process on every index run.
                _process.StandardInput.Close();
                await _process.WaitForExitAsync(
                    new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
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

        // The size the TREE reported, checked before a single byte is requested.
        // The header bound inside the session is the backstop for a caller that
        // did not check; this is the one that costs nothing, because ls-tree
        // already told us and refusing here means the object is never even asked
        // for.
        if (entry.Size > GitCatFileSession.MaximumObjectBytes)
        {
            throw new RepositorySnapshotUnavailableException("git-object-too-large");
        }

        return await _session.ReadAsync(entry.ObjectId, cancellationToken)
               ?? throw new RepositorySnapshotUnavailableException("object-missing");
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

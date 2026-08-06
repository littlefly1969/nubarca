using System.Diagnostics;

namespace NubArca.Api.Admin;

// Slice 83: low-impact throttling for admin server-side imports. All knobs are
// off by default except a cooperative scheduler yield every N files. Used ONLY
// by the import job — normal uploads never touch this.
public sealed class ImportThrottle
{
    private readonly int _delayMs;
    private readonly long _maxBytesPerSecond;
    private readonly int _yieldEvery;
    private readonly TimeProvider _clock;

    public ImportThrottle(AdminImportOptions options, TimeProvider clock)
    {
        _delayMs = Math.Max(0, options.DelayBetweenFilesMs);
        _maxBytesPerSecond = Math.Max(0, options.MaxBytesPerSecond);
        _yieldEvery = Math.Max(1, options.YieldEveryFiles);
        _clock = clock;
    }

    public bool IsRateLimited => _maxBytesPerSecond > 0;
    public int YieldEveryFiles => _yieldEvery;

    // Wrap a source stream so reads are byte-rate limited; pass-through when no
    // limit is configured (so we don't add overhead to the common case).
    public Stream Wrap(Stream source)
        => _maxBytesPerSecond > 0 ? new ThrottledReadStream(source, _maxBytesPerSecond, _clock) : source;

    // Inter-file delay so the import doesn't monopolise CPU/I-O.
    public Task BetweenFilesAsync(CancellationToken cancellationToken)
        => _delayMs > 0 ? Task.Delay(_delayMs, cancellationToken) : Task.CompletedTask;

    // True at the yield/flush cadence (every YieldEveryFiles files).
    public bool ShouldYield(int filesProcessed)
        => filesProcessed > 0 && filesProcessed % _yieldEvery == 0;
}

// Read-only stream decorator that paces ReadAsync to a maximum byte rate by
// sleeping when the cumulative throughput runs ahead of the target. Keeps the
// streaming contract — no buffering of the whole file.
public sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytesPerSecond;
    private readonly TimeProvider _clock;
    private readonly long _startTimestamp;
    private long _bytesRead;

    public ThrottledReadStream(Stream inner, long maxBytesPerSecond, TimeProvider clock)
    {
        _inner = inner;
        _maxBytesPerSecond = maxBytesPerSecond;
        _clock = clock;
        _startTimestamp = clock.GetTimestamp();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        await PaceAsync(n, cancellationToken);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        await PaceAsync(n, cancellationToken);
        return n;
    }

    private async Task PaceAsync(int bytesJustRead, CancellationToken cancellationToken)
    {
        if (bytesJustRead <= 0 || _maxBytesPerSecond <= 0) return;
        _bytesRead += bytesJustRead;
        var targetSeconds = (double)_bytesRead / _maxBytesPerSecond;
        var elapsed = _clock.GetElapsedTime(_startTimestamp).TotalSeconds;
        var aheadSeconds = targetSeconds - elapsed;
        if (aheadSeconds > 0.001)
        {
            await Task.Delay(TimeSpan.FromSeconds(aheadSeconds), cancellationToken);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Synchronous path: read without pacing (the import always reads async;
        // this exists only to satisfy the abstract contract).
        return _inner.Read(buffer, offset, count);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

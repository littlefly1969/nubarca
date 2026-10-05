using System.Buffers.Binary;
using Microsoft.Extensions.Options;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

/// <summary>
/// HEIF stills coded with HEVC — the iPhone's photo format (HEIC). Recognised by
/// the ISO-BMFF <c>ftyp</c> box, never by the name or the MIME type a browser
/// claimed: the major brand or any compatible brand is one of the HEVC image
/// brands. AVIF (brand <c>avif</c>) and image sequences without an HEVC image
/// brand are not HEIC.
/// </summary>
public static class HeifSignature
{
    public const string ContentType = "image/heic";
    public const string Format = "HEIF";

    // The brands that declare HEVC-coded HEIF images or image collections
    // (ISO/IEC 23008-12 Annex B): heic/heix/heim/heis for images, hevc/hevx/
    // hevm/hevs for sequences that still carry them.
    private static readonly string[] HevcBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs"];

    /// <summary>How many leading bytes <see cref="IsHeif"/> reads.</summary>
    public const int HeaderLength = 64;

    public static bool IsHeif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        if (IsHevcBrand(header.Slice(8, 4))) return true;
        // Compatible brands follow the minor version, four bytes each, to the box's end.
        for (var offset = 16; offset + 4 <= boxSize; offset += 4)
        {
            if (IsHevcBrand(header.Slice(offset, 4))) return true;
        }
        return false;
    }

    private static bool IsHevcBrand(ReadOnlySpan<byte> brand)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++) text[i] = (char)brand[i];
        foreach (var candidate in HevcBrands)
        {
            if (text.SequenceEqual(candidate)) return true;
        }
        return false;
    }
}

/// <summary>
/// The ONE way an original's pixels are opened: by the derivative pipeline, the
/// print renderers, the AI and the upload's and bulk import's own detection.
///
/// For every format ImageSharp and libvips read, that is the original's own
/// bytes, as it always was. For HEIC, which neither can read, it is a LOSSLESS
/// PNG of the original's frame, decoded by FFmpeg from the original file into a
/// bounded TEMPORARY FILE at the moment it is asked for — never a whole PNG
/// held in memory by the reader, nothing stored, and a print is still a print
/// of the original, not of a derivative. FFmpeg assembles the tile grid and
/// applies the container's rotation, so the pixels come out upright and carry
/// no EXIF: nothing downstream may rotate them again (see
/// <see cref="OriginalImageReader.IsUprightOnDecode"/>).
/// </summary>
public interface IOriginalImageReader
{
    /// <summary>
    /// The original's pixels to read, or null when it cannot be opened or
    /// decoded. The caller owns the lease and disposes it when it has finished
    /// with the pixels — bytes read from it included: that closes the stream,
    /// deletes a decoded frame's temporary file and frees its decode slot.
    /// </summary>
    Task<OriginalPixelsLease?> OpenForPixelsAsync(Guid blobObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for an original that has no blob row yet — a file a bulk
    /// import has written to storage and is still recognising.
    /// </summary>
    Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Func<CancellationToken, Task<Stream>> openContent, CancellationToken cancellationToken);

    /// <summary>
    /// The same, decoded under slots the caller already holds for several
    /// frames at once (<see cref="HeifDecodeGate.EnterAsync(int, CancellationToken)"/>):
    /// a print sheet. The lease does not own those slots.
    /// </summary>
    Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Guid blobObjectId, IDisposable heldSlots, CancellationToken cancellationToken);
}

/// <summary>
/// An original's pixels, opened. For HEIC, <see cref="Content"/> reads a
/// temporary PNG file that is deleted when the lease is disposed, and the lease
/// holds its decode slot until then — while its bytes are being used too.
/// </summary>
public sealed class OriginalPixelsLease : IAsyncDisposable
{
    private readonly long _maxBytes;
    private IDisposable? _slot;

    internal OriginalPixelsLease(Stream content, bool isDecodedFrame, long maxBytes, IDisposable? slot = null)
    {
        Content = content;
        IsDecodedFrame = isDecodedFrame;
        _maxBytes = maxBytes;
        _slot = slot;
    }

    /// <summary>The bytes to decode, from the start.</summary>
    public Stream Content { get; }

    /// <summary>True for a HEIC frame FFmpeg decoded; false for the original's own bytes.</summary>
    public bool IsDecodedFrame { get; }

    /// <summary>
    /// The whole content as ONE array, read straight into it — only for a
    /// consumer whose own API takes bytes (the derivative renderer, the AI
    /// backends, the print composer), which keeps the lease alive until it has
    /// finished with them. Bounded by the decode's output limit; a content that
    /// is shorter or longer than its length said is refused, never returned in
    /// part.
    /// </summary>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken)
    {
        if (!Content.CanSeek)
        {
            // No length to size one array by: only a non-seekable ORIGINAL
            // (never a decoded frame, which is always a file) comes here.
            using var unknown = new MemoryStream();
            await Content.CopyToAsync(unknown, cancellationToken);
            if (unknown.Length > _maxBytes) throw new InvalidDataException("The pixels exceed their limit.");
            return unknown.ToArray();
        }

        Content.Position = 0;
        var length = Content.Length;
        if (length > _maxBytes || length > Array.MaxLength)
        {
            throw new InvalidDataException("The pixels exceed their limit.");
        }
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        // Throws EndOfStreamException if the content is shorter than it said.
        await Content.ReadExactlyAsync(bytes, cancellationToken);
        if (await Content.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new InvalidDataException("The pixels changed while they were read.");
        }
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        Interlocked.Exchange(ref _slot, null)?.Dispose();
    }
}

/// <summary>
/// How many HEIC frames may be decoded — and held, file and bytes — at once in
/// this process (<see cref="MediaOptions.HeifDecodeMaxConcurrency"/>). A
/// 48-megapixel frame is ~150 MB of PNG: without a bound, a burst of uploads,
/// a bulk import and the AI backfills could each hold several at a time.
/// </summary>
public sealed class HeifDecodeGate
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _several = new(1, 1);

    public HeifDecodeGate(IOptions<MediaOptions> options)
    {
        Capacity = Math.Max(1, options.Value.HeifDecodeMaxConcurrency);
        _slots = new SemaphoreSlim(Capacity, Capacity);
    }

    public int Capacity { get; }

    /// <summary>Slots free right now.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>One frame's slot; the wait is cancellable.</summary>
    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken) => EnterAsync(1, cancellationToken);

    /// <summary>
    /// Slots for several frames held together (a print sheet): as many as it
    /// has frames, at most every slot — a sheet with more frames than slots
    /// runs alone. Those who take several do so one at a time, so two of them
    /// can never each hold part of what the other is waiting for; a single
    /// frame never waits while holding one, so it cannot block them either.
    /// </summary>
    public async Task<IDisposable> EnterAsync(int frames, CancellationToken cancellationToken)
    {
        var wanted = Math.Clamp(frames, 1, Capacity);
        if (wanted == 1)
        {
            await _slots.WaitAsync(cancellationToken);
            return new Slots(_slots, 1);
        }

        await _several.WaitAsync(cancellationToken);
        var taken = 0;
        try
        {
            for (; taken < wanted; taken++)
            {
                await _slots.WaitAsync(cancellationToken);
            }
            return new Slots(_slots, wanted);
        }
        catch
        {
            if (taken > 0) _slots.Release(taken);
            throw;
        }
        finally
        {
            _several.Release();
        }
    }

    private sealed class Slots(SemaphoreSlim slots, int count) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release(count);
        }
    }
}

public sealed class OriginalImageReader : IOriginalImageReader
{
    private static int _swept;

    private readonly IBlobService _blobs;
    private readonly IProcessFileRunner _runner;
    private readonly HeifDecodeGate _gate;
    private readonly IOptions<MediaOptions> _options;
    private readonly ILogger<OriginalImageReader> _logger;

    public OriginalImageReader(
        IBlobService blobs, IProcessFileRunner runner, HeifDecodeGate gate,
        IOptions<MediaOptions> options, ILogger<OriginalImageReader> logger)
    {
        _blobs = blobs;
        _runner = runner;
        _gate = gate;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// True for a detected content type whose decoded pixels are already upright:
    /// its stored dimensions are the displayed ones and any EXIF orientation it
    /// carries must not be applied on top.
    /// </summary>
    public static bool IsUprightOnDecode(string? detectedContentType) =>
        string.Equals(detectedContentType, HeifSignature.ContentType, StringComparison.OrdinalIgnoreCase);

    public Task<OriginalPixelsLease?> OpenForPixelsAsync(Guid blobObjectId, CancellationToken cancellationToken) =>
        OpenAsync(ct => _blobs.OpenContentAsync(blobObjectId, ct), heldSlots: null, cancellationToken);

    public Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Func<CancellationToken, Task<Stream>> openContent, CancellationToken cancellationToken) =>
        OpenAsync(openContent, heldSlots: null, cancellationToken);

    public Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Guid blobObjectId, IDisposable heldSlots, CancellationToken cancellationToken) =>
        OpenAsync(ct => _blobs.OpenContentAsync(blobObjectId, ct), heldSlots, cancellationToken);

    private async Task<OriginalPixelsLease?> OpenAsync(
        Func<CancellationToken, Task<Stream>> openContent, IDisposable? heldSlots, CancellationToken cancellationToken)
    {
        var source = await openContent(cancellationToken);
        bool heif;
        try
        {
            var header = new byte[HeifSignature.HeaderLength];
            var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            heif = HeifSignature.IsHeif(header.AsSpan(0, read));
            if (!heif && source.CanSeek)
            {
                // The original's own bytes, from the start: the stream itself.
                source.Position = 0;
                var own = source;
                source = null!;
                return new OriginalPixelsLease(own, isDecodedFrame: false, maxBytes: long.MaxValue);
            }
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
        }
        return heif
            ? await DecodeHeifAsync(openContent, heldSlots, cancellationToken)
            : new OriginalPixelsLease(await openContent(cancellationToken), isDecodedFrame: false, maxBytes: long.MaxValue);
    }

    private async Task<OriginalPixelsLease?> DecodeHeifAsync(
        Func<CancellationToken, Task<Stream>> openContent, IDisposable? heldSlots, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        // Waiting for a slot is cancellable; holding one ends with the lease —
        // unless the caller holds slots for several frames, and frees them itself.
        var slot = heldSlots is null ? await _gate.EnterAsync(cancellationToken) : null;
        string? inputCopy = null;
        string? output = null;
        try
        {
            var directory = WorkDirectory();
            var name = Guid.NewGuid().ToString("N");

            // HEIF needs a seekable file: its item locations point anywhere in
            // it. Production storage hands out the read-only FileStream itself,
            // and FFmpeg reads that path; anything else is copied first.
            string input;
            await using (var source = await openContent(cancellationToken))
            {
                if (source is FileStream file && File.Exists(file.Name))
                {
                    input = file.Name;
                }
                else
                {
                    inputCopy = Path.Combine(directory, name + ".heic");
                    await using (var copy = File.Create(inputCopy))
                    {
                        await source.CopyToAsync(copy, cancellationToken);
                    }
                    input = inputCopy;
                }
            }

            // The frame comes out on stdout and is streamed to a file with the
            // output limit enforced AS IT ARRIVES: one byte past it and FFmpeg is
            // killed and the file deleted — neither memory nor disk grows past
            // the cap. (-fs bounds no single image.)
            output = Path.Combine(directory, name + ".png");
            var result = await _runner.RunAsync(new ProcessFileRunRequest(
                options.FfmpegPath,
                [
                    "-v", "error", "-nostdin", "-i", input, "-frames:v", "1",
                    // Lossless and quick to write: a PNG with no compression.
                    "-c:v", "png", "-compression_level", "0", "-pred", "none",
                    "-f", "image2pipe", "-",
                ],
                output,
                options.HeifDecodeMaxOutputBytes,
                options.HeifDecodeTimeoutSeconds), cancellationToken);

            // Defence in depth: the file is what the runner says it wrote.
            var length = File.Exists(output) ? new FileInfo(output).Length : 0;
            if (result.ExitCode != 0 || result.TimedOut || result.OutputLimitExceeded
                || length == 0 || length != result.BytesWritten || length > options.HeifDecodeMaxOutputBytes)
            {
                // Never the path: only what happened.
                _logger.LogWarning(
                    "HEIF decode failed (exit {ExitCode}, timed out {TimedOut}, over the output limit {OverLimit}).",
                    result.ExitCode, result.TimedOut, result.OutputLimitExceeded);
                return null;
            }

            // From here the lease owns the file (deleted when its stream
            // closes) and the slot (freed when it is disposed).
            var frame = new FileStream(
                output, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            output = null;
            var lease = new OriginalPixelsLease(frame, isDecodedFrame: true, options.HeifDecodeMaxOutputBytes, slot);
            slot = null;
            return lease;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("HEIF decode could not run ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
        finally
        {
            // Every path that did not hand a frame to a lease — failure,
            // refusal, cancellation — leaves nothing behind.
            TryDelete(inputCopy);
            TryDelete(output);
            slot?.Dispose();
        }
    }

    /// <summary>
    /// The decoder's own temporary directory. Its files live only as long as a
    /// decode or a lease; the first use in a process clears what a crash may
    /// have left there.
    /// </summary>
    internal static string WorkDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nubarca-heif");
        Directory.CreateDirectory(directory);
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            foreach (var stale in Directory.EnumerateFiles(directory))
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1)) TryDelete(stale);
            }
        }
        return directory;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// An original's pixels as bytes, for a consumer whose own API takes bytes —
/// the AI backends, the guest book's and the face previews' renderers. HELD
/// for as long as the bytes are used: a HEIC frame keeps its decode slot until
/// the consumer disposes this, so the gate bounds the frames actually resident,
/// not only the ones being decoded.
/// </summary>
public sealed class OriginalPixelsBytes : IAsyncDisposable
{
    private readonly OriginalPixelsLease? _lease;

    internal OriginalPixelsBytes(byte[] bytes, OriginalPixelsLease? lease)
    {
        Bytes = bytes;
        _lease = lease;
    }

    public byte[] Bytes { get; }

    public ValueTask DisposeAsync() => _lease?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The adapter the byte-taking consumers use. With a reader, HEIC is decoded;
/// without — the AI services' direct-construction test sites — the original's
/// own bytes, as before. A frame that cannot be decoded throws, which every
/// caller already treats as unreadable bytes.
/// </summary>
public static class OriginalPixels
{
    public static async Task<OriginalPixelsBytes> OpenAsync(
        IBlobService blobs, IOriginalImageReader? originals, Guid blobObjectId, CancellationToken cancellationToken)
    {
        if (originals is not null)
        {
            var lease = await originals.OpenForPixelsAsync(blobObjectId, cancellationToken)
                ?? throw new InvalidDataException("The original's pixels could not be decoded.");
            try
            {
                return new OriginalPixelsBytes(await lease.ReadAllBytesAsync(cancellationToken), lease);
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }
        }
        await using var stream = await blobs.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return new OriginalPixelsBytes(buffer.ToArray(), lease: null);
    }
}

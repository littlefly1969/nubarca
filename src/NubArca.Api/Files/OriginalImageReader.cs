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
    /// decoded. The caller owns the lease and disposes it: that closes the
    /// stream, deletes a decoded frame's temporary file and frees its decode
    /// slot.
    /// </summary>
    Task<OriginalPixelsLease?> OpenForPixelsAsync(Guid blobObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for an original that has no blob row yet — a file a bulk
    /// import has written to storage and is still recognising.
    /// </summary>
    Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Func<CancellationToken, Task<Stream>> openContent, CancellationToken cancellationToken);
}

/// <summary>
/// An original's pixels, opened. For HEIC, <see cref="Content"/> reads a
/// temporary PNG file that is deleted when the lease is disposed, and the lease
/// holds one of the bounded decode slots until then.
/// </summary>
public sealed class OriginalPixelsLease : IAsyncDisposable
{
    private IDisposable? _slot;

    internal OriginalPixelsLease(Stream content, bool isDecodedFrame, IDisposable? slot = null)
    {
        Content = content;
        IsDecodedFrame = isDecodedFrame;
        _slot = slot;
    }

    /// <summary>The bytes to decode, from the start.</summary>
    public Stream Content { get; }

    /// <summary>True for a HEIC frame FFmpeg decoded; false for the original's own bytes.</summary>
    public bool IsDecodedFrame { get; }

    /// <summary>
    /// The whole content in memory, from the start — only for a consumer whose
    /// own API takes bytes (the derivative renderer, the AI backends, the print
    /// composer). Readers of the header alone use <see cref="Content"/>.
    /// </summary>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken)
    {
        if (Content.CanSeek) Content.Position = 0;
        using var buffer = Content.CanSeek ? new MemoryStream((int)Math.Min(Content.Length, int.MaxValue)) : new MemoryStream();
        await Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        Interlocked.Exchange(ref _slot, null)?.Dispose();
    }
}

/// <summary>
/// How many HEIC frames may be decoded — and held as decoded frames — at once
/// in this process (<see cref="MediaOptions.HeifDecodeMaxConcurrency"/>). A
/// 48-megapixel frame is ~150 MB of PNG: without a bound, a burst of uploads,
/// a bulk import and the AI backfills could each hold several at a time.
/// </summary>
public sealed class HeifDecodeGate
{
    private readonly SemaphoreSlim _slots;

    public HeifDecodeGate(IOptions<MediaOptions> options)
    {
        Capacity = Math.Max(1, options.Value.HeifDecodeMaxConcurrency);
        _slots = new SemaphoreSlim(Capacity, Capacity);
    }

    public int Capacity { get; }

    /// <summary>Slots free right now.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>Waits for a free slot; cancellable while waiting.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Slot(_slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}

public sealed class OriginalImageReader : IOriginalImageReader
{
    private static int _swept;

    private readonly IBlobService _blobs;
    private readonly IDirectoryProcessRunner _runner;
    private readonly HeifDecodeGate _gate;
    private readonly IOptions<MediaOptions> _options;
    private readonly ILogger<OriginalImageReader> _logger;

    public OriginalImageReader(
        IBlobService blobs, IDirectoryProcessRunner runner, HeifDecodeGate gate,
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
        OpenForPixelsAsync(ct => _blobs.OpenContentAsync(blobObjectId, ct), cancellationToken);

    public async Task<OriginalPixelsLease?> OpenForPixelsAsync(
        Func<CancellationToken, Task<Stream>> openContent, CancellationToken cancellationToken)
    {
        var source = await openContent(cancellationToken);
        bool heif;
        try
        {
            var header = new byte[HeifSignature.HeaderLength];
            var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            heif = HeifSignature.IsHeif(header.AsSpan(0, read));
            if (!heif)
            {
                // The original's own bytes, from the start: the stream itself
                // when it can rewind (storage's FileStream), else opened again.
                if (source.CanSeek)
                {
                    source.Position = 0;
                    var own = source;
                    source = null!;
                    return new OriginalPixelsLease(own, isDecodedFrame: false);
                }
            }
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
        }
        return heif
            ? await DecodeHeifAsync(openContent, cancellationToken)
            : new OriginalPixelsLease(await openContent(cancellationToken), isDecodedFrame: false);
    }

    private async Task<OriginalPixelsLease?> DecodeHeifAsync(
        Func<CancellationToken, Task<Stream>> openContent, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        // Waiting for a slot is cancellable; holding one ends with the lease.
        var slot = await _gate.EnterAsync(cancellationToken);
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

            output = Path.Combine(directory, name + ".png");
            var result = await _runner.RunAsync(new ProcessDirectoryRunRequest(
                options.FfmpegPath,
                [
                    "-v", "error", "-nostdin", "-y", "-i", input, "-frames:v", "1",
                    // Lossless and quick to write: a PNG with no compression.
                    "-c:v", "png", "-compression_level", "0", "-pred", "none",
                    "-f", "image2", "-update", "1", output,
                ],
                directory,
                options.HeifDecodeTimeoutSeconds), cancellationToken);

            // The output limit: a frame larger than it is refused, deleted and
            // never read — the file can only reach one frame before that.
            var length = File.Exists(output) ? new FileInfo(output).Length : 0;
            if (result.ExitCode != 0 || result.TimedOut || length == 0 || length > options.HeifDecodeMaxOutputBytes)
            {
                // Never the path: only what happened.
                _logger.LogWarning(
                    "HEIF decode failed (exit {ExitCode}, timed out {TimedOut}, over the output limit {OverLimit}).",
                    result.ExitCode, result.TimedOut, length > options.HeifDecodeMaxOutputBytes);
                return null;
            }

            // From here the lease owns the file (deleted when its stream
            // closes) and the slot (freed when it is disposed).
            var frame = new FileStream(
                output, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            output = null;
            var lease = new OriginalPixelsLease(frame, isDecodedFrame: true, slot);
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
/// The original's pixels as bytes for a consumer that also runs without the
/// reader — the AI services, whose direct-construction test sites predate it,
/// and whose backends take bytes. With a reader, HEIC is decoded; without, the
/// original's own bytes, as before. A frame that cannot be decoded throws,
/// which every caller already treats as unreadable bytes.
/// </summary>
public static class OriginalPixels
{
    public static async Task<byte[]> ReadAsync(
        IBlobService blobs, IOriginalImageReader? originals, Guid blobObjectId, CancellationToken cancellationToken)
    {
        if (originals is not null)
        {
            await using var pixels = await originals.OpenForPixelsAsync(blobObjectId, cancellationToken)
                ?? throw new InvalidDataException("The original's pixels could not be decoded.");
            return await pixels.ReadAllBytesAsync(cancellationToken);
        }
        await using var stream = await blobs.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

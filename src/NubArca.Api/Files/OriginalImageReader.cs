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
/// print renderers, the AI and the upload's own detection.
///
/// For every format ImageSharp and libvips read, that is the original's own
/// bytes, as it always was. For HEIC, which neither can read, it is a LOSSLESS
/// PNG of the original's frame, decoded by FFmpeg from the original file at the
/// moment it is asked for — nothing is stored, and a print is still a print of
/// the original, not of a derivative. FFmpeg assembles the tile grid and applies
/// the container's rotation, so the pixels come out upright and carry no EXIF:
/// nothing downstream may rotate them again (see <see cref="IsUprightOnDecode"/>).
/// </summary>
public interface IOriginalImageReader
{
    /// <summary>The bytes to decode, or null when the original cannot be opened or decoded.</summary>
    Task<byte[]?> ReadForPixelsAsync(Guid blobObjectId, CancellationToken cancellationToken);
}

public sealed class OriginalImageReader : IOriginalImageReader
{
    private readonly IBlobService _blobs;
    private readonly IProcessRunner _runner;
    private readonly IOptions<MediaOptions> _options;
    private readonly ILogger<OriginalImageReader> _logger;

    public OriginalImageReader(
        IBlobService blobs, IProcessRunner runner, IOptions<MediaOptions> options, ILogger<OriginalImageReader> logger)
    {
        _blobs = blobs;
        _runner = runner;
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

    public async Task<byte[]?> ReadForPixelsAsync(Guid blobObjectId, CancellationToken cancellationToken)
    {
        await using var source = await _blobs.OpenContentAsync(blobObjectId, cancellationToken);
        var header = new byte[HeifSignature.HeaderLength];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (!HeifSignature.IsHeif(header.AsSpan(0, read)))
        {
            using var copy = new MemoryStream();
            await copy.WriteAsync(header.AsMemory(0, read), cancellationToken);
            await source.CopyToAsync(copy, cancellationToken);
            return copy.ToArray();
        }
        return await DecodeHeifAsync(source, header.AsMemory(0, read), cancellationToken);
    }

    private async Task<byte[]?> DecodeHeifAsync(
        Stream source, ReadOnlyMemory<byte> header, CancellationToken cancellationToken)
    {
        // HEIF needs a seekable file: its item locations point anywhere in it.
        // Production storage hands out the read-only FileStream itself, and
        // FFmpeg reads that path; anything else is copied to a temp file first.
        string? temp = null;
        var input = source is FileStream file && File.Exists(file.Name) ? file.Name : null;
        try
        {
            if (input is null)
            {
                temp = Path.Combine(Path.GetTempPath(), $"nubarca-heif-{Guid.NewGuid():N}");
                await using (var destination = File.Create(temp))
                {
                    // The header was already read off the stream: it goes first.
                    await destination.WriteAsync(header, cancellationToken);
                    await source.CopyToAsync(destination, cancellationToken);
                }
                input = temp;
            }

            var options = _options.Value;
            var result = await _runner.RunAsync(new ProcessRunRequest(
                options.FfmpegPath,
                [
                    "-v", "error", "-nostdin", "-i", input, "-frames:v", "1",
                    // Lossless and quick to write: a PNG with no compression.
                    "-c:v", "png", "-compression_level", "0", "-pred", "none",
                    "-f", "image2pipe", "-",
                ],
                options.HeifDecodeTimeoutSeconds,
                options.HeifDecodeMaxOutputBytes), cancellationToken);
            if (result.ExitCode != 0 || result.TimedOut || result.OutputTruncated || result.StdoutBytes.Length == 0)
            {
                // Never the path: only what happened.
                _logger.LogWarning(
                    "HEIF decode failed (exit {ExitCode}, timed out {TimedOut}, truncated {Truncated}).",
                    result.ExitCode, result.TimedOut, result.OutputTruncated);
                return null;
            }
            return result.StdoutBytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("HEIF decode could not run ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }
}

/// <summary>
/// The original's pixels for a consumer that also runs without the reader —
/// the AI services, whose direct-construction test sites predate it. With a
/// reader, HEIC is decoded; without, the original's own bytes, as before. A
/// frame that cannot be decoded throws, which every caller already treats as
/// unreadable bytes.
/// </summary>
public static class OriginalPixels
{
    public static async Task<byte[]> ReadAsync(
        IBlobService blobs, IOriginalImageReader? originals, Guid blobObjectId, CancellationToken cancellationToken)
    {
        if (originals is not null)
        {
            return await originals.ReadForPixelsAsync(blobObjectId, cancellationToken)
                ?? throw new InvalidDataException("The original's pixels could not be decoded.");
        }
        await using var stream = await blobs.OpenContentAsync(blobObjectId, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

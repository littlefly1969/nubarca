using System.Buffers;
using System.Text;

namespace NubArca.Api.Metadata;

// Stateless, dependency-free implementation. Singleton-safe.
public sealed class VideoSignatureDetector : IVideoSignatureDetector
{
    public const string Mp4ContentType = "video/mp4";
    public const string WebmContentType = "video/webm";
    public const string QuickTimeContentType = "video/quicktime";

    // 64 bytes is enough to see the ISO BMFF ftyp box's brand + a few
    // compatible brands, and to find the EBML DocType element for WebM /
    // Matroska. Reading more would waste I/O on the first few KB of every
    // upload.
    private const int HeaderBytesToRead = 64;

    public async Task<VideoSignature?> InspectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var rented = ArrayPool<byte>.Shared.Rent(HeaderBytesToRead);
        try
        {
            var span = rented.AsMemory(0, HeaderBytesToRead);
            int read;
            try
            {
                read = await ReadFullAsync(stream, span, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (read < 8)
            {
                return null;
            }

            var header = rented.AsSpan(0, read);
            return DetectMp4(header) ?? DetectWebm(header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // ISO BMFF (MP4 / MOV / M4V / heif containers also use ftyp, but only
    // video brands are returned here). Layout:
    //   offset 0..3 : box size (uint32 BE)
    //   offset 4..7 : "ftyp"
    //   offset 8..11: major brand (ASCII)
    //   offset 12..15: minor version
    //   offset 16+ : compatible brands (ASCII, 4 bytes each)
    private static VideoSignature? DetectMp4(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16) return null;
        if (!header.Slice(4, 4).SequenceEqual("ftyp"u8)) return null;

        // Major brand at 8..11.
        var brand = AsciiBrand(header.Slice(8, 4));

        // Brand "qt  " = QuickTime; trailing spaces are significant in ftyp.
        if (string.Equals(brand, "qt", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoSignature(QuickTimeContentType, "QuickTime");
        }

        // Common MP4 brands. Most consumer files emit isom + a compatible mp4* brand.
        if (IsMp4VideoBrand(brand))
        {
            return new VideoSignature(Mp4ContentType, "MP4");
        }

        // Some producers stuff the video brand only in the compatible-brand
        // list. Walk the compatible brand slots after the minor version.
        for (var off = 16; off + 4 <= header.Length; off += 4)
        {
            var compat = AsciiBrand(header.Slice(off, 4));
            if (string.Equals(compat, "qt", StringComparison.OrdinalIgnoreCase))
            {
                return new VideoSignature(QuickTimeContentType, "QuickTime");
            }
            if (IsMp4VideoBrand(compat))
            {
                return new VideoSignature(Mp4ContentType, "MP4");
            }
        }

        return null;
    }

    private static bool IsMp4VideoBrand(string brand)
    {
        // Brands consumer browsers will accept under video/mp4.
        return brand switch
        {
            "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6"
                or "mp41" or "mp42" or "mp4 "
                or "avc1" or "M4V" or "M4VH" or "M4VP" or "M4A" or "dash"
                or "f4v" or "f4p" => true,
            _ => brand.StartsWith("mp4", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string AsciiBrand(ReadOnlySpan<byte> bytes)
    {
        var s = Encoding.ASCII.GetString(bytes);
        // Brands are 4-char ASCII padded with spaces. Trim trailing spaces so
        // callers can compare "qt  " as "qt".
        return s.TrimEnd();
    }

    // WebM / Matroska: file starts with EBML signature 1A 45 DF A3. The
    // DocType element (master ID 0x4282, in the EBML header) carries the
    // string "webm" or "matroska". A full EBML parser would be overkill —
    // we just look for either ASCII string anywhere in the first 64 bytes.
    private static VideoSignature? DetectWebm(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return null;
        if (header[0] != 0x1A || header[1] != 0x45 || header[2] != 0xDF || header[3] != 0xA3)
        {
            return null;
        }

        if (ContainsAscii(header, "webm"u8))
        {
            return new VideoSignature(WebmContentType, "WebM");
        }
        if (ContainsAscii(header, "matroska"u8))
        {
            // Mainstream browsers play webm but not raw matroska; we still
            // expose the file as video/webm so playback can be attempted.
            // Unsupported codecs inside the container surface via the
            // <video> element's error event on the client.
            return new VideoSignature(WebmContentType, "Matroska");
        }
        return null;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle) >= 0;
    }

    private static async Task<int> ReadFullAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(read), cancellationToken);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}

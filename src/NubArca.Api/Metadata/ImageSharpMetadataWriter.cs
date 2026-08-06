using System.Globalization;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace NubArca.Api.Metadata;

// Bakes a DateTaken value into JPEG bytes via ImageSharp's ExifProfile.
//
// Only JPEG is supported: ImageSharp writes a standard EXIF APP1 segment and
// the MetadataExtractor read path (slice 54) reliably maps DateTimeOriginal →
// DateTaken, so the value round-trips. PNG eXIf support is less consistent
// across the read path, so it is deferred (→ UnsupportedImageFormatException).
//
// Re-encoding a JPEG is lossy; quality 95 keeps the perceptual loss small,
// matching the stripper's choice. The existing EXIF profile (camera, etc.) is
// preserved — only the capture-date tags are overwritten. Determinism: same
// input + same date ⇒ byte-identical output, so the dedup-aware blob store
// short-circuits a repeated write.
public sealed class ImageSharpMetadataWriter : IImageMetadataWriter
{
    private const int JpegQuality = 95;
    public const string JpegContentType = "image/jpeg";

    // EXIF stores dates as "yyyy:MM:dd HH:mm:ss" (no timezone).
    private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";

    private readonly IOptions<ImageProcessingOptions> _options;

    public ImageSharpMetadataWriter(IOptions<ImageProcessingOptions> options)
    {
        _options = options;
    }

    public bool SupportsDateTaken(string? detectedContentType)
        => string.Equals(detectedContentType, JpegContentType, StringComparison.OrdinalIgnoreCase);

    public async Task<MemoryStream> WriteDateTakenAsync(
        Stream input,
        string detectedContentType,
        DateTime dateTakenUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!SupportsDateTaken(detectedContentType))
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        var opts = _options.Value;

        // Buffer first (blob streams may not be seekable) and reject
        // decompression bombs via a header-only identify before decoding.
        var buffered = await ReadAllAsync(input, cancellationToken);

        using (var identifyStream = new MemoryStream(buffered, writable: false))
        {
            ImageInfo? info = null;
            try
            {
                info = await Image.IdentifyAsync(identifyStream, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // fall through; LoadAsync will throw a sanitized exception
            }

            if (info is not null)
            {
                var pixels = (long)info.Width * info.Height;
                if (info.Width > opts.MaxWidth
                    || info.Height > opts.MaxHeight
                    || pixels > opts.MaxPixels)
                {
                    throw new ImageProcessingLimitException(
                        "Image is too large for safe metadata writeback.");
                }
            }
        }

        using var loadStream = new MemoryStream(buffered, writable: false);
        Image image;
        try
        {
            image = await Image.LoadAsync(loadStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        using (image)
        {
            var exif = image.Metadata.ExifProfile ?? new ExifProfile();
            var value = dateTakenUtc.ToString(ExifDateFormat, CultureInfo.InvariantCulture);
            exif.SetValue(ExifTag.DateTimeOriginal, value);
            exif.SetValue(ExifTag.DateTimeDigitized, value);
            image.Metadata.ExifProfile = exif;

            var output = new MemoryStream();
            try
            {
                await image.SaveAsync(output, new JpegEncoder { Quality = JpegQuality }, cancellationToken);
            }
            catch
            {
                output.Dispose();
                throw;
            }

            output.Position = 0;
            return output;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream input, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }
}

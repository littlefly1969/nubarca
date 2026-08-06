using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace NubArca.Api.Metadata;

// Strips embedded metadata from an image by decoding it with ImageSharp,
// clearing the standard metadata profiles + PNG text chunks, and
// re-encoding to the original container format. Supports JPEG and PNG.
//
// Trade-off: ImageSharp does not expose a way to mutate a JPEG's metadata
// segments without re-encoding the pixel data. Re-encoding a JPEG is
// LOSSY — the output bytes will differ from the input even when the
// metadata-free image would visually be identical. We pick quality=95 so
// the perceptual loss is small. PNG re-encoding is lossless.
//
// Idempotency: ImageSharp's encoders are deterministic for the same input
// + settings, so re-stripping an already-metadata-free image produces the
// same SHA-256 and the dedup-aware `IBlobService.StoreAsync` short-circuits.
public sealed class ImageSharpMetadataStripper : IImageMetadataStripper
{
    private const int JpegStripQuality = 95;

    public const string JpegContentType = "image/jpeg";
    public const string PngContentType = "image/png";

    private static readonly HashSet<string> SupportedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { JpegContentType, PngContentType };

    private readonly IOptions<ImageProcessingOptions> _options;

    public ImageSharpMetadataStripper(IOptions<ImageProcessingOptions> options)
    {
        _options = options;
    }

    public bool IsSupported(string? detectedContentType)
        => detectedContentType is not null
            && SupportedContentTypes.Contains(detectedContentType);

    public async Task<MemoryStream> StripAsync(
        Stream input,
        string detectedContentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(detectedContentType);

        if (!IsSupported(detectedContentType))
        {
            throw new UnsupportedImageFormatException(detectedContentType);
        }

        var opts = _options.Value;

        // Header-only check first: rejects decompression bombs without
        // allocating a pixel buffer. The bytes are buffered into memory so we
        // can decode after Identify consumes the leading bytes — blob streams
        // may not be seekable.
        var buffered = await ReadAllAsync(input, cancellationToken);
        try
        {
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
                    // Fall through; LoadAsync below will throw with a
                    // sanitized message.
                }

                if (info is not null)
                {
                    var pixels = (long)info.Width * info.Height;
                    if (info.Width > opts.MaxWidth
                        || info.Height > opts.MaxHeight
                        || pixels > opts.MaxPixels)
                    {
                        throw new ImageProcessingLimitException(
                            "Image is too large for safe metadata stripping.");
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
                // Standard profile metadata: EXIF (camera/GPS/serials), IPTC
                // (captions/keywords), XMP (Adobe descriptive XML), ICC
                // (color profile). All carry potentially identifying
                // information and all are SAFE to drop for a personal-cloud
                // viewing copy.
                image.Metadata.ExifProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;
                image.Metadata.IccProfile = null;

                // PNG-specific textual chunks (tEXt / iTXt / zTXt). Image
                // editors often write a "Software" tEXt chunk here even when
                // there is no EXIF block; clearing TextData removes all of
                // them. Safe to call regardless of source format — for non-
                // PNG inputs the PngMetadata is unused on encode.
                var pngMeta = image.Metadata.GetPngMetadata();
                pngMeta.TextData.Clear();

                var output = new MemoryStream();
                try
                {
                    if (string.Equals(detectedContentType, JpegContentType, StringComparison.OrdinalIgnoreCase))
                    {
                        await image.SaveAsync(
                            output,
                            new JpegEncoder { Quality = JpegStripQuality },
                            cancellationToken);
                    }
                    else
                    {
                        // PNG: lossless re-encode. PngEncoder defaults are
                        // deterministic and do not add author / software
                        // chunks beyond what was in image.Metadata (which we
                        // cleared above).
                        await image.SaveAsync(output, new PngEncoder(), cancellationToken);
                    }
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
        finally
        {
            // Best-effort: nothing to dispose on `buffered` itself (it's a
            // managed byte[]), but we make the lifetime explicit.
            _ = buffered;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream input, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }
}

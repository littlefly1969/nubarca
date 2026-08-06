using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Metadata;

// Programmatically-generated image fixtures with embedded EXIF/GPS, so tests
// don't depend on checked-in binary blobs. ImageSharp writes the EXIF profile;
// MetadataExtractor reads it back on the production side.
internal static class ImageFixtures
{
    public const string CameraMake = "NanoCam";
    public const string CameraModel = "Model-X";
    public const string Software = "NanoFirmware 1.0";
    public const string LensModel = "NanoLens 50mm";
    public const string BodySerial = "BODY-SN-SECRET-XYZ";
    public const string LensSerial = "LENS-SN-SECRET-XYZ";
    public const string DateTimeOriginalExif = "2023:06:15 14:30:00";

    // A 16x16 JPEG carrying a representative EXIF block. When includeGps is
    // true it also embeds GPS coordinates (~51.5°N, ~0.117°W → London-ish).
    public static byte[] JpegWithExif(bool includeGps = false, bool includeSerials = true)
    {
        using var image = new Image<Rgb24>(16, 16);
        var exif = new ExifProfile();

        exif.SetValue(ExifTag.Make, CameraMake);
        exif.SetValue(ExifTag.Model, CameraModel);
        exif.SetValue(ExifTag.Software, Software);
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.DateTimeOriginal, DateTimeOriginalExif);
        exif.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 400 });
        exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 250));
        exif.SetValue(ExifTag.FocalLength, new Rational(50, 1));
        exif.SetValue(ExifTag.LensModel, LensModel);
        exif.SetValue(ExifTag.ColorSpace, (ushort)1); // sRGB

        if (includeSerials)
        {
            // ImageSharp names the EXIF body serial tag (0xA431) "SerialNumber".
            exif.SetValue(ExifTag.SerialNumber, BodySerial);
            exif.SetValue(ExifTag.LensSerialNumber, LensSerial);
        }

        if (includeGps)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, new[]
            {
                new Rational(51, 1), new Rational(30, 1), new Rational(0, 1),
            });
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, new[]
            {
                new Rational(0, 1), new Rational(7, 1), new Rational(0, 1),
            });
        }

        image.Metadata.ExifProfile = exif;
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    // A plain PNG with no embedded camera metadata.
    public static byte[] PlainPng(int width = 16, int height = 16)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // A PNG whose 8-byte signature + IHDR are valid (so Image.Identify reads
    // dimensions) but whose IDAT is clobbered, so a full decode throws. Used to
    // exercise the decode_failed derivative diagnostic. `seed` varies the bytes
    // so multiple distinct (non-deduping) blobs can be produced.
    public static byte[] UndecodablePng(int seed = 0)
    {
        using var image = new Image<Rgba32>(32, 32);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        var png = ms.ToArray();
        for (var i = 45; i < png.Length; i++)
        {
            png[i] = (byte)(0xFF ^ (i + seed));
        }
        return png;
    }

    // A PNG carrying tEXt chunks (one well-known keyword + one user keyword).
    // Used to verify the slice-58 stripper drops PngTextData on re-encode.
    public const string PngSoftwareTag = "PNG-SECRET-EDITOR";
    public const string PngAuthorTag = "PNG-SECRET-AUTHOR";

    public static byte[] PngWithTextMetadata(int width = 16, int height = 16)
    {
        using var image = new Image<Rgba32>(width, height);
        var pngMeta = image.Metadata.GetPngMetadata();
        pngMeta.TextData.Add(new PngTextData("Software", PngSoftwareTag, string.Empty, string.Empty));
        pngMeta.TextData.Add(new PngTextData("Author", PngAuthorTag, string.Empty, string.Empty));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // A JPEG whose EXIF IFD has been scribbled over while keeping the JPEG
    // structure (APP1 length, SOF, scan) intact — exercises corrupt-metadata
    // tolerance without producing an undecodable image.
    public static byte[] JpegWithCorruptExif()
    {
        var bytes = JpegWithExif(includeGps: true);
        var marker = "Exif\0\0"u8.ToArray();
        var idx = IndexOf(bytes, marker);
        if (idx >= 0)
        {
            // Skip the TIFF header (8 bytes) and clobber some IFD bytes.
            var start = idx + marker.Length + 8;
            for (var i = start; i < Math.Min(start + 24, bytes.Length); i++)
            {
                bytes[i] = 0xFF;
            }
        }
        return bytes;
    }

    // Minimal MP4-shaped header: ISO BMFF box-size + "ftyp" + brand + minor
    // version + a compatible "mp42" brand. The byte stream is not a playable
    // MP4 (we don't write any moov/mdat) but the slice-62 video signature
    // detector only reads the leading bytes.
    public static byte[] MinimalMp4(string majorBrand = "isom")
    {
        if (majorBrand.Length != 4) throw new ArgumentException("brand must be 4 chars");
        var brand = System.Text.Encoding.ASCII.GetBytes(majorBrand);
        // 4-byte size + 4-byte "ftyp" + 4-byte major + 4-byte minor + 4-byte compat brand "mp42"
        return new byte[]
        {
            0x00, 0x00, 0x00, 0x18, // box size = 24
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            brand[0], brand[1], brand[2], brand[3],
            0x00, 0x00, 0x00, 0x01, // minor version
            (byte)'m', (byte)'p', (byte)'4', (byte)'2',
            // trailing junk so total length is reasonable
            0x00, 0x00, 0x00, 0x00,
        };
    }

    // Minimal QuickTime header (major brand "qt  ").
    public static byte[] MinimalMov()
    {
        var b = MinimalMp4("qt  ");
        return b;
    }

    // Minimal WebM-shaped header: EBML magic + DocType element with value
    // "webm". As above, only the leading bytes matter for detection.
    public static byte[] MinimalWebm()
    {
        return new byte[]
        {
            0x1A, 0x45, 0xDF, 0xA3, // EBML magic
            0x9F, // EBML header size (mock)
            // sprinkle the "webm" ASCII bytes inside the first 64 bytes
            0x42, 0x82, // DocType element ID
            0x84,       // length 4
            (byte)'w', (byte)'e', (byte)'b', (byte)'m',
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}

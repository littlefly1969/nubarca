using System.Text;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using SixLabors.ImageSharp;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Pure unit tests for the slice-58 metadata stripper. No DB, no HTTP.
// Verifies that the re-encoded bytes carry no EXIF / PNG textual chunks /
// IPTC / XMP, while still decoding as the same format with the same
// dimensions.
public sealed class ImageSharpMetadataStripperTests
{
    private static ImageSharpMetadataStripper CreateStripper()
        => new ImageSharpMetadataStripper(
            Options.Create(new ImageProcessingOptions()));

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("image/webp", false)]
    [InlineData("image/gif", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSupported_Returns_Expected(string? contentType, bool expected)
    {
        var stripper = CreateStripper();
        Assert.Equal(expected, stripper.IsSupported(contentType));
    }

    [Fact]
    public async Task StripAsync_Jpeg_Removes_Exif_Including_Gps_And_Serials()
    {
        var stripper = CreateStripper();
        var input = ImageFixtures.JpegWithExif(includeGps: true);
        Assert.True(ContainsAscii(input, ImageFixtures.BodySerial),
            "fixture must carry the serial we'll be looking for");

        using var stripped = await stripper.StripAsync(
            new MemoryStream(input), "image/jpeg");
        var bytes = stripped.ToArray();

        // None of the sensitive ASCII strings survive the re-encode.
        Assert.False(ContainsAscii(bytes, ImageFixtures.BodySerial));
        Assert.False(ContainsAscii(bytes, ImageFixtures.LensSerial));
        Assert.False(ContainsAscii(bytes, ImageFixtures.Software));
        Assert.False(ContainsAscii(bytes, ImageFixtures.CameraMake));
        Assert.False(ContainsAscii(bytes, ImageFixtures.CameraModel));
        Assert.False(ContainsAscii(bytes, ImageFixtures.LensModel));

        // The new bytes still decode as a JPEG of the same dimensions.
        using var roundTrip = await Image.LoadAsync(new MemoryStream(bytes));
        Assert.Null(roundTrip.Metadata.ExifProfile);
        Assert.Null(roundTrip.Metadata.IptcProfile);
        Assert.Null(roundTrip.Metadata.XmpProfile);
        Assert.Null(roundTrip.Metadata.IccProfile);
        Assert.Equal(16, roundTrip.Width);
        Assert.Equal(16, roundTrip.Height);
    }

    [Fact]
    public async Task StripAsync_Png_Removes_TextChunks()
    {
        var stripper = CreateStripper();
        var input = ImageFixtures.PngWithTextMetadata();
        Assert.True(ContainsAscii(input, ImageFixtures.PngSoftwareTag),
            "fixture must carry the tEXt value we'll be looking for");

        using var stripped = await stripper.StripAsync(
            new MemoryStream(input), "image/png");
        var bytes = stripped.ToArray();

        Assert.False(ContainsAscii(bytes, ImageFixtures.PngSoftwareTag));
        Assert.False(ContainsAscii(bytes, ImageFixtures.PngAuthorTag));

        using var roundTrip = await Image.LoadAsync(new MemoryStream(bytes));
        var pngMeta = roundTrip.Metadata.GetPngMetadata();
        Assert.Empty(pngMeta.TextData);
        Assert.Equal(16, roundTrip.Width);
        Assert.Equal(16, roundTrip.Height);
    }

    [Fact]
    public async Task StripAsync_IsDeterministic_ForGivenInput()
    {
        // Idempotency in practice: re-encoding the same input twice with the
        // same encoder settings must produce identical bytes, so a re-strip
        // of an already-stripped file dedups to the same SHA-256.
        var stripper = CreateStripper();
        var input = ImageFixtures.JpegWithExif();

        using var first = await stripper.StripAsync(new MemoryStream(input), "image/jpeg");
        using var second = await stripper.StripAsync(new MemoryStream(input), "image/jpeg");

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public async Task StripAsync_UnsupportedContentType_Throws()
    {
        var stripper = CreateStripper();
        await Assert.ThrowsAsync<UnsupportedImageFormatException>(
            () => stripper.StripAsync(new MemoryStream(new byte[] { 0xFF }), "image/webp"));
    }

    [Fact]
    public async Task StripAsync_Undecodable_Bytes_Throws_UnsupportedImageFormat()
    {
        var stripper = CreateStripper();
        // Twelve bytes of garbage with a JPEG-claimed type. ImageSharp will
        // throw on decode; the stripper sanitizes to a 415-ready exception.
        await Assert.ThrowsAsync<UnsupportedImageFormatException>(
            () => stripper.StripAsync(
                new MemoryStream(Encoding.ASCII.GetBytes("not-an-image")),
                "image/jpeg"));
    }

    [Fact]
    public async Task StripAsync_Image_Exceeding_Pixel_Limits_Throws()
    {
        var stripper = new ImageSharpMetadataStripper(
            Options.Create(new ImageProcessingOptions
            {
                MaxWidth = 8,
                MaxHeight = 8,
                MaxPixels = 64,
            }));
        var input = ImageFixtures.JpegWithExif(); // 16x16, exceeds 8x8 cap

        await Assert.ThrowsAsync<ImageProcessingLimitException>(
            () => stripper.StripAsync(new MemoryStream(input), "image/jpeg"));
    }

    private static bool ContainsAscii(byte[] bytes, string needle)
    {
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i <= bytes.Length - needleBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needleBytes.Length; j++)
            {
                if (bytes[i + j] != needleBytes[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}

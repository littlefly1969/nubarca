using System.Text.Json;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Pure unit tests for the embedded image metadata extractor — no DB, no host.
public sealed class EmbeddedImageMetadataExtractorTests
{
    private readonly EmbeddedImageMetadataExtractor _extractor = new();

    private ImageMetadataExtractionResult Extract(byte[] bytes)
        => _extractor.Extract(new MemoryStream(bytes));

    // ---- Slice 86: XMP-GPS coordinate parser (improves hasGps coverage) ----

    [Theory]
    [InlineData("37,48.522N", null, 37.8087)]      // deg,decimal-minutes + hemisphere
    [InlineData("122,16.788W", null, -122.2798)]
    [InlineData("37,48.522", "N", 37.8087)]         // hemisphere via separate ref
    [InlineData("8,30.0", "S", -8.5)]
    [InlineData("51,30,30N", null, 51.50833)]       // deg,min,sec
    [InlineData("-33.8688", null, -33.8688)]        // plain signed decimal
    [InlineData("151.2093", "E", 151.2093)]
    public void ParseXmpGpsCoordinate_ParsesCommonForms(string value, string? refValue, double expected)
    {
        var result = EmbeddedImageMetadataExtractor.ParseXmpGpsCoordinate(value, refValue);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-coordinate")]
    [InlineData("N")]
    public void ParseXmpGpsCoordinate_ReturnsNullForUnparseable(string? value)
    {
        Assert.Null(EmbeddedImageMetadataExtractor.ParseXmpGpsCoordinate(value, null));
    }

    [Fact]
    public void Jpeg_With_Exif_Extracts_Normalized_Typed_Fields()
    {
        var result = Extract(ImageFixtures.JpegWithExif());

        Assert.Equal(MetadataStatuses.Completed, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal(EmbeddedImageMetadataExtractor.Version, result.Version);

        Assert.Equal(new DateTime(2023, 6, 15, 14, 30, 0, DateTimeKind.Utc), result.DateTaken);
        Assert.Equal("DateTimeOriginal", result.DateTakenSource);
        Assert.Equal(6, result.Orientation);
        Assert.Equal(ImageFixtures.CameraMake, result.CameraMake);
        Assert.Equal(ImageFixtures.CameraModel, result.CameraModel);
        Assert.Equal(ImageFixtures.LensModel, result.LensModel);
        Assert.Equal(400, result.IsoSpeed);
        Assert.NotNull(result.FNumber);
        Assert.Equal(2.8, result.FNumber!.Value, precision: 2);
        Assert.NotNull(result.ExposureTime);
        Assert.Contains("1/250", result.ExposureTime);
        Assert.NotNull(result.FocalLength);
        Assert.Equal(50.0, result.FocalLength!.Value, precision: 2);
        Assert.Equal("sRGB", result.ColorSpace);
    }

    [Fact]
    public void Jpeg_With_Exif_Extracts_Sensitive_Fields_Internally()
    {
        var result = Extract(ImageFixtures.JpegWithExif(includeSerials: true));

        // Serial numbers ARE extracted (exhaustive extraction); they just must
        // never leave through a normal DTO — that gate is tested elsewhere.
        Assert.Equal(ImageFixtures.BodySerial, result.BodySerialNumber);
        Assert.Equal(ImageFixtures.LensSerial, result.LensSerialNumber);
        Assert.Equal(ImageFixtures.Software, result.Software);
    }

    [Fact]
    public void Jpeg_With_Gps_Extracts_Coordinates()
    {
        var result = Extract(ImageFixtures.JpegWithExif(includeGps: true));

        Assert.NotNull(result.GpsLatitude);
        Assert.NotNull(result.GpsLongitude);
        Assert.Equal(51.5, result.GpsLatitude!.Value, precision: 1);
        // West longitude is negative.
        Assert.True(result.GpsLongitude!.Value < 0);
    }

    [Fact]
    public void Raw_Metadata_Json_Is_Stored_And_Valid_And_Bounded()
    {
        var result = Extract(ImageFixtures.JpegWithExif(includeGps: true));

        Assert.NotNull(result.RawMetadataJson);
        // Valid JSON object.
        using var doc = JsonDocument.Parse(result.RawMetadataJson!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        // Bounded.
        Assert.True(result.RawMetadataJson!.Length <= 64 * 1024);
        // No NUL / control characters leaked into the document.
        Assert.DoesNotContain('\0', result.RawMetadataJson);
    }

    [Fact]
    public void Plain_Png_Completes_With_No_Camera_Fields()
    {
        var result = Extract(ImageFixtures.PlainPng());

        Assert.Equal(MetadataStatuses.Completed, result.Status);
        Assert.Null(result.CameraMake);
        Assert.Null(result.DateTaken);
        Assert.Null(result.GpsLatitude);
        Assert.NotNull(result.RawMetadataJson);
    }

    [Fact]
    public void Unsupported_Bytes_Return_Skipped_With_Sanitized_Code_Without_Throwing()
    {
        var garbage = "this is definitely not an image file at all 1234567890"u8.ToArray();

        var result = Extract(garbage);

        Assert.Equal(MetadataStatuses.Skipped, result.Status);
        Assert.Equal(MetadataErrorCodes.UnsupportedFormat, result.ErrorCode);
    }

    [Fact]
    public void Corrupt_Exif_Is_NonFatal_And_Error_Code_Is_Sanitized()
    {
        var result = Extract(ImageFixtures.JpegWithCorruptExif());

        // Must not throw and must resolve to a safe status.
        Assert.Contains(result.Status, new[]
        {
            MetadataStatuses.Completed, MetadataStatuses.Failed, MetadataStatuses.Skipped,
        });
        // Error code, if any, is one of the known sanitized codes — never raw
        // exception text.
        if (result.ErrorCode is not null)
        {
            Assert.Contains(result.ErrorCode, new[]
            {
                MetadataErrorCodes.UnsupportedFormat,
                MetadataErrorCodes.IoError,
                MetadataErrorCodes.Unexpected,
                MetadataErrorCodes.RawTruncated,
            });
        }
    }

    [Fact]
    public void Empty_Stream_Is_NonFatal()
    {
        var result = Extract(Array.Empty<byte>());

        Assert.Contains(result.Status, new[] { MetadataStatuses.Skipped, MetadataStatuses.Failed });
        Assert.NotNull(result.ErrorCode);
    }
}

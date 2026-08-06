using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Slice 62 — pure header-only video signature detection. No DB, no HTTP.
public sealed class VideoSignatureDetectorTests
{
    private static readonly VideoSignatureDetector Detector = new();

    [Fact]
    public async Task Detects_Mp4_Isom_Brand_As_Mp4()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.MinimalMp4("isom")));
        Assert.NotNull(sig);
        Assert.Equal(VideoSignatureDetector.Mp4ContentType, sig!.ContentType);
        Assert.Equal("MP4", sig.Container);
    }

    [Theory]
    [InlineData("mp42")]
    [InlineData("avc1")]
    [InlineData("iso2")]
    [InlineData("M4V ")]
    public async Task Detects_Common_Mp4_Brands(string brand)
    {
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.MinimalMp4(brand)));
        Assert.NotNull(sig);
        Assert.Equal(VideoSignatureDetector.Mp4ContentType, sig!.ContentType);
    }

    [Fact]
    public async Task Detects_QuickTime_Brand()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.MinimalMov()));
        Assert.NotNull(sig);
        Assert.Equal(VideoSignatureDetector.QuickTimeContentType, sig!.ContentType);
        Assert.Equal("QuickTime", sig.Container);
    }

    [Fact]
    public async Task Detects_Webm_Ebml_Magic_With_DocType()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.MinimalWebm()));
        Assert.NotNull(sig);
        Assert.Equal(VideoSignatureDetector.WebmContentType, sig!.ContentType);
        Assert.Equal("WebM", sig.Container);
    }

    [Fact]
    public async Task Rejects_Plain_Text_Bytes()
    {
        var sig = await Detector.InspectAsync(
            new MemoryStream("plain notes — not a video"u8.ToArray()));
        Assert.Null(sig);
    }

    [Fact]
    public async Task Rejects_Jpeg_Bytes()
    {
        // Real JPEG fixture — its leading bytes are not ftyp/EBML.
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.JpegWithExif()));
        Assert.Null(sig);
    }

    [Fact]
    public async Task Rejects_Png_Bytes()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(ImageFixtures.PlainPng()));
        Assert.Null(sig);
    }

    [Fact]
    public async Task Empty_Stream_Resolves_To_Null()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(Array.Empty<byte>()));
        Assert.Null(sig);
    }

    [Fact]
    public async Task Truncated_Header_Resolves_To_Null()
    {
        var sig = await Detector.InspectAsync(new MemoryStream(new byte[] { 0x1A, 0x45, 0xDF }));
        Assert.Null(sig);
    }

    [Fact]
    public async Task Spoofed_Ftyp_Without_Video_Brand_Resolves_To_Null()
    {
        // Build an ftyp box with a non-video brand ("heic") + no video
        // compatible brand. The detector should reject it.
        var bytes = new byte[]
        {
            0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'h', (byte)'e', (byte)'i', (byte)'c',
            0x00, 0x00, 0x00, 0x01,
            (byte)'m', (byte)'i', (byte)'f', (byte)'1',
            0x00, 0x00, 0x00, 0x00,
        };
        var sig = await Detector.InspectAsync(new MemoryStream(bytes));
        Assert.Null(sig);
    }
}

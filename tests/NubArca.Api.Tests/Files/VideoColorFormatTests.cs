using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

public sealed class VideoColorFormatTests
{
    // What ffprobe says about an iPhone's HDR video (Dolby Vision 8.4 over HLG).
    private const string IphoneHdr = """
        pix_fmt=yuv420p10le
        color_range=tv
        color_space=bt2020nc
        color_transfer=arib-std-b67
        color_primaries=bt2020
        """;

    [Fact]
    public void An_Iphone_Hdr_Video_Is_Tone_Mapped_With_Sdr_White_At_203_Nits()
    {
        var format = VideoColorFormat.Parse(IphoneHdr);

        Assert.True(format.IsHdr);
        Assert.False(format.IsWideGamut);
        Assert.False(format.CanStreamCopy);
        Assert.Equal(
            "zscale=tin=arib-std-b67:pin=bt2020:min=bt2020nc:rin=tv:t=linear:p=bt2020:npl=203,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=tonemap=mobius:param=0.5:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p",
            format.ToBt709Filter());
    }

    [Fact]
    public void Hdr10_Is_Hdr_Too()
    {
        var format = new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "smpte2084", "bt2020");

        Assert.True(format.IsHdr);
        Assert.StartsWith("zscale=tin=smpte2084:pin=bt2020:", format.ToBt709Filter());
    }

    [Fact]
    public void Display_P3_Is_Converted_To_Bt709_Without_Tone_Mapping()
    {
        // An iPhone's SDR video in Display P3, full range.
        var format = new VideoColorFormat("yuvj420p", "pc", "bt709", "bt709", "smpte432");

        Assert.False(format.IsHdr);
        Assert.True(format.IsWideGamut);
        Assert.False(format.CanStreamCopy);
        Assert.Equal(
            "zscale=tin=bt709:pin=smpte432:min=bt709:rin=pc:t=linear:p=smpte432,format=gbrpf32le,zscale=p=bt709,"
            + "zscale=t=bt709:m=bt709:r=tv,format=yuv420p",
            format.ToBt709Filter());
    }

    [Theory]
    [InlineData("yuv420p", "bt709", "bt709")]
    [InlineData("yuvj420p", "smpte170m", "bt470bg")]
    [InlineData("yuv420p", null, null)]
    public void Ordinary_Sdr_Is_Left_As_It_Is(string pixelFormat, string? transfer, string? primaries)
    {
        var format = new VideoColorFormat(pixelFormat, "tv", "bt709", transfer, primaries);

        Assert.Null(format.ToBt709Filter());
        Assert.True(format.CanStreamCopy);
        Assert.Equal("scale=640:640,setsar=1", format.ToBt709Then("scale=640:640,setsar=1"));
    }

    [Fact]
    public void A_Probe_That_Could_Not_Answer_Changes_Nothing()
    {
        Assert.Null(VideoColorFormat.Unknown.ToBt709Filter());
        Assert.True(VideoColorFormat.Unknown.CanStreamCopy);
    }

    [Theory]
    [InlineData("yuv444p")]
    [InlineData("yuv422p10le")]
    [InlineData("yuv420p10le")]
    public void Only_8_Bit_4_2_0_Is_Copied_Into_A_Rendition(string pixelFormat)
    {
        var format = new VideoColorFormat(pixelFormat, "tv", "bt709", "bt709", "bt709");

        Assert.False(format.CanStreamCopy);
        // Its colours need nothing: only the copy is refused.
        Assert.Null(format.ToBt709Filter());
    }

    [Fact]
    public void A_Single_Frame_Is_Made_Even_Then_Converted_Then_Scaled()
    {
        var format = VideoColorFormat.Parse(IphoneHdr);

        var filter = format.ToBt709Then("scale=640:640:force_original_aspect_ratio=decrease,setsar=1");

        Assert.StartsWith("scale=trunc(iw/2)*2:trunc(ih/2)*2,zscale=tin=arib-std-b67:", filter);
        Assert.EndsWith(",format=yuv420p,scale=640:640:force_original_aspect_ratio=decrease,setsar=1", filter);
    }

    [Fact]
    public void An_Unstated_Matrix_Or_Range_Gets_The_Standard_One()
    {
        Assert.StartsWith(
            "zscale=tin=arib-std-b67:pin=bt2020:min=bt2020nc:rin=tv:",
            new VideoColorFormat("yuv420p10le", null, null, "arib-std-b67", "bt2020").ToBt709Filter());
        Assert.StartsWith(
            "zscale=tin=bt709:pin=smpte432:min=bt709:rin=tv:",
            new VideoColorFormat("yuv420p", null, null, "bt709", "smpte432").ToBt709Filter());
        // HDR whose primaries are unsaid is BT.2020: that is what HDR is coded in.
        Assert.StartsWith(
            "zscale=tin=arib-std-b67:pin=bt2020:",
            new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "arib-std-b67", null).ToBt709Filter());
    }

    [Fact]
    public void Nothing_Outside_The_Known_Names_Reaches_The_Filter_Graph()
    {
        // A file can claim anything; only listed names are ever written.
        Assert.Null(new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "arib-std-b67:x=1", "bt2020").ToBt709Filter());
        Assert.Null(new VideoColorFormat("yuv420p", "tv", "bt709", "bt709", "smpte432,drawtext").ToBt709Filter());

        var filter = new VideoColorFormat("yuv420p10le", "pc;x", "bt2020nc[out]", "arib-std-b67", "bt2020'")
            .ToBt709Filter()!;
        Assert.StartsWith("zscale=tin=arib-std-b67:pin=bt2020:min=bt2020nc:rin=tv:", filter);
        Assert.DoesNotContain("[", filter);
        Assert.DoesNotContain("'", filter);
        Assert.DoesNotContain(";", filter);
    }

    [Fact]
    public void Parse_Reads_Unknown_And_Unspecified_As_Nothing()
    {
        var format = VideoColorFormat.Parse("""
            pix_fmt=yuv420p
            color_range=unknown
            color_space=unspecified
            color_transfer=reserved
            color_primaries=
            """);

        Assert.Equal(new VideoColorFormat("yuv420p", null, null, null, null), format);
    }
}

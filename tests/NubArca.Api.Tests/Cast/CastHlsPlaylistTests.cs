using NubArca.Api.Cast;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// The rewriter is where a mistake would be quiet and expensive: it turns
// playlist text into URLs a television will fetch without a cookie. These tests
// pin both halves of its contract — that every legitimate reference is signed,
// and that anything outside the ladder whitelist kills the whole playlist rather
// than being passed through.
public sealed class CastHlsPlaylistTests
{
    private const string Base = "/api/cast/media/11111111-1111-1111-1111-111111111111";
    private const string Token = "s3cret-token_value";

    [Fact]
    public void Master_Rendition_Uris_Become_GrantScoped()
    {
        const string master =
            "#EXTM3U\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\n"
            + "high/stream.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480\n"
            + "low/stream.m3u8\n";

        var rewritten = CastHlsPlaylist.RewriteMaster(master, Base, Token);

        Assert.NotNull(rewritten);
        Assert.Contains($"{Base}/hls/high/stream.m3u8?token={Token}", rewritten, StringComparison.Ordinal);
        Assert.Contains($"{Base}/hls/low/stream.m3u8?token={Token}", rewritten, StringComparison.Ordinal);
        // Tags survive untouched.
        Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080", rewritten,
            StringComparison.Ordinal);
        Assert.StartsWith("#EXTM3U", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Variant_Segments_And_The_Init_Map_Are_Signed()
    {
        const string variant =
            "#EXTM3U\n"
            + "#EXT-X-TARGETDURATION:4\n"
            + "#EXT-X-MAP:URI=\"init_0.mp4\"\n"
            + "#EXTINF:4.000000,\n"
            + "seg-0.m4s\n"
            + "#EXTINF:4.000000,\n"
            + "seg-1.m4s\n"
            + "#EXT-X-ENDLIST\n";

        var rewritten = CastHlsPlaylist.RewriteVariant(variant, Base, "high", Token);

        Assert.NotNull(rewritten);
        Assert.Contains($"#EXT-X-MAP:URI=\"{Base}/hls/high/init_0.mp4?token={Token}\"", rewritten,
            StringComparison.Ordinal);
        Assert.Contains($"{Base}/hls/high/seg-0.m4s?token={Token}", rewritten, StringComparison.Ordinal);
        Assert.Contains($"{Base}/hls/high/seg-1.m4s?token={Token}", rewritten, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Token_Is_Url_Encoded()
    {
        var rewritten = CastHlsPlaylist.RewriteMaster(
            "#EXTM3U\nhigh/stream.m3u8\n", Base, "a+b/c=d");

        Assert.NotNull(rewritten);
        Assert.Contains("?token=a%2Bb%2Fc%3Dd", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    // Traversal, in both plain and percent-encoded forms.
    [InlineData("../master.m3u8")]
    [InlineData("..%2Fmaster.m3u8")]
    [InlineData("%2e%2e/high/stream.m3u8")]
    // An absolute URL pointing anywhere at all.
    [InlineData("https://attacker.test.invalid/stream.m3u8")]
    [InlineData("//attacker.test.invalid/stream.m3u8")]
    [InlineData("/api/files/00000000-0000-0000-0000-000000000000/video")]
    // Unexpected names and rendition directories.
    [InlineData("medium/stream.m3u8")]
    [InlineData("high/evil.sh")]
    [InlineData("high/../../etc/passwd")]
    // A master listing itself would be a resolution loop.
    [InlineData("master.m3u8")]
    public void A_Master_Uri_Outside_The_Ladder_Rejects_The_Whole_Playlist(string uri)
    {
        var rewritten = CastHlsPlaylist.RewriteMaster($"#EXTM3U\n{uri}\n", Base, Token);

        Assert.Null(rewritten);
    }

    [Theory]
    [InlineData("../../master.m3u8")]
    [InlineData("..%2Fseg-0.m4s")]
    [InlineData("https://attacker.test.invalid/seg-0.m4s")]
    [InlineData("seg-x.m4s")]
    [InlineData("payload.exe")]
    public void A_Variant_Uri_Outside_The_Ladder_Rejects_The_Whole_Playlist(string uri)
    {
        var rewritten = CastHlsPlaylist.RewriteVariant($"#EXTM3U\n{uri}\n", Base, "high", Token);

        Assert.Null(rewritten);
    }

    [Fact]
    public void An_Unexpected_Uri_Attribute_Rejects_The_Playlist()
    {
        // #EXT-X-KEY is not something this pipeline produces; a playlist that
        // carried one pointing off-ladder must not be signed and forwarded.
        var rewritten = CastHlsPlaylist.RewriteVariant(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://attacker.test.invalid/key\"\nseg-0.m4s\n",
            Base, "high", Token);

        Assert.Null(rewritten);
    }

    [Fact]
    public void A_Malformed_Uri_Attribute_Rejects_The_Playlist()
    {
        var rewritten = CastHlsPlaylist.RewriteVariant(
            "#EXTM3U\n#EXT-X-MAP:URI=\"init_0.mp4\nseg-0.m4s\n", Base, "high", Token);

        Assert.Null(rewritten);
    }

    [Fact]
    public void Blank_Lines_And_Crlf_Survive_The_Rewrite()
    {
        var rewritten = CastHlsPlaylist.RewriteVariant(
            "#EXTM3U\r\n\r\n#EXTINF:4.0,\r\nseg-0.m4s\r\n", Base, "low", Token);

        Assert.NotNull(rewritten);
        Assert.DoesNotContain('\r', rewritten);
        Assert.Contains($"{Base}/hls/low/seg-0.m4s?token={Token}", rewritten, StringComparison.Ordinal);
    }
}

using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Video-hls slice 1 — hash-sharded HLS ladder store: publish/exists/delete
// lifecycle, publish races, and (critically) the serving whitelist +
// path-traversal defence for the untrusted relative path.
public sealed class HlsDerivativeStorageTests : IDisposable
{
    private static readonly string Sha = new('a', 64);

    private readonly string _root;
    private readonly HlsDerivativeStorage _store;

    public HlsDerivativeStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nc-hls-store-{Guid.NewGuid():N}");
        _store = new HlsDerivativeStorage(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string StageLadder()
    {
        var staging = _store.CreateStagingDirectory();
        File.WriteAllText(Path.Combine(staging, "master.m3u8"), "#EXTM3U master");
        foreach (var name in new[] { "high", "low" })
        {
            var dir = Path.Combine(staging, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stream.m3u8"), "#EXTM3U variant");
            File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x01]);
            File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [0x02]);
        }
        return staging;
    }

    [Fact]
    public void Publish_Makes_Ladder_Exist_And_Servable()
    {
        Assert.False(_store.Exists(Sha));
        _store.Publish(Sha, StageLadder());
        Assert.True(_store.Exists(Sha));

        using var master = _store.OpenServableFile(Sha, "master.m3u8");
        Assert.NotNull(master);
        using var seg = _store.OpenServableFile(Sha, "high/seg-0.m4s");
        Assert.NotNull(seg);
        using var init = _store.OpenServableFile(Sha, "low/init_0.mp4");
        Assert.NotNull(init);
    }

    [Fact]
    public void Lost_Publish_Race_Discards_Staging_And_Keeps_Winner()
    {
        _store.Publish(Sha, StageLadder());
        var loser = StageLadder();
        File.WriteAllText(Path.Combine(loser, "master.m3u8"), "#EXTM3U LOSER");

        _store.Publish(Sha, loser);

        Assert.False(Directory.Exists(loser));
        using var master = _store.OpenServableFile(Sha, "master.m3u8");
        using var reader = new StreamReader(master!);
        Assert.DoesNotContain("LOSER", reader.ReadToEnd());
    }

    [Fact]
    public void Delete_Is_Idempotent()
    {
        _store.Publish(Sha, StageLadder());
        _store.Delete(Sha);
        Assert.False(_store.Exists(Sha));
        _store.Delete(Sha); // second call: no throw
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("high/../master.m3u8")]
    [InlineData("high/../../secrets")]
    [InlineData("HIGH/stream.m3u8")]       // case-sensitive whitelist
    [InlineData("high/evil.sh")]
    [InlineData("high/seg-.m4s")]
    [InlineData("master.m3u8.bak")]
    [InlineData("medium/stream.m3u8")]     // unknown rendition name
    [InlineData("")]
    public void NonWhitelisted_Or_Traversal_Paths_Return_Null(string relative)
    {
        _store.Publish(Sha, StageLadder());
        Assert.Null(_store.OpenServableFile(Sha, relative));
    }

    [Fact]
    public void Missing_Whitelisted_File_Returns_Null()
    {
        _store.Publish(Sha, StageLadder());
        Assert.Null(_store.OpenServableFile(Sha, "high/seg-999.m4s"));
    }

    [Theory]
    [InlineData("not-a-sha")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase
    [InlineData("../../aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Malformed_Sha_Throws(string sha)
    {
        Assert.Throws<ArgumentException>(() => _store.Exists(sha));
        Assert.Throws<ArgumentException>(() => _store.Delete(sha));
    }
}

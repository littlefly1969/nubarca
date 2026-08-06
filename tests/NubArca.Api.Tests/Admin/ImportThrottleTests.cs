using System.Diagnostics;
using System.Text;
using NubArca.Api.Admin;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 83 — unit coverage for the import throttle mechanics (delay hook,
// yield cadence, byte-rate read stream).
public sealed class ImportThrottleTests
{
    [Fact]
    public void ShouldYield_AtConfiguredCadence()
    {
        var t = new ImportThrottle(new AdminImportOptions { YieldEveryFiles = 4 }, TimeProvider.System);
        Assert.False(t.ShouldYield(0));
        Assert.False(t.ShouldYield(3));
        Assert.True(t.ShouldYield(4));
        Assert.True(t.ShouldYield(8));
        Assert.False(t.ShouldYield(5));
        Assert.Equal(4, t.YieldEveryFiles);
    }

    [Fact]
    public async Task BetweenFilesAsync_DelaysWhenConfigured()
    {
        var none = new ImportThrottle(new AdminImportOptions { DelayBetweenFilesMs = 0 }, TimeProvider.System);
        Assert.True(none.BetweenFilesAsync(CancellationToken.None).IsCompleted); // no delay → completes synchronously

        var delayed = new ImportThrottle(new AdminImportOptions { DelayBetweenFilesMs = 40 }, TimeProvider.System);
        var sw = Stopwatch.StartNew();
        await delayed.BetweenFilesAsync(CancellationToken.None);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 25, $"expected a delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Wrap_PassThroughWhenNoRateLimit()
    {
        var t = new ImportThrottle(new AdminImportOptions { MaxBytesPerSecond = 0 }, TimeProvider.System);
        using var src = new MemoryStream(new byte[10]);
        Assert.Same(src, t.Wrap(src));
        Assert.False(t.IsRateLimited);
    }

    [Fact]
    public async Task Wrap_RateLimited_ReturnsAllBytesAndPaces()
    {
        var t = new ImportThrottle(new AdminImportOptions { MaxBytesPerSecond = 2000 }, TimeProvider.System);
        Assert.True(t.IsRateLimited);
        var payload = Encoding.UTF8.GetBytes(new string('x', 400));
        using var src = new MemoryStream(payload);
        await using var wrapped = t.Wrap(src);
        Assert.IsType<ThrottledReadStream>(wrapped);

        var buffer = new byte[payload.Length];
        var sw = Stopwatch.StartNew();
        int total = 0, n;
        while ((n = await wrapped.ReadAsync(buffer.AsMemory(total))) > 0) total += n;
        sw.Stop();

        Assert.Equal(payload.Length, total); // streaming returns every byte
        // 400 bytes at 2000 B/s ≈ 0.2s of pacing; allow generous slack for CI.
        Assert.True(sw.ElapsedMilliseconds >= 80, $"expected pacing, got {sw.ElapsedMilliseconds}ms");
    }
}

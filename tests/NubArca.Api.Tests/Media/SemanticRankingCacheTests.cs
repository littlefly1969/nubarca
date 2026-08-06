using Microsoft.Extensions.Options;
using NubArca.Api.Media.Semantic;
using Xunit;

namespace NubArca.Api.Tests.Media;

// SEARCH-SEM-01: the ranking cache's security and concurrency contract.
//
// Tested directly rather than through the search service so the assertions are
// about OBSERVABLE build behaviour — how many times the expensive ranking ran,
// and for whom — instead of any particular locking primitive.
public sealed class SemanticRankingCacheTests
{
    private static readonly Guid OwnerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Fingerprint = "abc123";

    private static SemanticRankingCache Cache(
        TimeProvider? clock = null, int ttlSeconds = 60, int maxEntries = 64)
        => new(clock ?? TimeProvider.System,
            Options.Create(new SemanticRankingCacheOptions
            {
                TtlSeconds = ttlSeconds,
                MaxEntries = maxEntries,
            }));

    private static SemanticRankingSnapshot Snapshot(int total = 1)
        => new(Array.Empty<SemanticRankedHit>(), false, total, 0, 0);

    [Fact]
    public async Task Concurrent_Identical_First_Pages_Build_The_Ranking_Once()
    {
        var cache = Cache();
        var builds = 0;
        var release = new TaskCompletionSource();

        async Task<SemanticRankingSnapshot> Build(CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            await release.Task;
            return Snapshot();
        }

        // Twelve identical first pages arrive together.
        var racers = Enumerable.Range(0, 12)
            .Select(_ => cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default))
            .ToArray();

        release.SetResult();
        await Task.WhenAll(racers);

        // Exactly one full-library ranking ran, not twelve.
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task A_Failed_Build_Can_Be_Retried()
    {
        var cache = Cache();
        var attempts = 0;

        Task<SemanticRankingSnapshot> Build(CancellationToken ct)
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<SemanticRankingSnapshot>(new InvalidOperationException("boom"))
                : Task.FromResult(Snapshot(total: 7));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default));

        // The failure published nothing and did not poison the key.
        var second = await cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default);
        Assert.Equal(7, second.PhotoCandidatesExamined);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_Cancelled_Build_Can_Be_Retried()
    {
        var cache = Cache();
        var attempts = 0;

        Task<SemanticRankingSnapshot> Build(CancellationToken ct)
        {
            attempts++;
            return attempts == 1
                ? Task.FromCanceled<SemanticRankingSnapshot>(new CancellationToken(true))
                : Task.FromResult(Snapshot(total: 5));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default));

        // No partial entry was published by the cancellation.
        var second = await cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default);
        Assert.Equal(5, second.PhotoCandidatesExamined);
    }

    [Fact]
    public async Task Another_Owner_Never_Joins_The_Same_Build()
    {
        var cache = Cache();
        var buildsByOwner = new Dictionary<Guid, int>();
        var gate = new TaskCompletionSource();
        var seen = new object();

        Func<Guid, Func<CancellationToken, Task<SemanticRankingSnapshot>>> build = owner =>
            async ct =>
            {
                lock (seen)
                {
                    buildsByOwner[owner] = buildsByOwner.GetValueOrDefault(owner) + 1;
                }
                await gate.Task;
                return Snapshot();
            };

        // SAME query fingerprint, DIFFERENT owners, at the same time.
        var a = cache.GetOrBuildAsync(OwnerA, Fingerprint, build(OwnerA), default);
        var b = cache.GetOrBuildAsync(OwnerB, Fingerprint, build(OwnerB), default);
        gate.SetResult();
        await Task.WhenAll(a, b);

        // Each owner ranked their OWN scope; neither reused the other's build.
        Assert.Equal(1, buildsByOwner[OwnerA]);
        Assert.Equal(1, buildsByOwner[OwnerB]);
    }

    [Fact]
    public async Task A_Cached_Ranking_Is_Never_Handed_To_A_Different_Owner()
    {
        var cache = Cache();
        await cache.GetOrBuildAsync(OwnerA, Fingerprint, _ => Task.FromResult(Snapshot(total: 99)), default);

        var built = false;
        var theirs = await cache.GetOrBuildAsync(OwnerB, Fingerprint, _ =>
        {
            built = true;
            return Task.FromResult(Snapshot(total: 1));
        }, default);

        // Owner is part of the KEY, so the second owner missed and ranked their
        // own scope rather than inheriting the first owner's results.
        Assert.True(built);
        Assert.Equal(1, theirs.PhotoCandidatesExamined);
        Assert.False(cache.LastLookupWasHit);
    }

    [Fact]
    public async Task An_Entry_Expires_After_Its_Ttl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = Cache(clock, ttlSeconds: 60);
        var builds = 0;

        Task<SemanticRankingSnapshot> Build(CancellationToken ct)
        {
            builds++;
            return Task.FromResult(Snapshot());
        }

        await cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default);
        Assert.Equal(1, builds);
        Assert.True(cache.LastLookupWasHit);

        clock.Advance(TimeSpan.FromSeconds(31));
        await cache.GetOrBuildAsync(OwnerA, Fingerprint, Build, default);
        Assert.Equal(2, builds);
        Assert.False(cache.LastLookupWasHit);
    }

    [Fact]
    public async Task Entry_Count_Stays_Bounded()
    {
        var cache = Cache(maxEntries: 4);
        for (var i = 0; i < 40; i++)
        {
            await cache.GetOrBuildAsync(
                OwnerA, $"fp-{i}", _ => Task.FromResult(Snapshot()), default);
        }

        // The oldest entries were evicted, so the very first is gone: memory
        // cannot grow with traffic.
        var rebuilt = false;
        await cache.GetOrBuildAsync(OwnerA, "fp-0", _ =>
        {
            rebuilt = true;
            return Task.FromResult(Snapshot());
        }, default);
        Assert.True(rebuilt);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

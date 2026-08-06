using Microsoft.Extensions.Options;

namespace NubArca.Api.Media.Semantic;

// SEARCH-SEM-01: a short-lived, owner-bound cache of ONE completed semantic
// ranking.
//
// Full-library coverage makes the first page genuinely expensive: an unfiltered
// owner query now ranks every eligible photo embedding and every temporal video
// sample instead of a 20k prefix. Doing that again for page 2 would be absurd,
// so the ranked snapshot is cached and every later page is a keyset slice of
// the SAME immutable list.
//
// SECURITY CONTRACT
// -----------------
//   * The key is (ownerUserId, fingerprint). Owner is part of the key, not a
//     property checked afterwards, so a cursor replayed by another account
//     cannot address this account's ranking — it simply misses and that account
//     ranks its own scope. There is no code path that returns an entry to a
//     different owner than the one that built it.
//   * The fingerprint already folds query, profile, kind, filters, segmentation
//     and result-policy version, so a change to any of them is a different
//     ranking, never a stale hit.
//   * The client-facing token stays the existing signed SemanticMediaCursor. It
//     carries a HASH of the query identity, never the query text or filters.
//   * Entries hold FileItem ids, scores and timestamp evidence only — no
//     vectors, no blob ids, no query text.
//
// BOUNDS
// ------
// Entry count is capped and entries expire; eviction is oldest-first plus
// opportunistic expiry sweeps, so memory cannot grow with traffic. A cancelled
// or failed ranking publishes nothing.
//
// DEPLOYMENT NOTE: this is an in-process cache, which is correct for NubArca's
// current single-API topology. Behind more than one API instance, pagination
// would need affinity or a shared store — a later page landing on another
// instance would simply miss the cache and re-rank, which is correct but slow,
// never wrong.
public sealed class SemanticRankingCacheOptions
{
    public const string SectionName = "Semantic:RankingCache";

    public int TtlSeconds { get; set; } = 60;

    public int MaxEntries { get; set; } = 64;
}

public sealed record SemanticRankingSnapshot(
    IReadOnlyList<SemanticRankedHit> Hits,
    bool StillIndexingManyItems,
    int PhotoCandidatesExamined,
    int VideoSamplesExamined,
    int DistinctVideosCovered);

public sealed class SemanticRankingCache
{
    private readonly record struct Key(Guid OwnerUserId, string Fingerprint);

    private sealed class Entry
    {
        public required SemanticRankingSnapshot Snapshot { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required long Sequence { get; init; }
    }

    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Dictionary<Key, SemaphoreSlim> _builders = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly SemanticRankingCacheOptions _options;
    private long _sequence;

    public SemanticRankingCache(
        TimeProvider clock, IOptions<SemanticRankingCacheOptions>? options = null)
    {
        _clock = clock;
        _options = options?.Value ?? new SemanticRankingCacheOptions();
    }

    public bool LastLookupWasHit { get; private set; }

    // SEARCH-SEM-01: drop every cached ranking belonging to one owner.
    //
    // Needed because the "Solo da organizzare" filter makes a result set depend
    // on ALBUM MEMBERSHIP, which the user changes from inside the very grid the
    // ranking describes. Filing a photo into an album must remove it from the
    // filtered view immediately; a 60-second-stale snapshot would leave it
    // sitting there looking unfiled.
    //
    // Owner-scoped and cheap: rankings are per (owner, fingerprint), so this
    // touches nobody else's cache. Disabling the cache whenever the filter is
    // active was the alternative and was rejected — it would re-rank the whole
    // library on every page, which is the exact cost the cache exists to pay
    // once.
    public void InvalidateOwner(Guid ownerUserId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.OwnerUserId == ownerUserId).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    // Returns the cached ranking for (owner, fingerprint) or builds it exactly
    // once. Concurrent identical first pages serialize on a per-key builder
    // lock, so a burst of duplicate requests triggers ONE full-library ranking
    // rather than N.
    public async Task<SemanticRankingSnapshot> GetOrBuildAsync(
        Guid ownerUserId,
        string fingerprint,
        Func<CancellationToken, Task<SemanticRankingSnapshot>> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        var key = new Key(ownerUserId, fingerprint);

        if (TryGet(key, out var cached))
        {
            LastLookupWasHit = true;
            return cached;
        }

        var builder = GetBuilder(key);
        await builder.WaitAsync(cancellationToken);
        try
        {
            // Someone may have finished while we waited.
            if (TryGet(key, out var afterWait))
            {
                LastLookupWasHit = true;
                return afterWait;
            }

            LastLookupWasHit = false;
            // Nothing is published until the ranking completes: a cancelled or
            // faulted build leaves no partial entry behind, so the next caller
            // recomputes rather than serving a truncated ranking.
            var snapshot = await build(cancellationToken);
            Store(key, snapshot);
            return snapshot;
        }
        finally
        {
            builder.Release();
            ReleaseBuilderIfIdle(key, builder);
        }
    }

    private bool TryGet(Key key, out SemanticRankingSnapshot snapshot)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    snapshot = entry.Snapshot;
                    return true;
                }
                _entries.Remove(key);
            }
        }
        snapshot = null!;
        return false;
    }

    private void Store(Key key, SemanticRankingSnapshot snapshot)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            // Opportunistic expiry sweep keeps a quiet instance from holding
            // dead rankings until pressure forces eviction.
            if (_entries.Count >= _options.MaxEntries)
            {
                foreach (var expired in _entries
                    .Where(kv => kv.Value.ExpiresAt <= now)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    _entries.Remove(expired);
                }
            }
            while (_entries.Count >= Math.Max(1, _options.MaxEntries))
            {
                var oldest = _entries.OrderBy(kv => kv.Value.Sequence).First().Key;
                _entries.Remove(oldest);
            }

            _entries[key] = new Entry
            {
                Snapshot = snapshot,
                ExpiresAt = now.AddSeconds(Math.Max(1, _options.TtlSeconds)),
                Sequence = ++_sequence,
            };
        }
    }

    private SemaphoreSlim GetBuilder(Key key)
    {
        lock (_gate)
        {
            if (!_builders.TryGetValue(key, out var builder))
            {
                builder = new SemaphoreSlim(1, 1);
                _builders[key] = builder;
            }
            return builder;
        }
    }

    private void ReleaseBuilderIfIdle(Key key, SemaphoreSlim builder)
    {
        lock (_gate)
        {
            if (builder.CurrentCount == 1 && _builders.TryGetValue(key, out var current)
                && ReferenceEquals(current, builder))
            {
                _builders.Remove(key);
            }
        }
    }
}

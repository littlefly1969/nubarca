using System.Collections.Concurrent;

namespace NubArca.Api.Rag.Retrieval;

/// Keeps ONE built lexical index per domain, rebuilt when the corpus changes.
///
/// The index is expensive to build and cheap to query, and both facts have to
/// be respected at once. Rebuilding per request would make every question pay
/// for the whole corpus; caching forever would make `rag index` require a
/// restart before an operator saw their own change. So the cache is keyed by a
/// SIGNATURE the corpus source computes cheaply, and an index whose signature no
/// longer matches is replaced.
///
/// The per-domain lock is not about correctness — building twice would produce
/// two identical indexes — but about cost: without it, N concurrent questions
/// arriving after a reindex would each build the repository index.
public sealed class RagLexicalIndexCache
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<RagLexicalIndex> GetOrBuildAsync(
        RagDomainKey domain,
        string signature,
        Func<CancellationToken, Task<RagCorpus>> load,
        CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(domain.Value, out var cached)
            && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            return cached.Index;
        }

        var gate = _locks.GetOrAdd(domain.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check: another caller may have built it while we waited.
            if (_entries.TryGetValue(domain.Value, out var afterWait)
                && string.Equals(afterWait.Signature, signature, StringComparison.Ordinal))
            {
                return afterWait.Index;
            }

            var corpus = await load(cancellationToken);
            var index = new RagLexicalIndex(corpus, RagRankingProfiles.For(domain));
            _entries[domain.Value] = new Entry(signature, index);
            return index;
        }
        finally
        {
            gate.Release();
        }
    }

    /// Drops every cached index. Used by the CLI after an indexing run inside
    /// one process, so a `rag index` followed by a `rag query` reads what was
    /// just written.
    public void Clear() => _entries.Clear();

    private readonly record struct Entry(string Signature, RagLexicalIndex Index);
}

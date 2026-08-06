namespace NubArca.Api.Media.Semantic;

// SEARCH-SEM-01: a fixed-capacity "best N so far" accumulator.
//
// This is what lets semantic ranking cover the WHOLE eligible library without
// holding it in memory. Batches of candidates are ranked and offered here; only
// the best `capacity` survive, so peak memory is a function of the policy's
// safety limit, not of library size. Offering 161k video samples costs the same
// as offering 200.
//
// The comparison is the SAME total order pagination uses — score descending,
// then FileItem id ascending — so a result can never sit on one side of the
// accumulator boundary and the other side of the page boundary. Equal scores
// resolve by id, so the outcome does not depend on which batch a candidate
// happened to arrive in.
//
// A binary-search insert into a List is deliberate over a heap: capacity is in
// the low thousands, the list is already in final sorted order (so no sort pass
// afterwards), and the common case once full is a single O(log n) comparison
// that rejects immediately.
public sealed class BoundedTopResults<T>
{
    private readonly List<T> _items;
    private readonly Comparison<T> _betterFirst;
    private readonly int _capacity;

    public BoundedTopResults(int capacity, Comparison<T> betterFirst)
    {
        ArgumentNullException.ThrowIfNull(betterFirst);
        _capacity = Math.Max(1, capacity);
        _betterFirst = betterFirst;
        _items = new List<T>(Math.Min(_capacity, 1024));
    }

    public int Count => _items.Count;

    // True when the accumulator is full AND the candidate cannot displace the
    // current worst entry. Callers can use it to skip work, but correctness
    // never depends on it — Offer performs the same check.
    public bool WouldReject(T candidate)
        => _items.Count >= _capacity && _betterFirst(candidate, _items[^1]) >= 0;

    public void Offer(T candidate)
    {
        if (WouldReject(candidate))
        {
            return;
        }

        var index = _items.BinarySearch(candidate, Comparer<T>.Create(_betterFirst));
        if (index < 0)
        {
            index = ~index;
        }
        _items.Insert(index, candidate);

        if (_items.Count > _capacity)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }

    // Already in final ranked order — no sort needed by the caller.
    public IReadOnlyList<T> ToOrderedList() => _items;
}

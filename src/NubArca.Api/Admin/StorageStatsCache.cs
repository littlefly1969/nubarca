namespace NubArca.Api.Admin;

// Slice 84: a tiny thread-safe holder for the last computed Storage Stats
// response. Registered as a singleton so the short-lived cache survives across
// the scoped StorageStatsService instances. Admin-only data; never serialized
// anywhere except back through the admin endpoint that produced it.
public sealed class StorageStatsCache
{
    private readonly object _gate = new();
    private StorageStatsResponse? _value;
    private DateTime _computedAtUtc;
    private bool _includedPhysical;

    // Returns the cached value when it is younger than ttl, else null. Also
    // reports whether the cached value included the (expensive) physical scan,
    // so a caller that needs the scan can force a recompute on a scan-less hit.
    public (StorageStatsResponse Value, DateTime ComputedAtUtc, bool IncludedPhysical)? TryGet(
        TimeSpan ttl, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_value is not null && nowUtc - _computedAtUtc <= ttl)
            {
                return (_value, _computedAtUtc, _includedPhysical);
            }
            return null;
        }
    }

    public void Set(StorageStatsResponse value, DateTime computedAtUtc, bool includedPhysical)
    {
        lock (_gate)
        {
            _value = value;
            _computedAtUtc = computedAtUtc;
            _includedPhysical = includedPhysical;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _value = null;
            _computedAtUtc = default;
            _includedPhysical = false;
        }
    }
}

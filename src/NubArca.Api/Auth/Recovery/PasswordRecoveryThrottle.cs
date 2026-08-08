using System.Collections.Concurrent;

namespace NubArca.Api.Auth.Recovery;

// Per-normalized-email rate limiting for password-recovery requests.
//
// The endpoint already carries a per-IP limiter; this is the second axis, and
// the two answer different attacks. An IP limit alone lets a distributed caller
// hammer one mailbox; an email limit alone lets one host walk an address list.
//
// It counts the SUBMITTED address whether or not an account exists, so being
// throttled reveals nothing: a 429 means "this address was asked for too often",
// never "this address is real". A single-process in-memory window is the right
// size here — the installation is one API container, and a counter that resets
// on restart costs an attacker a restart they cannot cause.
public sealed class PasswordRecoveryThrottle
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public PasswordRecoveryThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    private sealed class Window
    {
        public DateTimeOffset StartedAt;
        public int Count;
    }

    public bool TryConsume(string normalizedEmail, int permitLimit, TimeSpan window)
    {
        if (permitLimit <= 0)
        {
            return true;
        }

        var now = _clock.GetUtcNow();
        var entry = _windows.GetOrAdd(normalizedEmail, _ => new Window { StartedAt = now, Count = 0 });

        lock (entry)
        {
            if (now - entry.StartedAt >= window)
            {
                entry.StartedAt = now;
                entry.Count = 0;
            }

            if (entry.Count >= permitLimit)
            {
                return false;
            }

            entry.Count++;
        }

        // Sweep opportunistically rather than on a timer: the dictionary only
        // grows with distinct addresses actually submitted, and a background
        // sweeper for a handful of entries would be more moving parts than the
        // problem has.
        if (_windows.Count > 1024)
        {
            PruneExpired(now, window);
        }

        return true;
    }

    private void PruneExpired(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, entry) in _windows)
        {
            bool expired;
            lock (entry)
            {
                expired = now - entry.StartedAt >= window;
            }
            if (expired)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }
}

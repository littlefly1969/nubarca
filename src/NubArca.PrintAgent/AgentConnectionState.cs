namespace NubArca.PrintAgent;

/// <summary>
/// When the agent last reached the NubArca server, as the worker saw it.
///
/// The Print Box's local page reads this rather than asking the server itself:
/// one cloud client per agent, and no second login for a status line.
/// </summary>
public sealed class AgentConnectionState
{
    private long _lastContactTicks;

    public DateTimeOffset? LastContact
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContactTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void MarkContact(DateTimeOffset at) => Interlocked.Exchange(ref _lastContactTicks, at.UtcTicks);

    /// <summary>Reached within <paramref name="window"/> — a few heartbeats' worth.</summary>
    public bool IsConnected(DateTimeOffset now, TimeSpan window) =>
        LastContact is { } last && now - last <= window;
}

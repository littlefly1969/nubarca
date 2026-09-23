using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Auth.Recovery;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// WHERE A ONE-TIME CODE GOES, AND WHAT HAPPENS WHEN IT CANNOT.
///
/// <para>The dispatcher exists so a challenge answers in constant time — a
/// listed address must not take an SMTP round trip longer than an unlisted one,
/// because a slower answer is itself a reading of the owner's guest list. What
/// it must not do in exchange is lose codes quietly: a challenge is already
/// committed by the time it is queued, so a discarded message leaves somebody
/// waiting for six digits that will never arrive.</para>
///
/// <para>Nothing here waits on a timer. A fake sender with a
/// <c>TaskCompletionSource</c> says exactly when work has happened, so these
/// are deterministic rather than merely usually-green.</para>
/// </summary>
public sealed class AlbumShareMailDispatcherTests
{
    [Fact]
    public void A_dropped_code_is_counted_and_logged_and_never_names_the_address()
    {
        var log = new CapturingLogger<AlbumShareMailDispatcher>();
        // Capacity one, nothing draining: the second write has nowhere to go.
        using var dispatcher = new AlbumShareMailDispatcher(log, capacity: 1);
        var linkId = Guid.NewGuid();

        dispatcher.Enqueue(Message("zia@example.com", "482117"), linkId);
        dispatcher.Enqueue(Message("zio@example.com", "913044"), linkId);

        // `TryWrite` returns TRUE under DropWrite, so the old detector saw
        // nothing at all. The runtime's drop callback is what knows.
        Assert.Equal(1, dispatcher.DroppedCapacity);

        var line = Assert.Single(log.Warnings);
        Assert.Contains(linkId.ToString(), line);
        // A log that named either would turn a delivery problem into a
        // disclosure.
        Assert.DoesNotContain("zio@example.com", line);
        Assert.DoesNotContain("913044", line);
        Assert.DoesNotContain("zia@example.com", line);
        Assert.DoesNotContain("482117", line);
    }

    [Fact]
    public void Queueing_never_blocks_the_caller_even_when_the_queue_is_full()
    {
        using var dispatcher = new AlbumShareMailDispatcher(
            NullLogger<AlbumShareMailDispatcher>.Instance, capacity: 1);

        // Far more than the queue holds, on the thread an HTTP request would be
        // on. If this could block, the timing oracle the dispatcher exists to
        // remove would be back — worse, because now it depends on the backlog.
        for (var i = 0; i < 100; i++)
        {
            dispatcher.Enqueue(Message($"g{i}@example.com", "000000"), Guid.NewGuid());
        }

        Assert.Equal(99, dispatcher.DroppedCapacity);
    }

    [Fact]
    public async Task The_drain_keeps_going_after_a_send_throws()
    {
        var sender = new ScriptedSender();
        using var drain = Drain(sender, out var dispatcher);
        await drain.Service.StartAsync(CancellationToken.None);

        // The first send throws, the second is refused, the third succeeds. A
        // drain that stopped on any of them would take everybody behind that
        // message with it — and the next code is somebody else's.
        sender.Script.Enqueue(_ => throw new InvalidOperationException("relay down"));
        sender.Script.Enqueue(_ => false);
        sender.Script.Enqueue(_ => true);

        dispatcher.Enqueue(Message("a@example.com", "111111"), Guid.NewGuid());
        dispatcher.Enqueue(Message("b@example.com", "222222"), Guid.NewGuid());
        dispatcher.Enqueue(Message("c@example.com", "333333"), Guid.NewGuid());

        await sender.WaitForAsync(3);
        Assert.Equal(
            ["a@example.com", "b@example.com", "c@example.com"],
            sender.Seen.Select(m => m.ToAddress).ToArray());

        await drain.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Everything_queued_is_eventually_handed_to_the_sender()
    {
        var sender = new ScriptedSender();
        using var drain = Drain(sender, out var dispatcher);
        await drain.Service.StartAsync(CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            sender.Script.Enqueue(_ => true);
            dispatcher.Enqueue(Message($"g{i}@example.com", "000000"), Guid.NewGuid());
        }

        await sender.WaitForAsync(20);
        Assert.Equal(0, dispatcher.DroppedCapacity);

        await drain.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Shutdown_drains_every_message_already_queued()
    {
        var sender = new ScriptedSender();
        using var drain = Drain(sender, out var dispatcher);
        await drain.Service.StartAsync(CancellationToken.None);

        // Block the drain on the first message, so the rest are certainly still
        // sitting in the channel when shutdown begins — which is exactly the
        // state that used to lose them.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Script.Enqueue(_ => { held.Task.GetAwaiter().GetResult(); return true; });
        for (var i = 0; i < 4; i++) sender.Script.Enqueue(_ => true);
        for (var i = 0; i < 5; i++)
        {
            dispatcher.Enqueue(Message($"g{i}@example.com", "000000"), Guid.NewGuid());
        }
        await sender.WaitForAsync(1);

        var stopping = drain.Service.StopAsync(CancellationToken.None);
        held.SetResult();
        await stopping;

        // WHAT THIS PINS, and what it does not. It pins that a shutdown
        // delivers everything already accepted. It does NOT fail if the reader
        // goes back to taking the stopping token — verified by trying it —
        // because completing the writer makes the reader reach the end of the
        // channel before that token is ever cancelled. The writer completion is
        // the fix; `Enqueue_after_shutdown_is_not_silently_accepted` is the
        // test that fails without it. Reading on `None` remains correct for the
        // case `StopAsync` never runs at all, and is defence this file does not
        // demonstrate.
        Assert.Equal(5, sender.Seen.Count);
        Assert.Equal(0, dispatcher.DroppedShutdown);
    }

    [Fact]
    public async Task Enqueue_after_shutdown_is_not_silently_accepted()
    {
        var sender = new ScriptedSender();
        using var drain = Drain(sender, out var dispatcher);
        await drain.Service.StartAsync(CancellationToken.None);
        await drain.Service.StopAsync(CancellationToken.None);

        dispatcher.Enqueue(Message("late@example.com", "999999"), Guid.NewGuid());

        // Refused and COUNTED. Looking taken and then simply not existing is
        // the one outcome a committed challenge cannot afford.
        Assert.Equal(1, dispatcher.DroppedShutdown);
        Assert.Empty(sender.Seen);
    }

    [Fact]
    public async Task Shutdown_timeout_reports_messages_left_undelivered()
    {
        var sender = new ScriptedSender();
        using var drain = Drain(sender, out var dispatcher);
        await drain.Service.StartAsync(CancellationToken.None);

        // A relay that never answers. The drain cannot finish, so the deadline
        // decides — and what is left behind must be counted rather than
        // forgotten, because somebody is waiting for each of those codes.
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Script.Enqueue(_ => { wedged.Task.GetAwaiter().GetResult(); return true; });
        for (var i = 0; i < 3; i++)
        {
            dispatcher.Enqueue(Message($"g{i}@example.com", "000000"), Guid.NewGuid());
        }
        await sender.WaitForAsync(1);

        // The host's own token is what bounds this in production; cancelling it
        // here is the same path, reached without waiting ten real seconds.
        using var immediate = new CancellationTokenSource();
        await immediate.CancelAsync();
        await drain.Service.StopAsync(immediate.Token);

        // Two never left the queue, and the count says so.
        Assert.Equal(2, dispatcher.DroppedShutdown);
        wedged.SetResult();
    }

    // --- plumbing ----------------------------------------------------------

    private static EmailMessage Message(string to, string code) =>
        new(to, to, "Il tuo codice", $"Ecco il codice: {code}");

    /// <summary>
    /// The dispatcher and its drain, wired the way Program.cs wires them, and
    /// nothing else — a whole host would add startup this test has no opinion
    /// about.
    /// </summary>
    private static DrainHarness Drain(
        IEmailSender sender, out AlbumShareMailDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sender);
        var provider = services.BuildServiceProvider();
        dispatcher = new AlbumShareMailDispatcher(
            NullLogger<AlbumShareMailDispatcher>.Instance);
        return new DrainHarness(
            provider,
            dispatcher,
            new AlbumShareMailService(
                dispatcher,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AlbumShareMailService>.Instance));
    }

    private sealed record DrainHarness(
        ServiceProvider Provider,
        AlbumShareMailDispatcher Dispatcher,
        AlbumShareMailService Service) : IDisposable
    {
        public void Dispose()
        {
            Dispatcher.Dispose();
            Provider.Dispose();
        }
    }

    /// <summary>
    /// A sender that does what the test tells it to, and SAYS when it has —
    /// so nothing here has to guess with a delay.
    /// </summary>
    private sealed class ScriptedSender : IEmailSender
    {
        private readonly List<EmailMessage> _seen = [];
        private readonly List<TaskCompletionSource> _waiters = [];

        public Queue<Func<EmailMessage, bool>> Script { get; } = new();
        public bool IsEnabled => true;

        public IReadOnlyList<EmailMessage> Seen
        {
            get { lock (_seen) return _seen.ToArray(); }
        }

        public Task<bool> SendAsync(
            EmailMessage message, CancellationToken cancellationToken = default)
        {
            Func<EmailMessage, bool>? step;
            lock (_seen)
            {
                _seen.Add(message);
                step = Script.Count > 0 ? Script.Dequeue() : null;
                foreach (var waiter in _waiters) waiter.TrySetResult();
            }
            return Task.FromResult(step?.Invoke(message) ?? true);
        }

        public async Task WaitForAsync(int count, int timeoutMs = 10_000)
        {
            var deadline = Task.Delay(timeoutMs);
            while (true)
            {
                TaskCompletionSource waiter;
                lock (_seen)
                {
                    if (_seen.Count >= count) return;
                    waiter = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(waiter);
                }
                // Signalled by the sender itself, never by a fixed sleep: the
                // test finishes the moment the work does.
                if (await Task.WhenAny(waiter.Task, deadline) == deadline)
                {
                    Assert.Fail($"expected {count} sends, saw {Seen.Count}");
                }
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}

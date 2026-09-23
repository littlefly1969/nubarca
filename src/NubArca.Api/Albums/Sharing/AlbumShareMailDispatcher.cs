using System.Threading.Channels;
using NubArca.Api.Auth.Recovery;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// Hands a share code to the mail server WITHOUT the request waiting for it.
///
/// <para><b>Why the request must not wait.</b> A challenge for an address the
/// owner listed did an SMTP round trip before answering; one for an address
/// nobody listed returned as soon as the lookup missed. That difference is
/// measurable from outside, and a slower answer means "this address is on the
/// list" — the same guest-list oracle the status codes were already carefully
/// hiding, leaking through the clock instead.</para>
///
/// <para><b>Why a channel and not <c>Task.Run</c>.</b> Fire-and-forget is
/// unowned work: nothing bounds it, nothing observes it, and a shutdown drops
/// whatever was in flight without a word. A bounded channel drained by a hosted
/// service is the same non-blocking behaviour with an owner — back-pressure
/// when a mail server is slow, a count for everything discarded, a drain that
/// finishes what it accepted, and a seam tests can wait on.</para>
///
/// <para><b>Every code has exactly one observable end.</b> It is delivered, or
/// it is counted under one of three headings — and the three are kept apart
/// because they ask an operator for different things: a full queue is a mail
/// server that has stopped, a refused send is a relay that answered no, and a
/// shutdown loss is a deploy that did not wait.</para>
/// </summary>
public interface IAlbumShareMailDispatcher
{
    /// <summary>
    /// Queues one message. Returns immediately, and returns the SAME way
    /// whether or not the queue accepted it — a caller must never be able to
    /// tell, because only a listed address ever reaches this call.
    /// </summary>
    void Enqueue(EmailMessage message, Guid linkId);
}

public sealed class AlbumShareMailDispatcher : IAlbumShareMailDispatcher, IDisposable
{
    /// <summary>
    /// Bounded, and small. A share sends one code per address per minute at
    /// most; a backlog past this is a mail server that has stopped, not a busy
    /// evening, and queueing thousands would only turn that into memory.
    /// </summary>
    public const int DefaultCapacity = 256;

    private readonly Channel<QueuedCode> _channel;
    private readonly ILogger<AlbumShareMailDispatcher> _logger;

    private int _droppedCapacity;
    private int _droppedShutdown;
    private int _undeliveredSend;

    /// <summary>Codes discarded because the queue was full.</summary>
    public int DroppedCapacity => Volatile.Read(ref _droppedCapacity);

    /// <summary>Codes still queued when the drain was forced to stop.</summary>
    public int DroppedShutdown => Volatile.Read(ref _droppedShutdown);

    /// <summary>Codes the mail server refused or failed to take.</summary>
    public int UndeliveredSend => Volatile.Read(ref _undeliveredSend);

    public AlbumShareMailDispatcher(
        ILogger<AlbumShareMailDispatcher> logger, int capacity = DefaultCapacity)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<QueuedCode>(
            new BoundedChannelOptions(capacity)
            {
                // DROP RATHER THAN BLOCK. The caller is an HTTP request that
                // must answer in constant time; making it wait for a full queue
                // would reintroduce the very timing difference this class
                // removes.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            // THE DROP CALLBACK IS THE ONLY HONEST DETECTOR.
            //
            // `TryWrite` returns TRUE under `DropWrite` — the write is accepted
            // and the item is thrown away — so checking its result found
            // nothing, and every discarded code was lost in silence while the
            // challenge that produced it sat committed in the database. The
            // runtime hands the dropped item to this callback instead, which is
            // the one place that knows.
            OnDroppedForCapacity);
    }

    public ChannelReader<QueuedCode> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message, Guid linkId)
    {
        // A CLOSED QUEUE REFUSES, AND SAYS SO. `TryWrite` returns false once
        // the writer is complete, and that is a different thing from a capacity
        // drop: the code was never accepted at all. Counting it under shutdown
        // is what stops a message looking taken and then simply not existing.
        if (_channel.Writer.TryWrite(new QueuedCode(message, linkId))) return;

        Interlocked.Increment(ref _droppedShutdown);
        _logger.LogWarning(
            "album.share.code.refused LinkId={LinkId} Reason=closed DroppedShutdown={Count}",
            linkId, DroppedShutdown);
    }

    /// <summary>
    /// Stops accepting, so the drain has a finite amount of work to do.
    ///
    /// <para>Called at the START of shutdown, before anything waits: the reader
    /// then runs to the end of the channel on its own rather than being
    /// cancelled part-way through a queue it had already accepted.</para>
    /// </summary>
    public void CompleteWriting() => _channel.Writer.TryComplete();

    /// <summary>
    /// Counts what a forced shutdown left behind.
    ///
    /// <para>Reached only when the drain hit its deadline. The codes are still
    /// in the channel and nobody will send them, so the one thing left worth
    /// doing is saying how many — an operator who deployed over a slow relay
    /// deserves to know that some people are waiting for a code that is not
    /// coming, rather than to find out from them.</para>
    /// </summary>
    public void CountAbandoned()
    {
        var abandoned = 0;
        while (_channel.Reader.TryRead(out var item))
        {
            abandoned++;
            Interlocked.Increment(ref _droppedShutdown);
            _logger.LogWarning(
                "album.share.code.abandoned LinkId={LinkId} Reason=shutdown", item.LinkId);
        }
        if (abandoned > 0)
        {
            _logger.LogWarning(
                "album.share.code.shutdown Abandoned={Abandoned} DroppedShutdown={Total}",
                abandoned, DroppedShutdown);
        }
    }

    /// <summary>Records a code the mail server would not take.</summary>
    public void CountUndelivered(Guid linkId)
    {
        Interlocked.Increment(ref _undeliveredSend);
        _logger.LogWarning(
            "album.share.code.undelivered LinkId={LinkId} UndeliveredSend={Count}",
            linkId, UndeliveredSend);
    }

    private void OnDroppedForCapacity(QueuedCode item)
    {
        Interlocked.Increment(ref _droppedCapacity);
        // The link and the running total. NEVER the address and never the
        // code: a log that named either would turn a delivery problem into a
        // disclosure, and this line exists to be read by an operator who has no
        // business with either.
        _logger.LogWarning(
            "album.share.code.dropped LinkId={LinkId} Reason=capacity DroppedCapacity={Count}",
            item.LinkId, DroppedCapacity);
    }

    public void Dispose() => CompleteWriting();
}

/// <summary>One code on its way out. The link identifies it; nothing else does.</summary>
public readonly record struct QueuedCode(EmailMessage Message, Guid LinkId);

/// <summary>
/// Drains the dispatcher, one message at a time.
///
/// <para>Serial on purpose: a self-hosted installation's SMTP relay is usually
/// one connection, and parallelism here buys nothing while making a slow server
/// look like a broken one.</para>
///
/// <para><b>Shutdown finishes what was accepted.</b> The drain used to read
/// with the host's stopping token, so a deploy could cancel it mid-queue and
/// codes that had been accepted — and whose challenges were already committed —
/// simply ceased to exist, without passing through any drop path. Now stopping
/// completes the WRITER and lets the reader run to the end of the channel; the
/// stopping token only bounds how long that may take, because an installation
/// must still be able to restart over an unreachable relay.</para>
/// </summary>
public sealed class AlbumShareMailService : BackgroundService
{
    /// <summary>
    /// How long a drain may hold up a restart. Long enough for a queue of codes
    /// against a healthy relay, short enough that a dead one does not wedge a
    /// deploy — and whatever is left is counted rather than forgotten.
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly AlbumShareMailDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AlbumShareMailService> _logger;

    public AlbumShareMailService(
        AlbumShareMailDispatcher dispatcher,
        IServiceScopeFactory scopes,
        ILogger<AlbumShareMailService> logger)
    {
        _dispatcher = dispatcher;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // NOT `ReadAllAsync(stoppingToken)`. The token is what STOPS accepting,
        // not what stops delivering: reading to the end of a completed channel
        // is the whole point of completing it.
        await foreach (var item in _dispatcher.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                // A token of its own, so one unreachable relay cannot wedge the
                // drain — and NOT the stopping token, because a message already
                // taken off the queue deserves its attempt.
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (!await email.SendAsync(item.Message, attempt.Token))
                {
                    _dispatcher.CountUndelivered(item.LinkId);
                }
            }
            catch (Exception ex)
            {
                // A failed send never stops the drain: the next code is
                // somebody else's, and they are still waiting for it.
                _logger.LogWarning(
                    ex, "album.share.code.undelivered LinkId={LinkId}", item.LinkId);
                _dispatcher.CountUndelivered(item.LinkId);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 1. Stop accepting, so the drain has a finite amount of work.
        _dispatcher.CompleteWriting();

        // 2. Let it finish, bounded — a restart must not hang on a dead relay.
        var executing = ExecuteTask;
        if (executing is not null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(AlbumShareMailService.DrainTimeout);
            var completed = await Task.WhenAny(
                executing, Task.Delay(Timeout.Infinite, deadline.Token));
            if (completed != executing)
            {
                // 3. Whatever is still queued will never be sent. Say how much.
                _dispatcher.CountAbandoned();
            }
        }

        await base.StopAsync(cancellationToken);
    }
}

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
/// when a mail server is slow, a log line when something is dropped, and a
/// graceful drain on shutdown. It also gives tests a seam they can wait on,
/// which fire-and-forget never did.</para>
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
    private const int Capacity = 256;

    private readonly Channel<(EmailMessage Message, Guid LinkId)> _channel =
        Channel.CreateBounded<(EmailMessage, Guid)>(new BoundedChannelOptions(Capacity)
        {
            // DROP RATHER THAN BLOCK. The caller is an HTTP request that must
            // answer in constant time; making it wait for a full queue would
            // reintroduce the very timing difference this class removes.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly ILogger<AlbumShareMailDispatcher> _logger;

    public AlbumShareMailDispatcher(ILogger<AlbumShareMailDispatcher> logger) => _logger = logger;

    public ChannelReader<(EmailMessage Message, Guid LinkId)> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message, Guid linkId)
    {
        if (!_channel.Writer.TryWrite((message, linkId)))
        {
            // The line names the link, never the address and never the code.
            _logger.LogWarning("album.share.code.dropped LinkId={LinkId}", linkId);
        }
    }

    public void Dispose() => _channel.Writer.TryComplete();
}

/// <summary>
/// Drains the dispatcher, one message at a time.
///
/// <para>Serial on purpose: a self-hosted installation's SMTP relay is usually
/// one connection, and parallelism here buys nothing while making a slow server
/// look like a broken one.</para>
/// </summary>
public sealed class AlbumShareMailService : BackgroundService
{
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
        await foreach (var item in _dispatcher.Reader.ReadAllAsync(stoppingToken))
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
                    _logger.LogWarning(
                        "album.share.code.undelivered LinkId={LinkId}", item.LinkId);
                }
            }
            catch (Exception ex)
            {
                // A failed send never stops the drain: the next code is
                // somebody else's, and they are still waiting for it.
                _logger.LogWarning(
                    ex, "album.share.code.undelivered LinkId={LinkId}", item.LinkId);
            }
        }
    }
}

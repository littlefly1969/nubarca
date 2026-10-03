namespace NubArca.PrintAgent.Networking;

public enum ProvisioningMode { Starting, Connected, AccessPoint, Disconnected, Unavailable }

public enum ConnectOutcome { Connecting, Connected, Failed }

/// <summary>The last phone-initiated connection attempt. The SSID only — never the password.</summary>
public sealed record ConnectAttempt(string Ssid, ConnectOutcome Outcome, DateTimeOffset At);

public sealed record ProvisioningState(
    ProvisioningMode Mode,
    bool ManagerAvailable,
    NetworkUplink? Uplink,
    string AccessPointSsid,
    string? AccessPointAddress,
    ConnectAttempt? LastAttempt);

/// <summary>
/// Unavailable: the Wi-Fi cannot be configured now — the box is neither in
/// setup mode nor on Ethernet (on Wi-Fi, changing network would cut its uplink).
/// </summary>
public enum ConnectRequest { Accepted, Unavailable, Busy }

/// <summary>
/// Where a phone's attempt started, fixed when it is accepted: from setup
/// mode the radio must be handed over and the setup network comes back on
/// failure; over Ethernet the box stays reachable and nothing else moves.
/// </summary>
public enum WifiProvisioningOrigin { AccessPoint, Ethernet }

/// <summary>Timings the service runs on; tests shrink them, production reads the options.</summary>
public sealed record ProvisioningTimings(
    TimeSpan Grace,
    TimeSpan Poll,
    TimeSpan Verify,
    TimeSpan HandOff,
    TimeSpan FirstRetry,
    TimeSpan MaxRetry,
    TimeSpan UserQuiet)
{
    public static ProvisioningTimings From(NetworkProvisioningOptions options) => new(
        Grace: TimeSpan.FromSeconds(options.ConnectionGraceSeconds),
        Poll: TimeSpan.FromSeconds(3),
        Verify: TimeSpan.FromSeconds(30),
        HandOff: TimeSpan.FromSeconds(2),
        FirstRetry: TimeSpan.FromMinutes(3),
        MaxRetry: TimeSpan.FromMinutes(30),
        UserQuiet: TimeSpan.FromMinutes(5));
}

/// <summary>
/// Keeps a headless box reachable: on a network if it can find one, otherwise
/// offering its own setup network so a phone can give it one.
///
/// NetworkManager does the networking; this only decides WHEN the setup network
/// exists. The rules, in order:
///
/// - A usable connection — an address and a default route, Ethernet or Wi-Fi —
///   means no setup network, ever. Whether the NubArca server answers is the
///   agent's business, not a reason to open one.
/// - At boot, and after a working network is lost, NetworkManager gets the grace
///   period to (re)connect by itself before the setup network opens.
/// - A phone's connection attempt from the setup network closes it (one
///   radio), tries, and on ANY failure removes the failed profile and reopens
///   the setup network: a wrong password must never leave the box unreachable.
/// - Over Ethernet the Wi-Fi can be set up too, without touching the cable: a
///   failed attempt only removes its profile — the box is reachable already,
///   so no setup network opens. On Wi-Fi it cannot: one radio, and changing
///   network would cut the very connection the page is served over.
/// - While the setup network is up and nobody is using it, known networks are
///   retried now and then with a growing interval, so a router that was merely
///   late does not strand the box — and the retries cannot oscillate.
/// </summary>
public sealed class NetworkProvisioningService : BackgroundService
{
    private readonly INetworkManager _network;
    private readonly ILogger<NetworkProvisioningService> _logger;
    private readonly ProvisioningTimings _timings;
    private readonly string? _accessPointPassword;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private ProvisioningState _state;
    private IReadOnlyList<WifiNetwork> _networks = [];
    private DateTimeOffset? _lostSince;
    private DateTimeOffset _nextRetry = DateTimeOffset.MaxValue;
    private TimeSpan _retryInterval;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private int _connecting;
    private bool _unavailableLogged;
    private Task _pendingConnect = Task.CompletedTask;
    private long _lastScanTicks;

    /// <summary>A scan costs the radio a moment; a page reloaded in a loop must not.</summary>
    private static readonly TimeSpan MinScanInterval = TimeSpan.FromSeconds(10);

    public NetworkProvisioningService(INetworkManager network, NetworkProvisioningOptions options,
        ILogger<NetworkProvisioningService> logger, ProvisioningTimings? timings = null,
        string? boxSuffix = null)
    {
        _network = network;
        _logger = logger;
        _timings = timings ?? ProvisioningTimings.From(options);
        _retryInterval = _timings.FirstRetry;
        _accessPointPassword = string.IsNullOrEmpty(options.AccessPointPassword) ? null : options.AccessPointPassword;
        var ssid = $"{options.AccessPointPrefix}-{boxSuffix ?? BoxIdentity.ReadSuffix()}";
        _state = new ProvisioningState(ProvisioningMode.Starting, false, null, ssid, null, null);
    }

    public ProvisioningState State { get { lock (_stateLock) return _state; } }

    /// <summary>
    /// Whether the page may scan and configure the Wi-Fi now: in setup mode,
    /// or connected over Ethernet — never while Wi-Fi is the uplink.
    /// </summary>
    public bool CanConfigureWifi { get { lock (_stateLock) return CanConfigure(_state); } }

    private static bool CanConfigure(ProvisioningState state) =>
        state.Mode == ProvisioningMode.AccessPoint
        || (state.Mode == ProvisioningMode.Connected && state.Uplink?.Type == "ethernet");

    /// <summary>The networks last seen; a single radio in setup mode often cannot rescan.</summary>
    public IReadOnlyList<WifiNetwork> Networks { get { lock (_stateLock) return _networks; } }

    /// <summary>The phone's connection attempt, for tests to await; the endpoint never waits.</summary>
    public Task PendingConnect { get { lock (_stateLock) return _pendingConnect; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_accessPointPassword is null)
            _logger.LogWarning("The setup network {Ssid} is OPEN: anyone nearby can join it. Set PrintAgent:NetworkProvisioning:AccessPointPassword for an installation.",
                State.AccessPointSsid);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never take the agent down with it: printing does not depend on
                // this service, and a box that stops trying is worse than one
                // that tries again in a moment.
                _logger.LogError("Network provisioning failed ({ExceptionType}: {Message}); retrying.",
                    ex.GetType().Name, ex.Message);
                await Task.Delay(_timings.Poll * 3, stoppingToken);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await BootAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_timings.Poll, ct);
            await TickAsync(ct);
        }
    }

    /// <summary>Wait for NetworkManager to be there, then give it the grace period.</summary>
    internal async Task BootAsync(CancellationToken ct)
    {
        NetworkSnapshot snapshot;
        while (!(snapshot = await _network.GetSnapshotAsync(ct)).ManagerAvailable)
        {
            Update(s => s with { Mode = ProvisioningMode.Unavailable, ManagerAvailable = false });
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                _logger.LogCritical("Network provisioning unavailable: NetworkManager is not running. The setup network cannot open until it is.");
            }
            await Task.Delay(_timings.Poll * 5, ct);
        }
        if (_unavailableLogged)
        {
            _unavailableLogged = false;
            _logger.LogInformation("NetworkManager available.");
        }

        var deadline = DateTimeOffset.UtcNow + _timings.Grace;
        while (snapshot.Uplink is null && DateTimeOffset.UtcNow < deadline)
        {
            Update(s => s with { Mode = ProvisioningMode.Starting, ManagerAvailable = true });
            await Task.Delay(_timings.Poll, ct);
            snapshot = await _network.GetSnapshotAsync(ct);
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (snapshot.Uplink is not null) MarkConnected(snapshot.Uplink!);
            else await OpenAccessPointAsync(snapshot, "no network after the grace period", ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>One look at the network, and whatever that look calls for.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // a phone's attempt owns the radio
        try
        {
            var snapshot = await _network.GetSnapshotAsync(ct);
            if (!snapshot.ManagerAvailable)
            {
                Update(s => s with { Mode = ProvisioningMode.Unavailable, ManagerAvailable = false });
                return;
            }
            var now = DateTimeOffset.UtcNow;
            switch (State.Mode)
            {
                case ProvisioningMode.AccessPoint:
                    if (snapshot.Uplink is not null)
                    {
                        // Ethernet plugged in, or NetworkManager found a network by
                        // itself: the setup network has nothing left to do.
                        await _network.StopAccessPointAsync(ct);
                        MarkConnected(snapshot.Uplink!);
                    }
                    else if (!snapshot.AccessPointActive)
                    {
                        await OpenAccessPointAsync(snapshot, "the setup network dropped", ct);
                    }
                    else if (now >= _nextRetry && now - _lastUserActivity >= _timings.UserQuiet)
                    {
                        await RetryKnownNetworksAsync(snapshot, ct);
                    }
                    break;

                case ProvisioningMode.Disconnected:
                    if (snapshot.Uplink is not null) MarkConnected(snapshot.Uplink!);
                    else if (now >= _nextRetry) await OpenAccessPointAsync(snapshot, "retrying the setup network", ct);
                    break;

                default:
                    if (snapshot.Uplink is not null)
                    {
                        if (_lostSince is not null) _logger.LogInformation("Network connected again.");
                        MarkConnected(snapshot.Uplink!);
                    }
                    else
                    {
                        if (_lostSince is null)
                        {
                            _lostSince = now;
                            _logger.LogWarning("Network lost; NetworkManager has {Grace}s to reconnect before the setup network opens.",
                                (int)_timings.Grace.TotalSeconds);
                        }
                        Update(s => s with { Uplink = null });
                        if (now - _lostSince >= _timings.Grace)
                            await OpenAccessPointAsync(snapshot, "the network did not come back", ct);
                    }
                    break;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A phone asked to join <paramref name="ssid"/>. Answers at once: with one
    /// radio the setup network goes down during the attempt, so the phone would
    /// never see a reply that waited for the outcome. It learns the outcome from
    /// <see cref="State"/> — over Ethernet straight away, from setup mode if the
    /// setup network comes back.
    /// </summary>
    public ConnectRequest RequestConnect(string ssid, string? password)
    {
        lock (_stateLock)
        {
            if (!CanConfigure(_state)) return ConnectRequest.Unavailable;
            if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0) return ConnectRequest.Busy;
            // Fixed now: by the time the attempt runs the state may have moved.
            var origin = _state.Mode == ProvisioningMode.AccessPoint
                ? WifiProvisioningOrigin.AccessPoint
                : WifiProvisioningOrigin.Ethernet;
            _lastUserActivity = DateTimeOffset.UtcNow;
            _state = _state with { LastAttempt = new ConnectAttempt(ssid, ConnectOutcome.Connecting, DateTimeOffset.UtcNow) };
            _pendingConnect = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_timings.HandOff);
                    await ConnectNowAsync(ssid, password, origin, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Wi-Fi connection attempt aborted ({ExceptionType}).", ex.GetType().Name);
                }
                finally
                {
                    Interlocked.Exchange(ref _connecting, 0);
                }
            });
        }
        return ConnectRequest.Accepted;
    }

    /// <summary>Someone is looking at the setup page: no background retry for a while.</summary>
    public void NoteUserActivity()
    {
        lock (_stateLock) _lastUserActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Tries a fresh scan whenever the Wi-Fi can be configured — in setup mode
    /// or over Ethernet — at most every <see cref="MinScanInterval"/>; otherwise,
    /// or when the radio cannot scan as an access point, the last list stands.
    /// </summary>
    public async Task<IReadOnlyList<WifiNetwork>> RefreshNetworksAsync(CancellationToken ct)
    {
        NoteUserActivity();
        if (!CanConfigureWifi) return Networks;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastScanTicks);
        if (last != 0 && now - last < MinScanInterval.TotalMilliseconds) return Networks;
        if (Interlocked.CompareExchange(ref _lastScanTicks, now, last) != last) return Networks;
        var snapshot = await _network.GetSnapshotAsync(ct);
        if (snapshot.WifiInterface is null) return Networks;
        var found = await _network.ScanAsync(snapshot.WifiInterface, ct);
        if (found.Count > 0) lock (_stateLock) _networks = found;
        return Networks;
    }

    private async Task ConnectNowAsync(string ssid, string? password, WifiProvisioningOrigin origin,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await _network.GetSnapshotAsync(ct);
            if (snapshot.WifiInterface is null)
            {
                Finish(ssid, ConnectOutcome.Failed);
                return;
            }
            var fromAccessPoint = origin == WifiProvisioningOrigin.AccessPoint;
            _logger.LogInformation("Wi-Fi connection attempt to {Ssid} ({Origin}).", ssid,
                fromAccessPoint ? "from the setup network" : "over Ethernet");
            // From setup mode the one radio is the setup network: hand it over.
            // Over Ethernet there is no setup network, and the cable is not touched.
            if (fromAccessPoint) await _network.StopAccessPointAsync(ct);
            var activated = await _network.ConnectAsync(snapshot.WifiInterface, ssid, password, ct);
            // Joined means THIS interface on THIS attempt's profile, with an
            // address — and, when it is to be the box's only way out, a gateway.
            // Never that something else (a cable) carries traffic.
            var joined = activated
                ? await WaitForWifiAsync(snapshot.WifiInterface, ssid, requireGateway: fromAccessPoint, _timings.Verify, ct)
                : null;
            if (joined is not null)
            {
                _logger.LogInformation("Wi-Fi connection succeeded: {Ssid}.", ssid);
                // Over Ethernet the cable stays the uplink it was; the state follows
                // whatever NetworkManager prefers at the next look.
                if (fromAccessPoint) MarkConnected(new NetworkUplink(snapshot.WifiInterface, "wifi", ssid, joined.Address));
                Finish(ssid, ConnectOutcome.Connected);
                return;
            }

            await _network.ForgetAsync(ssid, ct);
            Finish(ssid, ConnectOutcome.Failed);
            if (!fromAccessPoint)
            {
                // Reachable over the cable already: a setup network adds nothing.
                _logger.LogWarning("Wi-Fi connection failed: {Ssid}. Still connected over Ethernet.", ssid);
                return;
            }
            _logger.LogWarning("Wi-Fi connection failed: {Ssid}. Restoring the setup network.", ssid);
            await OpenAccessPointAsync(await _network.GetSnapshotAsync(ct), "the connection attempt failed", ct);
            if (State.Mode == ProvisioningMode.AccessPoint) _logger.LogInformation("Setup network restored.");
        }
        finally { _gate.Release(); }
    }

    private async Task RetryKnownNetworksAsync(NetworkSnapshot snapshot, CancellationToken ct)
    {
        _logger.LogInformation("Setup network pausing so NetworkManager can try the known networks.");
        await _network.StopAccessPointAsync(ct);
        var connected = await WaitForUplinkAsync(_timings.Grace, ct);
        if (connected is not null)
        {
            MarkConnected(connected.Uplink!);
            return;
        }
        _retryInterval = TimeSpan.FromTicks(Math.Min(_retryInterval.Ticks * 2, _timings.MaxRetry.Ticks));
        await OpenAccessPointAsync(await _network.GetSnapshotAsync(ct), "no known network came back", ct);
    }

    /// <summary>
    /// Waits for <paramref name="wifiInterface"/> to be connected on this box's
    /// own profile for <paramref name="ssid"/>, with an address — and, when
    /// asked, a gateway. Any other uplink, or another Wi-Fi profile, is not it.
    /// </summary>
    private async Task<WifiClientState?> WaitForWifiAsync(string wifiInterface, string ssid, bool requireGateway,
        TimeSpan within, CancellationToken ct)
    {
        var profile = NetworkManagerCli.ClientConnectionPrefix + ssid;
        var deadline = DateTimeOffset.UtcNow + within;
        while (true)
        {
            var state = await _network.GetWifiClientStateAsync(wifiInterface, ct);
            if (state.Connected && state.ConnectionName == profile && state.Address is not null
                && (!requireGateway || state.HasGateway))
            {
                return state;
            }
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(_timings.Poll, ct);
        }
    }

    /// <summary>Any usable network at all: what retrying the KNOWN networks waits for.</summary>
    private async Task<NetworkSnapshot?> WaitForUplinkAsync(TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        while (true)
        {
            var snapshot = await _network.GetSnapshotAsync(ct);
            if (snapshot.Uplink is not null) return snapshot;
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(_timings.Poll, ct);
        }
    }

    private async Task OpenAccessPointAsync(NetworkSnapshot snapshot, string reason, CancellationToken ct)
    {
        _lostSince = null;
        if (snapshot.WifiInterface is null)
        {
            _logger.LogError("No Wi-Fi interface: the setup network cannot open ({Reason}).", reason);
            MarkDisconnected();
            return;
        }
        // Scan while the radio is still a client: in access-point mode many
        // adapters cannot, and the phone needs a list to choose from.
        var found = await _network.ScanAsync(snapshot.WifiInterface, ct);
        if (found.Count > 0)
        {
            lock (_stateLock) _networks = found;
            _logger.LogInformation("Wi-Fi scan completed: {Count} networks.", found.Count);
        }

        var ssid = State.AccessPointSsid;
        _logger.LogInformation("Setup network starting: {Ssid} ({Reason}).", ssid, reason);
        if (!await _network.StartAccessPointAsync(snapshot.WifiInterface, ssid, _accessPointPassword, ct))
        {
            _logger.LogError("Setup network {Ssid} could not start; retrying later.", ssid);
            MarkDisconnected();
            return;
        }
        var active = await _network.GetSnapshotAsync(ct);
        Update(s => s with
        {
            Mode = ProvisioningMode.AccessPoint, ManagerAvailable = true, Uplink = null,
            AccessPointAddress = active.AccessPointAddress,
        });
        _nextRetry = DateTimeOffset.UtcNow + _retryInterval;
        _logger.LogInformation("Setup network active: {Ssid}.", ssid);
    }

    private void MarkConnected(NetworkUplink uplink)
    {
        var wasConnected = State.Mode == ProvisioningMode.Connected;
        _lostSince = null;
        _retryInterval = _timings.FirstRetry;
        _nextRetry = DateTimeOffset.MaxValue;
        Update(s => s with
        {
            Mode = ProvisioningMode.Connected, ManagerAvailable = true, Uplink = uplink,
            AccessPointAddress = null,
        });
        if (!wasConnected)
            _logger.LogInformation("Network connected: {Type} {Name}.", uplink.Type, uplink.Name ?? uplink.Device);
    }

    private void MarkDisconnected()
    {
        _nextRetry = DateTimeOffset.UtcNow + _retryInterval;
        _retryInterval = TimeSpan.FromTicks(Math.Min(_retryInterval.Ticks * 2, _timings.MaxRetry.Ticks));
        Update(s => s with { Mode = ProvisioningMode.Disconnected, Uplink = null, AccessPointAddress = null });
    }

    private void Finish(string ssid, ConnectOutcome outcome) =>
        Update(s => s with { LastAttempt = new ConnectAttempt(ssid, outcome, DateTimeOffset.UtcNow) });

    private void Update(Func<ProvisioningState, ProvisioningState> change)
    {
        lock (_stateLock) _state = change(_state);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NubArca.PrintAgent.Api;

namespace NubArca.PrintAgent.Update;

/// <summary>
/// The agent's half of a Print Box update. The agent never installs anything:
/// it runs unprivileged and only rings the bell. When the server offers an
/// update and the agent is idle, it writes a request file into its own state
/// directory and stops claiming jobs; the box's root update service
/// (linux/update-print-box.sh, started by a systemd path unit) then asks the
/// server itself, with the station credential and the agent binary already
/// installed, what to fetch — so nothing the agent account writes decides what
/// root installs. The root service reports back through a result file, which
/// the agent relays to the server on its next heartbeat.
/// </summary>
public sealed partial class AgentSelfUpdate
{
    public const string RequestFile = "update-request.json";
    public const string ResultFile = "update-result";
    public const string HeartbeatFile = "heartbeat-ok";

    /// <summary>
    /// How long a request may wait for the root service to pick it up. Past it
    /// the service is not there to run (its path unit missing or broken): the
    /// agent takes its request back, reports it failed and works again.
    /// </summary>
    public static readonly TimeSpan PickupLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// An in-progress marker older than this outlived its service, which
    /// systemd stops well before it (TimeoutStartSec in
    /// linux/nubarca-print-agent-update.service): it is ignored.
    /// </summary>
    public static readonly TimeSpan InProgressLimit = TimeSpan.FromMinutes(25);

    [GeneratedRegex(@"^(ok|failed) \d{1,4}\.\d{1,4}\.\d{1,4} \d{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultPattern();
    [GeneratedRegex(@"^\d{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    private readonly string _stateDirectory;
    private readonly string _marker;
    private readonly string _inProgress;
    private readonly ILogger _logger;
    private string? _requestedId;
    private string? _requestedVersion;
    private string? _localResult;

    public AgentSelfUpdate(string stateDirectory, string marker, string inProgress, ILogger logger)
    {
        _stateDirectory = stateDirectory;
        _marker = marker;
        _inProgress = inProgress;
        _logger = logger;
    }

    public static AgentSelfUpdate For(PrintAgentOptions options, ILogger logger) =>
        new(Path.GetDirectoryName(Path.GetFullPath(options.CredentialPath))!, options.SelfUpdateMarkerPath,
            options.SelfUpdateInProgressPath, logger);

    /// <summary>A Linux box whose root update service is installed (it leaves the marker).</summary>
    public bool Supported => OperatingSystem.IsLinux() && File.Exists(_marker) && Directory.Exists(_stateDirectory);

    /// <summary>
    /// Claims wait while an update may restart this agent: from the moment it
    /// asks until the root service picks the request up, and then for exactly
    /// as long as that service runs (its in-progress marker) — not for a timer
    /// of the agent's own that the service could outlive.
    /// </summary>
    public bool Pending(DateTimeOffset now)
    {
        try
        {
            var marker = new FileInfo(_inProgress);
            if (marker.Exists && now - marker.LastWriteTimeUtc < InProgressLimit) return true;
            var request = new FileInfo(Path.Combine(_stateDirectory, RequestFile));
            if (!request.Exists) return false;
            if (now - request.LastWriteTimeUtc < PickupLimit) return true;
            // Nobody picked it up: take it back, say so, and work again.
            request.Delete();
            if (_requestedVersion is not null && _requestedId is not null)
                _localResult = $"failed {_requestedVersion} {_requestedId}";
            _logger.LogWarning("print.agent.update.not_picked_up: the box's update service did not start");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The root service's last outcome, exactly as it may be relayed, or null.</summary>
    public string? ReadResult()
    {
        try
        {
            var path = Path.Combine(_stateDirectory, ResultFile);
            if (!File.Exists(path) || new FileInfo(path).Length > 128) return _localResult;
            var value = File.ReadAllText(path).Trim();
            return ResultPattern().IsMatch(value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The three-part version the server, the bundles and the box's update service name.</summary>
    public static string ShortVersion(string version) =>
        System.Version.TryParse(version, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}" : version;

    /// <summary>The root service's health check: this version reached the server.</summary>
    public void MarkHeartbeat(string version)
    {
        if (!Supported) return;
        try
        {
            Write(HeartbeatFile, ShortVersion(version) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("print.agent.update.heartbeat_mark_failed ({ExceptionType})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Acts on the heartbeat's offer: asks the root service once per request,
    /// and only when idle — which is whenever the worker asks, between jobs.
    /// </summary>
    public void Handle(AgentUpdateOffer? offer, DateTimeOffset now)
    {
        // Withdrawn, done, failed or superseded: nothing to ring for.
        if (offer is null) return;
        if (!Supported || !RequestIdPattern().IsMatch(offer.RequestId)) return;
        // One ring per request; a request that failed is not rung again —
        // the box reports, and someone asks again.
        if (offer.RequestId == _requestedId) return;
        try
        {
            // The outcome of an earlier attempt is no longer news.
            File.Delete(Path.Combine(_stateDirectory, ResultFile));
            _localResult = null;
            Write(RequestFile, JsonSerializer.Serialize(new { version = offer.Version, requestId = offer.RequestId }));
            _requestedId = offer.RequestId;
            _requestedVersion = offer.Version;
            _logger.LogInformation("print.agent.update.requested version={Version}", offer.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("print.agent.update.request_failed ({ExceptionType})", ex.GetType().Name);
        }
    }

    private void Write(string name, string content)
    {
        var target = Path.Combine(_stateDirectory, name);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>
    /// Run as root by the box's update service: asks the server what to
    /// install and downloads exactly that, checked against the size and hash
    /// the server names. Writes <c>offer.json</c> and the zip into
    /// <paramref name="output"/>. Returns 0 when staged, 3 when nothing is offered.
    /// </summary>
    public static async Task<int> FetchAsync(PrintAgentApiClient api, string output, CancellationToken cancellationToken)
    {
        var offer = await api.GetUpdateOfferAsync(cancellationToken);
        if (offer is null) return 3;
        if (!Regex.IsMatch(offer.Version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")
            || offer.FileName != $"nubarca-print-agent-{offer.Version}-linux-x64.zip"
            || !Regex.IsMatch(offer.Sha256, "^[0-9a-f]{64}$")
            || offer.SizeBytes is <= 0 or > MaxBundleBytes
            || !RequestIdPattern().IsMatch(offer.RequestId))
            throw new InvalidDataException("The server's update offer is malformed.");
        Directory.CreateDirectory(output);
        var target = Path.Combine(output, offer.FileName);
        var partial = target + ".part";
        if (File.Exists(partial)) File.Delete(partial);
        await api.DownloadUpdateAsync(partial, offer.SizeBytes, cancellationToken);
        await using (var bytes = File.OpenRead(partial))
        {
            if (bytes.Length != offer.SizeBytes
                || Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, cancellationToken)) != offer.Sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException("The downloaded bundle is not the one the server named.");
            }
        }
        File.Move(partial, target, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(output, "offer.json"), JsonSerializer.Serialize(new
        {
            version = offer.Version, fileName = offer.FileName, sha256 = offer.Sha256, requestId = offer.RequestId,
        }), cancellationToken);
        return 0;
    }

    public const long MaxBundleBytes = 512L * 1024 * 1024;
}

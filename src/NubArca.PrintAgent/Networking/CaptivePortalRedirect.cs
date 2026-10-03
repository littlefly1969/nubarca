using System.Globalization;
using System.Text.RegularExpressions;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Networking;

/// <summary>
/// Plain HTTP on the setup network, sent to the setup page: a phone on the
/// setup network types <c>http://&lt;gateway&gt;/</c> — or its captive-portal
/// probe arrives on port 80 — and reaches the page, which itself stays on its
/// own port. Only while the setup network is up; never a lasting rule.
/// </summary>
public interface ICaptivePortalRedirect
{
    /// <summary>
    /// Sends tcp/80 arriving on <paramref name="wifiInterface"/> to local port
    /// <paramref name="targetPort"/>. False when it could not; the page is then
    /// still reachable on its own port.
    /// </summary>
    Task<bool> EnableAsync(string wifiInterface, int targetPort, CancellationToken cancellationToken);

    /// <summary>Removes the redirect. Idempotent and best-effort: never throws for a redirect that is not there.</summary>
    Task DisableAsync(CancellationToken cancellationToken);
}

/// <summary>No redirect: the page stays on its own port only.</summary>
public sealed class NoCaptivePortalRedirect : ICaptivePortalRedirect
{
    public static readonly NoCaptivePortalRedirect Instance = new();
    public Task<bool> EnableAsync(string wifiInterface, int targetPort, CancellationToken cancellationToken) =>
        Task.FromResult(false);
    public Task DisableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The redirect in nftables: one table of this box's own, <c>ip nubarca_setup</c>,
/// created and deleted whole — nothing another tool owns is ever touched — in a
/// single nft transaction each way, so it is either fully there or not at all.
///
/// nft needs CAP_NET_ADMIN, which the installer grants to the Print Box's own
/// service instance only (a systemd drop-in); without it the attempt fails, is
/// logged, and the page stays reachable on its own port.
/// </summary>
public sealed partial class NftCaptivePortalRedirect : ICaptivePortalRedirect
{
    public const string TableName = "nubarca_setup";

    private const string Nft = "nft";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly ILogger<NftCaptivePortalRedirect> _logger;

    public NftCaptivePortalRedirect(IProcessRunner runner, ILogger<NftCaptivePortalRedirect> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>A kernel interface name: what nmcli reports, and nothing nft could read as syntax.</summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,15}$")]
    private static partial Regex InterfaceName();

    /// <summary>The whole ruleset for one interface and port, as one nft transaction.</summary>
    public static string EnableCommand(string wifiInterface, int targetPort) => string.Join("; ",
        // Replace, never stack: a table left by a crash is dropped in the same
        // transaction that writes the new one.
        $"add table ip {TableName}",
        $"delete table ip {TableName}",
        $"add table ip {TableName}",
        $"add chain ip {TableName} prerouting {{ type nat hook prerouting priority -100 ; policy accept ; }}",
        $"add rule ip {TableName} prerouting iifname \"{wifiInterface}\" tcp dport 80 redirect to :"
            + targetPort.ToString(CultureInfo.InvariantCulture));

    /// <summary>Removes the table if it is there, as one transaction that cannot fail for its absence.</summary>
    public static string DisableCommand() => $"add table ip {TableName}; delete table ip {TableName}";

    public async Task<bool> EnableAsync(string wifiInterface, int targetPort, CancellationToken cancellationToken)
    {
        if (!InterfaceName().IsMatch(wifiInterface) || targetPort is < 1 or > 65535)
        {
            _logger.LogError("Setup page redirect refused: unexpected interface or port.");
            return false;
        }
        var result = await _runner.RunAsync(Nft, [EnableCommand(wifiInterface, targetPort)], Timeout, cancellationToken);
        if (result.Succeeded)
        {
            _logger.LogInformation("Setup page redirect on: HTTP port 80 on {Interface} → {Port}.", wifiInterface, targetPort);
            return true;
        }
        _logger.LogError("Setup page redirect could not be installed ({Reason}); the page stays on port {Port}.",
            FirstLine(result.StdErr), targetPort);
        return false;
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(Nft, [DisableCommand()], Timeout, cancellationToken);
        if (result.Succeeded) _logger.LogDebug("Setup page redirect off.");
        else _logger.LogWarning("Setup page redirect could not be removed ({Reason}).", FirstLine(result.StdErr));
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "no detail" : line.Length > 200 ? line[..200] : line;
    }
}

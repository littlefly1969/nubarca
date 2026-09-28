using NubArca.PrintAgent.Adapters;

namespace NubArca.PrintAgent;

/// <summary>
/// Says at start-up, in plain words, which Linux tool this configuration needs
/// and cannot find. It never stops the agent: without CUPS the printer is
/// reported offline, without NetworkManager the box simply cannot open its
/// setup network — both are states, not reasons to crash-loop.
/// </summary>
public sealed class LinuxRuntimeCheck : IHostedService
{
    private readonly PrintAgentOptions _options;
    private readonly ILogger<LinuxRuntimeCheck> _logger;

    public LinuxRuntimeCheck(PrintAgentOptions options, ILogger<LinuxRuntimeCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Adapter == PrintAdapterKinds.Cups)
        {
            foreach (var tool in Missing("lp", "lpstat"))
                _logger.LogError("CUPS tool {Tool} not found: install the cups package. Printers are reported offline until it is.", tool);
        }
        if (_options.NetworkProvisioning.Enabled)
        {
            foreach (var tool in Missing("nmcli"))
                _logger.LogCritical("{Tool} not found: network provisioning is unavailable. Install network-manager.", tool);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static IReadOnlyList<string> Missing(params string[] tools) =>
        tools.Where(tool => !OnPath(tool)).ToList();

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));
}

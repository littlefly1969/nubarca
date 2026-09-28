using System.Net.Http;
using NubArca.PrintAgent;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Journal;
using NubArca.PrintAgent.Security;

// Run by the Print Box installer, as root, before this box has a configuration.
if (args.FirstOrDefault()?.Equals("setup-cups", StringComparison.OrdinalIgnoreCase) == true)
{
    return await SetupCupsAsync(args.Skip(1).ToArray());
}

var builder = Host.CreateApplicationBuilder(args);
PrintAgentConfiguration.AddInstanceFile(builder.Configuration, args);
var options = builder.Configuration.GetSection(PrintAgentOptions.SectionName).Get<PrintAgentOptions>()
    ?? new PrintAgentOptions();
options.NormalizeAndValidate();

if (args.FirstOrDefault()?.Equals("enroll", StringComparison.OrdinalIgnoreCase) == true)
{
    return await EnrollAsync(args.Skip(1).ToArray(), options);
}

if (!Uri.TryCreate(options.ServerOrigin, UriKind.Absolute, out var serverOrigin))
    throw new InvalidOperationException("PrintAgent:ServerOrigin must be an absolute URL.");

if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(service => service.ServiceName = "NubArca Print Agent");
else if (OperatingSystem.IsLinux())
    builder.Services.AddSystemd();
else
    throw new PlatformNotSupportedException("NubArca Print Agent supports Windows and Linux only.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<AgentConnectionState>();
builder.Services.AddSingleton<ICredentialStore>(_ => PrintAgentPlatform.CreateCredentialStore(options.CredentialPath));
builder.Services.AddSingleton(_ => new ExecutionJournal(options.JournalPath));
builder.Services.AddSingleton<IPrinterAdapter>(sp => PrintAgentPlatform.CreatePrinterAdapter(options,
    sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<ILoggerFactory>()));
if (OperatingSystem.IsLinux())
{
    builder.Services.AddHostedService<LinuxRuntimeCheck>();
    if (options.NetworkProvisioning.Enabled) builder.Services.AddPrintBoxSetup(options.NetworkProvisioning);
}
builder.Services.AddHttpClient<PrintAgentApiClient>(nameof(PrintAgentApiClient), client =>
{
    client.BaseAddress = new Uri(serverOrigin.ToString().TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Never REUSE a connection that has been sitting idle across a poll gap.
    //
    // This agent talks in short bursts a few seconds apart, through whatever
    // reverse proxy fronts the installation. A proxy closes an idle keep-alive
    // connection on its own schedule, and the pooled socket is dead before the
    // next burst picks it up — the request then fails in under two milliseconds
    // with "the response ended prematurely", far too fast to be a network trip.
    // On one installation that was ~370 failed cycles an hour, every hour, and
    // a job left claimed each time the failure landed mid-cycle.
    //
    // Two seconds is below any poll interval this agent uses, so a connection is
    // either still warm from the burst it belongs to or freshly opened. The cost
    // is one handshake per burst; the alternative is depending on a proxy's
    // timeout being longer than ours, which is not ours to guarantee.
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
});
builder.Services.AddSharedPrintAgentApiClient();
builder.Services.AddSingleton<AgentExecutionCoordinator>();
builder.Services.AddHostedService<PrintAgentWorker>();
await builder.Build().RunAsync();
return 0;

static async Task<int> SetupCupsAsync(string[] args)
{
    string Value(string name, string fallback)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    if (!OperatingSystem.IsLinux())
    {
        Console.Error.WriteLine("setup-cups requires Linux.");
        return 2;
    }
    var photo = Value("--printer", "NubArca-RX1HS");
    var strip = Value("--strip-printer", "NubArca-RX1HS-STRIP");
    var name = new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_.-]{1,127}$");
    if (!name.IsMatch(photo) || !name.IsMatch(strip) || string.Equals(photo, strip, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: NubArca.PrintAgent setup-cups [--printer <queue>] [--strip-printer <queue>]");
        return 2;
    }
    var result = await new CupsQueueSetup(new ProcessRunner()).RunAsync(photo, strip, CancellationToken.None);
    if (result.DeviceUri is not null) Console.WriteLine($"Printer: {result.DeviceUri}");
    if (result.Driver is not null) Console.WriteLine($"Driver:  {result.Driver}");
    Console.WriteLine(result.Message);
    // 0 both queues, 10 photos only (no cut size), 3 no printer, 4 anything else.
    return result.StripQueue ? 0 : result.PhotoQueue ? 10 : result.PrinterFound ? 4 : 3;
}

static async Task<int> EnrollAsync(string[] args, PrintAgentOptions options)
{
    string? Value(string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    var server = Value("--server") ?? options.ServerOrigin;
    var stationText = Value("--station");
    var token = Value("--token");
    if (args.Any(x => string.Equals(x, "--token-stdin", StringComparison.OrdinalIgnoreCase)))
        token = (await Console.In.ReadToEndAsync()).Trim();
    if (!Uri.TryCreate(server, UriKind.Absolute, out var origin)
        || !Guid.TryParse(stationText, out var stationId) || string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("Usage: NubArca.PrintAgent enroll --server https://host --station <guid> --token <one-shot-token>");
        return 2;
    }
    using var http = new HttpClient { BaseAddress = new Uri(origin.ToString().TrimEnd('/') + "/") };
    var api = new PrintAgentApiClient(http);
    var version = typeof(PrintAgentWorker).Assembly.GetName().Version?.ToString() ?? "unknown";
    var response = await api.EnrollAsync(stationId, token, version, CancellationToken.None);
    await PrintAgentPlatform.CreateCredentialStore(options.CredentialPath)
        .SaveAsync(response.StationCredential, CancellationToken.None);
    Console.WriteLine($"Print Station {response.StationId:D} enrolled. Credential stored in the platform credential store.");
    return 0;
}

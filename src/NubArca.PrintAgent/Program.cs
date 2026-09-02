using NubArca.PrintAgent;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Journal;
using NubArca.PrintAgent.Security;

var builder = Host.CreateApplicationBuilder(args);
var options = builder.Configuration.GetSection(PrintAgentOptions.SectionName).Get<PrintAgentOptions>()
    ?? new PrintAgentOptions();
options.NormalizeAndValidate();

if (args.FirstOrDefault()?.Equals("enroll", StringComparison.OrdinalIgnoreCase) == true)
{
    return await EnrollAsync(args.Skip(1).ToArray(), options);
}

if (!Uri.TryCreate(options.ServerOrigin, UriKind.Absolute, out var serverOrigin))
    throw new InvalidOperationException("PrintAgent:ServerOrigin must be an absolute URL.");

builder.Services.AddWindowsService(service => service.ServiceName = "NubArca Print Agent");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ICredentialStore>(_ => new DpapiCredentialStore(options.CredentialPath));
builder.Services.AddSingleton(_ => new ExecutionJournal(options.JournalPath));
builder.Services.AddSingleton<IPrinterAdapter>(_ =>
    string.Equals(options.Adapter, "fake", StringComparison.OrdinalIgnoreCase)
        ? new FakePrinterAdapter(options.FakeOutputPath)
        : OperatingSystem.IsWindows()
            ? new WindowsSpoolerPrinterAdapter(options.PrinterName)
            : throw new PlatformNotSupportedException("windows-spooler requires a Windows version supported by .NET 10."));
builder.Services.AddHttpClient<PrintAgentApiClient>(client =>
{
    client.BaseAddress = new Uri(serverOrigin.ToString().TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<AgentExecutionCoordinator>();
builder.Services.AddHostedService<PrintAgentWorker>();
await builder.Build().RunAsync();
return 0;

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
    await new DpapiCredentialStore(options.CredentialPath)
        .SaveAsync(response.StationCredential, CancellationToken.None);
    Console.WriteLine($"Print Station {response.StationId:D} enrolled. Credential stored with machine DPAPI.");
    return 0;
}

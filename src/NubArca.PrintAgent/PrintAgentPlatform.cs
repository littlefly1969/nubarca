using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Security;

namespace NubArca.PrintAgent;

public static class PrintAgentPlatform
{
    public static ICredentialStore CreateCredentialStore(string path) =>
        OperatingSystem.IsWindows() ? new DpapiCredentialStore(path)
        : OperatingSystem.IsLinux() ? new LinuxFileCredentialStore(path)
        : throw new PlatformNotSupportedException("NubArca Print Agent supports Windows and Linux only.");

    public static IPrinterAdapter CreatePrinterAdapter(PrintAgentOptions options,
        IProcessRunner? runner = null, ILoggerFactory? loggers = null) => options.Adapter switch
    {
        PrintAdapterKinds.Fake => new FakePrinterAdapter(
            options.FakeOutputPath, TimeSpan.FromSeconds(options.FakeSheetSeconds)),
        PrintAdapterKinds.WindowsSpooler when OperatingSystem.IsWindows() =>
            new WindowsSpoolerPrinterAdapter(options.PrinterName, options.StripPrinterName),
        PrintAdapterKinds.WindowsSpooler => throw new PlatformNotSupportedException("windows-spooler requires Windows."),
        PrintAdapterKinds.Cups when OperatingSystem.IsLinux() => new CupsPrinterAdapter(
            options.PrinterName, options.StripPrinterName, runner ?? new ProcessRunner(),
            (loggers ?? NullLoggerFactory.Instance).CreateLogger<CupsPrinterAdapter>()),
        PrintAdapterKinds.Cups => throw new PlatformNotSupportedException("cups requires Linux."),
        _ => throw new InvalidOperationException("Print Agent adapter is invalid."),
    };
}

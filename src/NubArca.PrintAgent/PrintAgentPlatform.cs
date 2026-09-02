using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Security;

namespace NubArca.PrintAgent;

public static class PrintAgentPlatform
{
    public static ICredentialStore CreateCredentialStore(string path) =>
        OperatingSystem.IsWindows() ? new DpapiCredentialStore(path)
        : OperatingSystem.IsLinux() ? new LinuxFileCredentialStore(path)
        : throw new PlatformNotSupportedException("NubArca Print Agent supports Windows and Linux only.");

    public static IPrinterAdapter CreatePrinterAdapter(PrintAgentOptions options) => options.Adapter switch
    {
        PrintAdapterKinds.Fake => new FakePrinterAdapter(options.FakeOutputPath),
        PrintAdapterKinds.WindowsSpooler when OperatingSystem.IsWindows() => new WindowsSpoolerPrinterAdapter(options.PrinterName),
        PrintAdapterKinds.WindowsSpooler => throw new PlatformNotSupportedException("windows-spooler requires Windows."),
        PrintAdapterKinds.Cups => throw new NotSupportedException("cups is a reserved adapter contract and is not implemented yet. Use fake on Linux."),
        _ => throw new InvalidOperationException("Print Agent adapter is invalid."),
    };
}

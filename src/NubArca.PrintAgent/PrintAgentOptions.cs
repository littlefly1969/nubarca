using NubArca.PrintAgent.Networking;

namespace NubArca.PrintAgent;

public sealed class PrintAgentOptions
{
    public const string SectionName = "PrintAgent";
    public string ServerOrigin { get; set; } = string.Empty;
    public string CredentialPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\credential.bin";
    public string JournalPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\journal.db";
    public string TemporaryPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\temp";
    public string Adapter { get; set; } = "windows-spooler";

    /// <summary>The printer's main queue — a Windows print queue or a CUPS queue.</summary>
    public string? PrinterName { get; set; }

    /// <summary>
    /// Optional second queue on the SAME physical printer as
    /// <see cref="PrinterName"/>, set up with the driver's 2-inch cut: on
    /// Windows the DNP "2inch cut" in the queue's Printing Defaults, on Linux
    /// the Gutenprint queue created with the cut. Only then does the printer
    /// report <c>2x6x2</c>, and a party strip arrives as two separate 2x6
    /// strips instead of one sheet to cut by hand.
    /// </summary>
    public string? StripPrinterName { get; set; }

    /// <summary>The Linux Print Box's own Wi-Fi setup (NetworkManager). Off by default.</summary>
    public NetworkProvisioningOptions NetworkProvisioning { get; set; } = new();
    public string FakeOutputPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\fake-output";
    public int IdlePollSeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 60;

    /// <summary>
    /// Seconds the FAKE printer spends producing one sheet. Zero prints
    /// instantly, which is what an automated test wants and what a simulator
    /// standing in for a real printer is not.
    /// </summary>
    public int FakeSheetSeconds { get; set; } = 10;

    /// <summary>
    /// The media count the FAKE printer reports, decremented per sheet it
    /// produces. Null reports none. Deterministic, for tests and simulators.
    /// </summary>
    public int? FakeRemainingPrints { get; set; }
    public long MaxArtifactBytes { get; set; } = 32 * 1024 * 1024;
    public long MaxTemporaryBytes { get; set; } = 128 * 1024 * 1024;

    public void NormalizeAndValidate() => NormalizeAndValidate(OperatingSystem.IsLinux());

    public void NormalizeAndValidate(bool isLinux)
    {
        CredentialPath = Environment.ExpandEnvironmentVariables(CredentialPath);
        JournalPath = Environment.ExpandEnvironmentVariables(JournalPath);
        TemporaryPath = Environment.ExpandEnvironmentVariables(TemporaryPath);
        FakeOutputPath = Environment.ExpandEnvironmentVariables(FakeOutputPath);
        Adapter = Adapter.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(StripPrinterName)) StripPrinterName = null;
        // The cutting queue belongs to ONE named printer. Without that name the
        // agent would report every installed queue, the cutting one included,
        // as a printer of its own.
        if (StripPrinterName is not null && string.IsNullOrWhiteSpace(PrinterName))
            throw new InvalidOperationException(
                "PrintAgent:StripPrinterName requires PrintAgent:PrinterName.");
        if (StripPrinterName is not null
            && string.Equals(StripPrinterName, PrinterName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "PrintAgent:StripPrinterName must be a second queue, not PrintAgent:PrinterName itself.");
        if (Adapter is not ("fake" or "windows-spooler" or "cups"))
            throw new InvalidOperationException("PrintAgent:Adapter must be fake, windows-spooler, or cups.");
        if (FakeSheetSeconds < 0) throw new InvalidOperationException(
            "Print Agent bounds are invalid.");
        if (FakeRemainingPrints is < 0 or > Adapters.CupsMarkers.MaxPlausiblePrints)
            throw new InvalidOperationException("PrintAgent:FakeRemainingPrints is out of range.");
        if (IdlePollSeconds < 1 || MaxBackoffSeconds < 2 || MaxArtifactBytes < 1
            || MaxTemporaryBytes < MaxArtifactBytes)
            throw new InvalidOperationException("Print Agent bounds are invalid.");
        NetworkProvisioning ??= new NetworkProvisioningOptions();
        NetworkProvisioning.Validate(isLinux);
    }
}

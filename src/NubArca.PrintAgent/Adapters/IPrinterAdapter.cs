namespace NubArca.PrintAgent.Adapters;

public static class PrintAdapterKinds
{
    public const string Fake = "fake";
    public const string WindowsSpooler = "windows-spooler";
    // Linux: queues of the local CUPS server, through its command-line tools.
    public const string Cups = "cups";
}

public sealed record PrinterCapabilities(IReadOnlyList<string> Formats, bool Color, int MaxCopies = 1);
public sealed record DiscoveredPrinter(
    string DeviceKey, string DisplayName, string? Manufacturer, string? Model, string AdapterKind);
public sealed record PrinterObservedStatus(string State, string? Detail = null);
public sealed record PrintSubmission(
    Guid JobId, string DeviceKey, string ArtifactPath, string ContentType, string Format);
public sealed record PrintSubmissionResult(bool Accepted, string? SpoolReference, string? FailureCode);

/// <summary>
/// How many prints the media physically loaded in the printer still holds, as
/// the PRINTER says — the DNP through Gutenprint and CUPS, never a count kept
/// here. <see cref="RemainingPrints"/> is null when no reliable number is
/// reported (a driver that gives only a percentage, a printer not yet asked by
/// its driver, a print system that cannot be reached); 0 is a printer that
/// says its media is spent. <see cref="ObservationAgeSeconds"/> is how long ago
/// the printer last changed that reading, when the source says.
///
/// Telemetry, never a lock: the physical outcome of a job belongs to the
/// printer, and a count that has not caught up with a sheet in flight is
/// still the truth as last reported.
/// </summary>
public sealed record PrinterMediaStatus(int? RemainingPrints, int? ObservationAgeSeconds = null)
{
    public static readonly PrinterMediaStatus Unavailable = new(RemainingPrints: null);
}

public interface IPrinterAdapter
{
    string Kind { get; }
    Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken cancellationToken);
    Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter printer, CancellationToken cancellationToken);
    Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter printer, CancellationToken cancellationToken);
    Task<PrintSubmissionResult> SubmitAsync(PrintSubmission submission, CancellationToken cancellationToken);

    /// <summary>
    /// The prints left on the media of the PHYSICAL printer behind
    /// <paramref name="printer"/>. Independent of its state: a ready printer
    /// whose driver reports no count is a valid answer, not an offline one.
    /// </summary>
    Task<PrinterMediaStatus> GetMediaStatusAsync(DiscoveredPrinter printer, CancellationToken cancellationToken);
}

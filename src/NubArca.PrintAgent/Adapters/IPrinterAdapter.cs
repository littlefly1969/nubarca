namespace NubArca.PrintAgent.Adapters;

public sealed record PrinterCapabilities(IReadOnlyList<string> Formats, bool Color, int MaxCopies = 1);
public sealed record DiscoveredPrinter(
    string DeviceKey, string DisplayName, string? Manufacturer, string? Model, string AdapterKind);
public sealed record PrinterObservedStatus(string State, string? Detail = null);
public sealed record PrintSubmission(
    Guid JobId, string DeviceKey, string ArtifactPath, string ContentType, string Format);
public sealed record PrintSubmissionResult(bool Accepted, string? SpoolReference, string? FailureCode);

public interface IPrinterAdapter
{
    string Kind { get; }
    Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken cancellationToken);
    Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter printer, CancellationToken cancellationToken);
    Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter printer, CancellationToken cancellationToken);
    Task<PrintSubmissionResult> SubmitAsync(PrintSubmission submission, CancellationToken cancellationToken);
}

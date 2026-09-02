using System.Collections;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace NubArca.PrintAgent.Adapters;

[SupportedOSPlatform("windows")]
public sealed class WindowsSpoolerPrinterAdapter : IPrinterAdapter
{
    private readonly string? _configuredPrinter;
    public WindowsSpoolerPrinterAdapter(string? configuredPrinter) => _configuredPrinter = configuredPrinter;
    public string Kind => "windows-spooler";

    public Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<DiscoveredPrinter>>([]);
        var printers = new List<DiscoveredPrinter>();
        foreach (var value in (IEnumerable)PrinterSettings.InstalledPrinters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = value?.ToString();
            if (string.IsNullOrWhiteSpace(name)
                || (!string.IsNullOrWhiteSpace(_configuredPrinter)
                    && !string.Equals(name, _configuredPrinter, StringComparison.OrdinalIgnoreCase))) continue;
            printers.Add(new(name, name, Manufacturer(name), name, Kind));
        }
        return Task.FromResult<IReadOnlyList<DiscoveredPrinter>>(printers);
    }

    public Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var settings = new PrinterSettings { PrinterName = printer.DeviceKey };
        if (!settings.IsValid) return Task.FromResult(new PrinterCapabilities([], false));
        var formats = settings.PaperSizes.Cast<PaperSize>()
            .Any(x => IsPhoto10x15(x.Width, x.Height)) ? new[] { "10x15" } : Array.Empty<string>();
        return Task.FromResult(new PrinterCapabilities(formats, settings.SupportsColor));
    }

    public Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var settings = new PrinterSettings { PrinterName = printer.DeviceKey };
        return Task.FromResult(new PrinterObservedStatus(settings.IsValid ? "ready" : "offline"));
    }

    public Task<PrintSubmissionResult> SubmitAsync(PrintSubmission submission,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Image.FromFile(submission.ArtifactPath);
        using var document = new PrintDocument
        {
            DocumentName = $"NubArca-{submission.JobId.ToString("N")[..8]}",
            PrintController = new StandardPrintController(),
        };
        document.PrinterSettings.PrinterName = submission.DeviceKey;
        if (!document.PrinterSettings.IsValid)
            return Task.FromResult(new PrintSubmissionResult(false, null, "printer_unavailable"));
        var paper = document.PrinterSettings.PaperSizes.Cast<PaperSize>()
            .FirstOrDefault(x => IsPhoto10x15(x.Width, x.Height));
        if (paper is null)
            return Task.FromResult(new PrintSubmissionResult(false, null, "format_unsupported"));
        document.DefaultPageSettings.PaperSize = paper;
        document.DefaultPageSettings.Landscape = image.Width > image.Height;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.PrintPage += (_, args) =>
        {
            var bounds = args.PageBounds;
            var scale = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            args.Graphics!.DrawImage(image,
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
            args.HasMorePages = false;
        };
        try
        {
            document.Print();
            return Task.FromResult(new PrintSubmissionResult(true, document.DocumentName, null));
        }
        catch (InvalidPrinterException)
        {
            return Task.FromResult(new PrintSubmissionResult(false, null, "printer_unavailable"));
        }
    }

    private static bool IsPhoto10x15(int width, int height)
    {
        var shortEdge = Math.Min(width, height);
        var longEdge = Math.Max(width, height);
        return Math.Abs(shortEdge - 400) <= 20 && Math.Abs(longEdge - 600) <= 25;
    }

    private static string? Manufacturer(string name) =>
        name.Contains("DNP", StringComparison.OrdinalIgnoreCase) ? "DNP" : null;
}

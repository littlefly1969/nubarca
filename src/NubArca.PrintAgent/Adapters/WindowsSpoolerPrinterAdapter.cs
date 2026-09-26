using System.Collections;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace NubArca.PrintAgent.Adapters;

[SupportedOSPlatform("windows")]
public sealed class WindowsSpoolerPrinterAdapter : IPrinterAdapter
{
    private readonly string? _configuredPrinter;
    private readonly string? _stripPrinter;

    /// <param name="stripPrinter">
    /// Optional second queue on the configured printer, with the DNP "2inch cut"
    /// enabled in its Printing Defaults. Without it no 2x6x2 is advertised.
    /// </param>
    public WindowsSpoolerPrinterAdapter(string? configuredPrinter, string? stripPrinter = null)
    {
        _configuredPrinter = configuredPrinter;
        _stripPrinter = stripPrinter;
    }

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
        var formats = SpoolerQueueRouting.Formats(printer.DeviceKey, PaperFor(settings) is not null,
            _configuredPrinter, _stripPrinter, StripQueueReady());
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
        // The strip is the same 10x15 artifact; only the queue differs, and the
        // cut comes from that queue's driver defaults.
        var queue = SpoolerQueueRouting.TargetQueue(submission.Format, submission.DeviceKey,
            _configuredPrinter, _stripPrinter);
        if (queue is null)
            return Task.FromResult(new PrintSubmissionResult(false, null, "format_unsupported"));
        using var image = Image.FromFile(submission.ArtifactPath);
        using var document = new PrintDocument
        {
            DocumentName = $"NubArca-{submission.JobId.ToString("N")[..8]}",
            PrintController = new StandardPrintController(),
        };
        document.PrinterSettings.PrinterName = queue;
        if (!document.PrinterSettings.IsValid)
            return Task.FromResult(new PrintSubmissionResult(false, null, "printer_unavailable"));
        var paper = PaperFor(document.PrinterSettings);
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

    private bool StripQueueReady()
    {
        if (string.IsNullOrWhiteSpace(_stripPrinter)) return false;
        var settings = new PrinterSettings { PrinterName = _stripPrinter };
        return settings.IsValid && PaperFor(settings) is not null;
    }

    /// <summary>
    /// The queue's own default paper when it is 10x15, else its first 10x15
    /// entry. Whatever the operator set on a queue travels in its defaults, and
    /// choosing a different entry of the same size must not quietly undo it.
    /// </summary>
    private static PaperSize? PaperFor(PrinterSettings settings)
    {
        var preferred = settings.DefaultPageSettings.PaperSize;
        if (IsPhoto10x15(preferred.Width, preferred.Height)) return preferred;
        return settings.PaperSizes.Cast<PaperSize>()
            .FirstOrDefault(x => IsPhoto10x15(x.Width, x.Height));
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

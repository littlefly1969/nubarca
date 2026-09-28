using System.Text.RegularExpressions;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Adapters;

/// <summary>What <see cref="CupsQueueSetup"/> did, in words an installer can print.</summary>
public sealed record CupsSetupResult(
    bool PrinterFound,
    bool PhotoQueue,
    bool StripQueue,
    string? DeviceUri,
    string? Driver,
    string Message);

/// <summary>
/// Creates the Print Box's two CUPS queues on a DNP DS-RX1/RX1HS found on USB,
/// so nobody has to type an lpadmin command.
///
/// Both queues use the Gutenprint dye-sub backend and driver for the same
/// printer. The photo queue defaults to plain 4x6 (<c>w288h432</c>); the strip
/// queue to Gutenprint's "2x6*2" (<c>w288h432-div2</c>), the same 4x6 sheet cut
/// into two 2x6 strips by the printer. Only page sizes this driver actually
/// lists are chosen: if the cut variant is missing, the strip queue is not
/// created and the box prints strips as one sheet, as on any printer that
/// cannot cut.
///
/// Both queues retry a job when the printer is unplugged instead of stopping
/// (a stopped queue would wait for an operator the box does not have), and
/// neither is shared on the network.
/// </summary>
public sealed partial class CupsQueueSetup
{
    public const string PlainSize = "w288h432";
    public const string CutSize = "w288h432-div2";

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;

    public CupsQueueSetup(IProcessRunner runner) => _runner = runner;

    [GeneratedRegex(@"^gutenprint[0-9]*\+usb://\S*(dnp|rx1)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GutenprintDnpUri();

    [GeneratedRegex(@"^(usb|gutenprint[0-9]*\+usb)://\S*(dnp|dai%20nippon|rx1)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyDnpUri();

    [GeneratedRegex(@"^(gutenprint\S*://dnp-ds-?rx1\S*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RxDriver();

    public async Task<CupsSetupResult> RunAsync(string photoQueue, string stripQueue, CancellationToken ct)
    {
        var devices = await _runner.RunAsync("lpinfo", ["-v"], ScanTimeout, ct);
        if (!devices.Succeeded)
            return Fail(false, "CUPS could not list devices (is the cups service running?).");
        var uri = PickDevice(devices.StdOut);
        if (uri is null)
            return Fail(false, "No DNP DS-RX1/RX1HS on USB. Plug it in, switch it on, and run the installer again.");

        var drivers = await _runner.RunAsync("lpinfo", ["-m"], ScanTimeout, ct);
        var driver = drivers.Succeeded ? PickDriver(drivers.StdOut) : null;
        if (driver is null)
            return Fail(true, "The Gutenprint driver for the DS-RX1 is missing: install printer-driver-gutenprint.", uri);

        if (!await CreateAsync(photoQueue, uri, driver, "NubArca photos (4x6)", ct)
            || !await SetPageSizeAsync(photoQueue, preferCut: false, ct))
            return Fail(true, $"CUPS could not set up {photoQueue} with 4x6 paper.", uri, driver);

        var strip = await CreateAsync(stripQueue, uri, driver, "NubArca strips (2x6 x2, cut)", ct)
            && await SetPageSizeAsync(stripQueue, preferCut: true, ct);
        if (!strip)
        {
            // Better no strip queue than one that prints strips uncut while
            // NubArca tells guests they come out as two.
            await _runner.RunAsync("lpadmin", ["-x", stripQueue], CommandTimeout, ct);
            return new CupsSetupResult(true, true, false, uri, driver,
                $"{photoQueue} is ready. This driver offers no 2x6 cut size, so strips print as one sheet.");
        }
        return new CupsSetupResult(true, true, true, uri, driver,
            $"{photoQueue} (4x6) and {stripQueue} (2x6 x2, cut) are ready.");

        static CupsSetupResult Fail(bool found, string message, string? uri = null, string? driver = null) =>
            new(found, false, false, uri, driver, message);
    }

    /// <summary>The Gutenprint dye-sub URI when there is one; the plain usb one only as a last resort.</summary>
    public static string? PickDevice(string lpinfoV)
    {
        var uris = lpinfoV.Split('\n')
            .Select(line => line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1].Trim())
            .ToList();
        return uris.FirstOrDefault(u => GutenprintDnpUri().IsMatch(u))
            ?? uris.FirstOrDefault(u => AnyDnpUri().IsMatch(u));
    }

    /// <summary>The "expert" PPD, which lists every page size, before the simplified one.</summary>
    public static string? PickDriver(string lpinfoM)
    {
        var names = lpinfoM.Split('\n')
            .Select(line => RxDriver().Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
        return names.FirstOrDefault(n => n.EndsWith("/expert", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
    }

    /// <summary>The PageSize keywords an <c>lpoptions -l</c> listing offers, default mark removed.</summary>
    public static IReadOnlyList<string> PageSizes(string lpoptionsL)
    {
        var line = lpoptionsL.Split('\n')
            .FirstOrDefault(l => l.StartsWith("PageSize/", StringComparison.Ordinal)
                || l.StartsWith("PageSize:", StringComparison.Ordinal));
        if (line is null) return [];
        var colon = line.IndexOf(':');
        return line[(colon + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(choice => choice.TrimStart('*'))
            .ToList();
    }

    private async Task<bool> CreateAsync(string queue, string uri, string driver, string description,
        CancellationToken ct)
    {
        var result = await _runner.RunAsync("lpadmin",
            ["-p", queue, "-E", "-v", uri, "-m", driver, "-D", description,
             "-o", "printer-error-policy=retry-job", "-o", "printer-is-shared=false"],
            CommandTimeout, ct);
        return result.Succeeded;
    }

    private async Task<bool> SetPageSizeAsync(string queue, bool preferCut, CancellationToken ct)
    {
        var options = await _runner.RunAsync("lpoptions", ["-p", queue, "-l"], CommandTimeout, ct);
        if (!options.Succeeded) return false;
        var wanted = preferCut ? CutSize : PlainSize;
        if (!PageSizes(options.StdOut).Contains(wanted, StringComparer.Ordinal)) return false;
        var set = await _runner.RunAsync("lpadmin", ["-p", queue, "-o", $"PageSize={wanted}"], CommandTimeout, ct);
        return set.Succeeded;
    }
}

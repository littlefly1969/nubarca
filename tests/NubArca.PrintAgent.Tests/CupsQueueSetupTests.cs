using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Tests;

public sealed class CupsQueueSetupTests
{
    private const string Devices = """
        network ipp
        direct usb://Dai%20Nippon%20Printing/DS-RX1?serial=1234
        direct gutenprint53+usb://dnp-dsrx1/1234
        network socket
        """;

    private const string Drivers = """
        drv:///sample.drv/generic.ppd Generic PostScript Printer
        gutenprint.5.3://dnp-dsrx1/simple DNP DS-RX1 - CUPS+Gutenprint v5.3.4 Simplified
        gutenprint.5.3://dnp-dsrx1/expert DNP DS-RX1 - CUPS+Gutenprint v5.3.4
        gutenprint.5.3://dnp-ds40/expert DNP DS40 - CUPS+Gutenprint v5.3.4
        """;

    private const string Options = """
        PageSize/Media Size: B7 *w288h432 w288h432-div2 w360h504 w432h576 w432h576-div2 Custom.WIDTHxHEIGHT
        StpLaminate/Overcoat Pattern: *Glossy Matte
        """;

    private static FakeProcessRunner Cups(string devices = Devices, string drivers = Drivers, string options = Options) =>
        new((file, args) => (file, args.FirstOrDefault()) switch
        {
            ("lpinfo", "-v") => FakeProcessRunner.Ok(devices),
            ("lpinfo", "-m") => FakeProcessRunner.Ok(drivers),
            ("lpoptions", _) => FakeProcessRunner.Ok(options),
            ("lpadmin", _) => FakeProcessRunner.Ok(),
            _ => ProcessResult.Missing(file),
        });

    [Fact]
    public async Task Both_Queues_Are_Created_On_The_Gutenprint_Backend_With_The_Right_Sizes()
    {
        var runner = Cups();
        var result = await new CupsQueueSetup(runner).RunAsync("NubArca-RX1HS", "NubArca-RX1HS-STRIP", default);

        Assert.True(result.PrinterFound && result.PhotoQueue && result.StripQueue);
        Assert.Equal("gutenprint53+usb://dnp-dsrx1/1234", result.DeviceUri);
        Assert.Equal("gutenprint.5.3://dnp-dsrx1/expert", result.Driver);

        var admin = runner.Calls.Where(c => c.File == "lpadmin").Select(c => c.Args).ToList();
        Assert.Equal(["-p", "NubArca-RX1HS", "-E", "-v", "gutenprint53+usb://dnp-dsrx1/1234",
            "-m", "gutenprint.5.3://dnp-dsrx1/expert", "-D", "NubArca photos (4x6)",
            "-o", "printer-error-policy=retry-job", "-o", "printer-is-shared=false"], admin[0]);
        Assert.Equal(["-p", "NubArca-RX1HS", "-o", "PageSize=w288h432"], admin[1]);
        Assert.Equal("NubArca-RX1HS-STRIP", admin[2][1]);
        Assert.Equal(["-p", "NubArca-RX1HS-STRIP", "-o", "PageSize=w288h432-div2"], admin[3]);
    }

    [Fact]
    public async Task Without_The_Printer_Nothing_Is_Created()
    {
        var runner = Cups(devices: "network ipp\nnetwork socket\n");
        var result = await new CupsQueueSetup(runner).RunAsync("P", "S", default);
        Assert.False(result.PrinterFound);
        Assert.Contains("Plug it in", result.Message);
        Assert.DoesNotContain(runner.Calls, c => c.File == "lpadmin");
    }

    [Fact]
    public async Task Without_The_Driver_Nothing_Is_Created()
    {
        var runner = Cups(drivers: "drv:///sample.drv/generic.ppd Generic PostScript Printer\n");
        var result = await new CupsQueueSetup(runner).RunAsync("P", "S", default);
        Assert.True(result.PrinterFound);
        Assert.False(result.PhotoQueue);
        Assert.Contains("printer-driver-gutenprint", result.Message);
        Assert.DoesNotContain(runner.Calls, c => c.File == "lpadmin");
    }

    [Fact]
    public async Task No_Cut_Size_Means_No_Strip_Queue_Rather_Than_Uncut_Strips()
    {
        var runner = Cups(options: "PageSize/Media Size: *w288h432 w360h504\n");
        var result = await new CupsQueueSetup(runner).RunAsync("P", "S", default);
        Assert.True(result.PhotoQueue);
        Assert.False(result.StripQueue);
        Assert.Contains(runner.Calls, c => c.File == "lpadmin" && c.Args.SequenceEqual(["-x", "S"]));
    }

    [Fact]
    public void Device_Driver_And_Sizes_Are_Picked_Conservatively()
    {
        // The dye-sub backend beats plain usb; plain usb is only a last resort.
        Assert.Equal("usb://Dai%20Nippon%20Printing/DS-RX1?serial=1",
            CupsQueueSetup.PickDevice("direct usb://Dai%20Nippon%20Printing/DS-RX1?serial=1\n"));
        Assert.Null(CupsQueueSetup.PickDevice("direct usb://HP/LaserJet\n"));
        // Expert before simplified: only the expert PPD lists every page size.
        Assert.Equal("gutenprint.5.3://dnp-dsrx1/simple",
            CupsQueueSetup.PickDriver("gutenprint.5.3://dnp-dsrx1/simple DNP DS-RX1 Simplified\n"));
        Assert.Null(CupsQueueSetup.PickDriver("gutenprint.5.3://dnp-ds40/expert DNP DS40\n"));
        Assert.Equal(["B7", "w288h432", "w288h432-div2"],
            CupsQueueSetup.PageSizes("PageSize/Media Size: B7 *w288h432 w288h432-div2\n"));
        Assert.Empty(CupsQueueSetup.PageSizes("StpLaminate/Overcoat: *Glossy\n"));
    }

    [Fact]
    public void The_Installer_Sets_Everything_Up_Without_Manual_Steps()
    {
        var installer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "linux", "install-print-box.sh"));
        Assert.Contains("setup-cups --printer \"$printer\" --strip-printer \"$strip\"", installer, StringComparison.Ordinal);
        Assert.Contains("renderer: NetworkManager", installer, StringComparison.Ordinal);
        Assert.Contains("systemd-run --quiet --collect --on-active=10", installer, StringComparison.Ordinal);
        Assert.Contains("ubuntu) version_ok \"${VERSION_ID:-0}\" 24.04", installer, StringComparison.Ordinal);
        Assert.Contains("printer='NubArca-RX1HS'", installer, StringComparison.Ordinal);
        Assert.Contains("strip='NubArca-RX1HS-STRIP'", installer, StringComparison.Ordinal);
    }
}

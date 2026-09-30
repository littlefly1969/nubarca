using NubArca.PrintAgent;
using NubArca.PrintAgent.Adapters;

namespace NubArca.PrintAgent.Tests;

/// <summary>
/// The DNP strip cut, decided without a spooler: which printer may say it can
/// cut, and which queue a cut sheet is sent to.
/// </summary>
public sealed class SpoolerQueueRoutingTests
{
    private const string Printer = "DNP DS-RX1HS";
    private const string StripQueue = "DNP DS-RX1HS 2inch";

    [Fact]
    public void Only_The_Configured_Printer_With_A_Ready_Strip_Queue_Reports_2x6x2()
    {
        Assert.Equal(["10x15", "2x6x2"],
            SpoolerQueueRouting.Formats(Printer, true, Printer, StripQueue, true));

        // Case differs between what Windows lists and what an operator typed.
        Assert.Equal(["10x15", "2x6x2"],
            SpoolerQueueRouting.Formats(Printer.ToLowerInvariant(), true, Printer, StripQueue, true));

        // No strip queue configured, or it lost its 10x15 paper: photos only.
        Assert.Equal(["10x15"], SpoolerQueueRouting.Formats(Printer, true, Printer, null, false));
        Assert.Equal(["10x15"], SpoolerQueueRouting.Formats(Printer, true, Printer, StripQueue, false));

        // Another printer never borrows the DNP's cutter.
        Assert.Equal(["10x15"],
            SpoolerQueueRouting.Formats("Office Inkjet", true, Printer, StripQueue, true));
        Assert.Equal(["10x15"],
            SpoolerQueueRouting.Formats(Printer, true, null, StripQueue, true));

        // A printer without 10x15 paper prints nothing, cut or not.
        Assert.Empty(SpoolerQueueRouting.Formats(Printer, false, Printer, StripQueue, true));
    }

    [Fact]
    public void A_2x6x2_Sheet_Goes_To_The_Strip_Queue_And_A_Photo_Stays_On_The_Printer()
    {
        Assert.Equal(Printer, SpoolerQueueRouting.TargetQueue("10x15", Printer, Printer, StripQueue));
        Assert.Equal(StripQueue, SpoolerQueueRouting.TargetQueue("2x6x2", Printer, Printer, StripQueue));
        Assert.Equal(StripQueue, SpoolerQueueRouting.TargetQueue("2X6X2", Printer, Printer, StripQueue));
    }

    [Fact]
    public void A_Sheet_This_Printer_Cannot_Cut_Is_Refused_Not_Printed_Uncut()
    {
        // A 2x6x2 job sent to a printer without its cutting queue would come out
        // as one sheet the guest was told would be two strips.
        Assert.Null(SpoolerQueueRouting.TargetQueue("2x6x2", Printer, Printer, null));
        Assert.Null(SpoolerQueueRouting.TargetQueue("2x6x2", "Office Inkjet", Printer, StripQueue));
        Assert.Null(SpoolerQueueRouting.TargetQueue("A4", Printer, Printer, StripQueue));
    }

    [Fact]
    public void A_Strip_Queue_Requires_A_Named_Printer_Of_Its_Own()
    {
        var unnamed = Options(printer: null, strip: StripQueue);
        Assert.Contains("requires PrintAgent:PrinterName",
            Assert.Throws<InvalidOperationException>(unnamed.NormalizeAndValidate).Message);

        var same = Options(printer: Printer, strip: Printer.ToUpperInvariant());
        Assert.Contains("second queue",
            Assert.Throws<InvalidOperationException>(same.NormalizeAndValidate).Message);

        var blank = Options(printer: null, strip: "  ");
        blank.NormalizeAndValidate();
        Assert.Null(blank.StripPrinterName);

        var valid = Options(printer: Printer, strip: StripQueue);
        valid.NormalizeAndValidate();
        Assert.Equal(StripQueue, valid.StripPrinterName);
    }

    [Fact]
    public void The_Windows_Installer_Writes_The_Strip_Queue()
    {
        var installer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "install-service.ps1"));
        Assert.Contains("[string] $StripPrinterName = ''", installer, StringComparison.Ordinal);
        Assert.Contains("StripPrinterName = $configuredStripPrinter", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Page_Turns_Relative_To_The_Drivers_Own_Paper()
    {
        // Paper sizes in hundredths of an inch, artifacts in pixels.
        const int portraitSheetW = 1200, portraitSheetH = 1800;   // photo or strip pair
        const int landscapeSheetW = 1800, landscapeSheetH = 1200; // landscape photo, test page

        // A driver that defines 4x6 standing up (4 wide, 6 tall): unchanged.
        Assert.False(SheetOrientation.Landscape(portraitSheetW, portraitSheetH, 410, 610));
        Assert.True(SheetOrientation.Landscape(landscapeSheetW, landscapeSheetH, 410, 610));

        // The DS-RX1 defines it lying down (6 wide, 4 tall), the way the paper
        // runs: a portrait sheet must turn the page, a landscape one must not.
        Assert.True(SheetOrientation.Landscape(portraitSheetW, portraitSheetH, 615, 413));
        Assert.False(SheetOrientation.Landscape(landscapeSheetW, landscapeSheetH, 615, 413));
    }

    private static PrintAgentOptions Options(string? printer, string? strip) => new()
    {
        ServerOrigin = "https://example.invalid",
        Adapter = "windows-spooler",
        PrinterName = printer,
        StripPrinterName = strip,
    };

    [Fact]
    public void Each_Paper_The_Queue_Prints_Is_Reported_And_Goes_To_The_Printer()
    {
        var formats = SpoolerQueueRouting.Formats("DNP", ["20x15", "10x15", "13x18"], "DNP", "DNP-STRIP", true);
        // In the one order, and the cut only on top of 10x15.
        Assert.Equal(["10x15", "13x18", "20x15", "2x6x2"], formats);
        Assert.Equal(["20x15"], SpoolerQueueRouting.Formats("DNP", ["20x15"], "DNP", "DNP-STRIP", true));
        foreach (var paper in new[] { "10x15", "13x18", "20x15" })
            Assert.Equal("DNP", SpoolerQueueRouting.TargetQueue(paper, "DNP", "DNP", "DNP-STRIP"));
        Assert.Null(SpoolerQueueRouting.TargetQueue("a4", "DNP", "DNP", "DNP-STRIP"));
    }

    [Theory]
    [InlineData("10x15", 400, 600, true)]
    [InlineData("10x15", 600, 400, true)]
    [InlineData("13x18", 500, 700, true)]
    [InlineData("13x18", 700, 500, true)]
    [InlineData("20x15", 600, 800, true)]
    [InlineData("20x15", 800, 600, true)]
    [InlineData("10x15", 500, 700, false)]
    [InlineData("20x15", 400, 600, false)]
    [InlineData("13x18", 600, 800, false)]
    public void A_Spooler_Paper_Is_Matched_By_Its_Size_Either_Way_Up(string paper, int w, int h, bool expected) =>
        Assert.Equal(expected, SpoolerQueueRouting.IsPaper(paper, w, h));
}

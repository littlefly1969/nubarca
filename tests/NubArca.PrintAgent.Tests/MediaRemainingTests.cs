using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Tests;

/// <summary>
/// The prints physically left on the printer's media, as the printer reports
/// them through Gutenprint and CUPS — read, never guessed.
/// </summary>
public sealed class MediaRemainingTests
{
    private const string Photo = "NubArca-RX1HS";
    private const string Strip = "NubArca-RX1HS-STRIP";

    /// <summary>
    /// ipptool's XML report, shaped exactly as `ipptool -T 5 -X` printed it for
    /// the shipped query against an IPP printer carrying Gutenprint's markers.
    /// </summary>
    private static string Plist(string attributes, bool successful = true) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        <key>ipptoolVersion</key>
        <string>CUPS v2.4.19</string>
        <key>Tests</key>
        <array>
        <dict>
        <key>Name</key>
        <string>NubArca media status</string>
        <key>Operation</key>
        <string>Get-Printer-Attributes</string>
        <key>Successful</key>
        {(successful ? "<true />" : "<false />")}
        <key>StatusCode</key>
        <string>successful-ok</string>
        <key>ResponseAttributes</key>
        <array>
        <dict>
        <key>attributes-charset</key>
        <string>utf-8</string>
        </dict>
        <dict>
        {attributes}
        </dict>
        </array>
        </dict>
        </array>
        <key>Successful</key>
        {(successful ? "<true />" : "<false />")}
        </dict>
        </plist>
        """;

    private static string Message(string text) =>
        $"<key>marker-message</key>\n<string>{text}</string>";

    private const string Levels42 = "<key>marker-levels</key>\n<integer>42</integer>";

    private static string Dnp(int remaining) =>
        Message($"{remaining} native prints remaining on '4x6' media") + "\n" + Levels42
        + "\n<key>marker-change-time</key>\n<integer>1000</integer>\n<key>printer-up-time</key>\n<integer>1003</integer>";

    private static int? Remaining(string attributes) =>
        CupsMarkers.StatusOf(CupsMarkers.ParsePlist(Plist(attributes))).RemainingPrints;

    // --- what the printer says ---------------------------------------------

    [Fact]
    public void A_DNP_Count_Is_The_Number_Of_Prints_Remaining()
    {
        var status = CupsMarkers.StatusOf(CupsMarkers.ParsePlist(Plist(Dnp(187))));
        Assert.Equal(187, status.RemainingPrints);
        Assert.Equal(3, status.ObservationAgeSeconds); // up-time minus change-time
    }

    [Theory]
    [InlineData("ipptool-dnp-187.plist", 187)]
    [InlineData("ipptool-levels-only.plist", null)]
    public void What_ipptool_Really_Prints_Is_Read(string fixture, int? expected)
    {
        // Captured from the real tool running the SHIPPED query, not written by hand.
        var plist = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var snapshot = CupsMarkers.ParsePlist(plist);
        Assert.NotNull(snapshot);
        Assert.Equal(expected, CupsMarkers.StatusOf(snapshot).RemainingPrints);
        Assert.Contains(42, snapshot!.Levels); // read, and still not turned into prints
    }

    [Fact]
    public void Zero_Is_Media_Spent_Not_Unknown() => Assert.Equal(0, Remaining(Dnp(0)));

    [Fact]
    public void No_Marker_Message_Is_No_Count() => Assert.Null(Remaining(Levels42));

    [Fact]
    public void A_Percentage_Is_Never_Turned_Into_Prints()
    {
        // marker-levels alone: a level, not a count — no capacity is assumed.
        Assert.Null(Remaining(Levels42));
        Assert.Null(Remaining(Message("Ribbon 42%") + "\n" + Levels42));
    }

    [Theory]
    [InlineData("Ribbon low")]
    [InlineData("prints remaining: 187")]
    [InlineData("187 native prints rem")]                 // truncated
    [InlineData("-5 native prints remaining on '4x6' media")] // negative
    [InlineData("999999999 native prints remaining on '4x6' media")] // implausibly large
    [InlineData("12345678901234567890 prints remaining")] // overflow
    [InlineData("")]
    public void Anything_That_Is_Not_A_Plain_Count_Is_No_Count(string message) =>
        Assert.Null(Remaining(Message(message)));

    [Theory]
    [InlineData("   187   native   prints remaining on '4x6' media", 187)]
    [InlineData("187 prints remaining", 187)]
    [InlineData("1 native print remaining on '6x8' media", 1)]
    [InlineData("100000 native prints remaining", 100000)]
    public void Whitespace_And_Wording_Variants_Still_Read(string message, int expected) =>
        Assert.Equal(expected, Remaining(Message(message)));

    [Fact]
    public void Several_Messages_Must_Agree_Or_There_Is_No_Number()
    {
        var agreeing = "<key>marker-message</key>\n<array>\n<string>187 native prints remaining</string>\n"
            + "<string>Ribbon OK</string>\n<string>187 prints remaining</string>\n</array>";
        Assert.Equal(187, Remaining(agreeing));
        var disagreeing = "<key>marker-message</key>\n<array>\n<string>187 native prints remaining</string>\n"
            + "<string>40 native prints remaining</string>\n</array>";
        Assert.Null(Remaining(disagreeing));
    }

    [Fact]
    public void A_Failed_Or_Malformed_Report_Is_No_Count()
    {
        Assert.Null(CupsMarkers.StatusOf(CupsMarkers.ParsePlist(Plist(Dnp(187), successful: false))).RemainingPrints);
        Assert.Null(CupsMarkers.ParsePlist("<plist><dict>"));
        Assert.Null(CupsMarkers.ParsePlist(""));
        Assert.Null(CupsMarkers.ParsePlist("ipptool: Unable to connect to \"localhost\""));
    }

    [Fact]
    public void The_Report_Never_Resolves_An_Entity()
    {
        var hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [ <!ENTITY secret SYSTEM "file:///etc/passwd"> ]>
            <plist><dict><key>Tests</key><array><dict><key>Successful</key><true />
            <key>ResponseAttributes</key><array><dict><key>marker-message</key>
            <string>&secret;</string></dict></array></dict></array></dict></plist>
            """;
        Assert.Null(CupsMarkers.StatusOf(CupsMarkers.ParsePlist(hostile)).RemainingPrints);
    }

    [Fact]
    public void An_Old_Reading_Is_Bounded_And_A_Missing_Clock_Is_Unknown_Age()
    {
        var ancient = Message("187 prints remaining")
            + "\n<key>marker-change-time</key>\n<integer>1</integer>\n<key>printer-up-time</key>\n<integer>99999999999</integer>";
        Assert.Equal((int)CupsMarkers.MaxAgeSeconds,
            CupsMarkers.StatusOf(CupsMarkers.ParsePlist(Plist(ancient))).ObservationAgeSeconds);
        Assert.Null(CupsMarkers.StatusOf(CupsMarkers.ParsePlist(Plist(Message("187 prints remaining"))))
            .ObservationAgeSeconds);
    }

    // --- the adapter -------------------------------------------------------

    private static (CupsPrinterAdapter Adapter, FakeProcessRunner Runner, string QueryFile) Adapter(
        Func<IReadOnlyList<string>, ProcessResult> ipptool)
    {
        var query = Path.GetTempFileName();
        var runner = new FakeProcessRunner((file, args) => file switch
        {
            "ipptool" => ipptool(args),
            "lpstat" when args.SequenceEqual(["-r"]) => FakeProcessRunner.Ok("scheduler is running\n"),
            "lpstat" => FakeProcessRunner.Ok($"printer {Photo} is idle.\n\tAlerts: none\nprinter {Strip} is idle.\n"),
            "lp" => FakeProcessRunner.Ok($"request id is {Photo}-7 (1 file(s))\n"),
            _ => ProcessResult.Missing(file),
        });
        return (new CupsPrinterAdapter(Photo, Strip, runner, NullLogger<CupsPrinterAdapter>.Instance, query),
            runner, query);
    }

    private static DiscoveredPrinter Device(string key = Photo) => new(key, key, "DNP", key, "cups");

    private static int IppCalls(FakeProcessRunner runner) => runner.Calls.Count(c => c.File == "ipptool");

    [Fact]
    public async Task The_Query_Asks_The_Photo_Queue_With_Safe_Bounded_Arguments()
    {
        var (adapter, runner, query) = Adapter(_ => FakeProcessRunner.Ok(Plist(Dnp(187))));
        try
        {
            Assert.Equal(187, (await adapter.GetMediaStatusAsync(Device(), default)).RemainingPrints);
            var call = Assert.Single(runner.Calls, c => c.File == "ipptool");
            Assert.Equal(["-T", "5", "-X", $"ipp://localhost/printers/{Photo}", query], call.Args);
        }
        finally { File.Delete(query); }
    }

    [Fact]
    public async Task Photo_And_Strip_Are_One_Printer_And_One_Media()
    {
        var (adapter, runner, query) = Adapter(_ => FakeProcessRunner.Ok(Plist(Dnp(187))));
        try
        {
            var photo = await adapter.GetMediaStatusAsync(Device(Photo), default);
            var strip = await adapter.GetMediaStatusAsync(Device(Strip), default);
            Assert.Equal(187, photo.RemainingPrints);
            Assert.Equal(187, strip.RemainingPrints); // the same media, never 374
            Assert.Equal(1, IppCalls(runner));
            Assert.DoesNotContain(runner.Calls, c => c.File == "ipptool" && c.Args.Any(a => a.Contains(Strip)));
        }
        finally { File.Delete(query); }
    }

    [Fact]
    public async Task One_Heartbeat_Asks_Once_And_A_Submitted_Sheet_Asks_Again()
    {
        var remaining = 187;
        var (adapter, runner, query) = Adapter(_ => FakeProcessRunner.Ok(Plist(Dnp(remaining))));
        var artifact = Path.GetTempFileName();
        try
        {
            await adapter.GetMediaStatusAsync(Device(), default);
            await adapter.GetMediaStatusAsync(Device(), default);
            Assert.Equal(1, IppCalls(runner));

            // A sheet goes to CUPS: the count is NOT decremented here…
            Assert.True((await adapter.SubmitAsync(
                new PrintSubmission(Guid.NewGuid(), Photo, artifact, "image/jpeg", "10x15"), default)).Accepted);
            // …it is asked again, and converges to what the printer reports.
            remaining = 186;
            Assert.Equal(186, (await adapter.GetMediaStatusAsync(Device(), default)).RemainingPrints);
            Assert.Equal(2, IppCalls(runner));
        }
        finally { File.Delete(query); File.Delete(artifact); }
    }

    [Fact]
    public async Task No_Count_Never_Makes_A_Ready_Printer_Offline()
    {
        var (adapter, _, query) = Adapter(_ => FakeProcessRunner.Ok(Plist(Levels42)));
        try
        {
            Assert.Null((await adapter.GetMediaStatusAsync(Device(), default)).RemainingPrints);
            Assert.Equal("ready", (await adapter.GetStatusAsync(Device(), default)).State);
        }
        finally { File.Delete(query); }
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("missing")]
    public async Task A_Print_System_That_Cannot_Answer_Is_No_Count_Not_A_Crash(string failure)
    {
        var (adapter, _, query) = Adapter(_ => failure switch
        {
            "unavailable" => new ProcessResult(1, Plist("", successful: false), "Unable to connect"),
            "timeout" => ProcessResult.Expired("ipptool"),
            _ => ProcessResult.Missing("ipptool"),
        });
        try
        {
            Assert.Equal(PrinterMediaStatus.Unavailable, await adapter.GetMediaStatusAsync(Device(), default));
        }
        finally { File.Delete(query); }
    }

    [Fact]
    public async Task A_Queue_Name_That_Is_Not_Plain_Is_Never_Put_In_A_URI()
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok(Plist(Dnp(187))));
        var query = Path.GetTempFileName();
        try
        {
            var adapter = new CupsPrinterAdapter(null, null, runner, NullLogger<CupsPrinterAdapter>.Instance, query);
            foreach (var hostile in new[] { "evil/../../jobs", "a b", "x?y=1", "../x", "" })
                Assert.Null((await adapter.GetMediaStatusAsync(Device(hostile), default)).RemainingPrints);
            Assert.DoesNotContain(runner.Calls, c => c.File == "ipptool");
        }
        finally { File.Delete(query); }
    }

    [Fact]
    public async Task Without_The_Shipped_Query_There_Is_No_Count_And_No_Process()
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok(Plist(Dnp(187))));
        var adapter = new CupsPrinterAdapter(Photo, null, runner, NullLogger<CupsPrinterAdapter>.Instance,
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.test"));
        Assert.Null((await adapter.GetMediaStatusAsync(Device(), default)).RemainingPrints);
        Assert.DoesNotContain(runner.Calls, c => c.File == "ipptool");
    }

    // --- what travels ------------------------------------------------------

    [Fact]
    public void The_Heartbeat_Says_Available_Or_Not_And_Never_Omits_It()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var report = new AgentDeviceReport(Photo, Photo, "DNP", Photo, "cups",
            new PrinterCapabilities(["10x15"], true), "ready", AgentMediaRemaining.From(new PrinterMediaStatus(187, 3)));
        var json = JsonDocument.Parse(JsonSerializer.Serialize(report, web)).RootElement.GetProperty("mediaRemaining");
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal(187, json.GetProperty("remainingPrints").GetInt32());
        Assert.Equal(3, json.GetProperty("ageSeconds").GetInt32());

        var none = AgentMediaRemaining.From(PrinterMediaStatus.Unavailable);
        Assert.False(none.Available);
        Assert.Null(none.RemainingPrints);
        Assert.Equal(new AgentMediaRemaining(true, 0, null), AgentMediaRemaining.From(new PrinterMediaStatus(0)));
    }

    [Fact]
    public async Task The_Simulator_Reports_A_Deterministic_Count_That_Falls_With_Each_Sheet()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nubarca-fake-media-{Guid.NewGuid():N}");
        var artifact = Path.GetTempFileName();
        try
        {
            var fake = new FakePrinterAdapter(output, TimeSpan.Zero, remainingPrints: 3);
            var printer = (await fake.DiscoverAsync(default))[0];
            Assert.Equal(3, (await fake.GetMediaStatusAsync(printer, default)).RemainingPrints);
            await fake.SubmitAsync(new PrintSubmission(Guid.NewGuid(), printer.DeviceKey, artifact, "image/jpeg", "10x15"), default);
            Assert.Equal(2, (await fake.GetMediaStatusAsync(printer, default)).RemainingPrints);

            var silent = new FakePrinterAdapter(output, TimeSpan.Zero);
            Assert.Null((await silent.GetMediaStatusAsync(printer, default)).RemainingPrints);
        }
        finally
        {
            File.Delete(artifact);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_Reports_No_Count_Without_Touching_Anything()
    {
        var adapter = new WindowsSpoolerPrinterAdapter("DNP DS-RX1HS");
        Assert.Equal(PrinterMediaStatus.Unavailable,
            await adapter.GetMediaStatusAsync(new DiscoveredPrinter("q", "q", null, null, "windows-spooler"), default));
    }

    [Fact]
    public void The_Box_Ships_Its_Own_Narrow_Query_And_Installs_ipptool()
    {
        var linux = Path.Combine(AppContext.BaseDirectory, "linux");
        Assert.Contains("cups-ipp-utils", File.ReadAllText(Path.Combine(linux, "install-print-box.sh")),
            StringComparison.Ordinal);
        var query = File.ReadAllText(Path.Combine(linux, "nubarca-media-status.test"));
        Assert.Contains("OPERATION Get-Printer-Attributes", query, StringComparison.Ordinal);
        Assert.Contains(
            "requested-attributes marker-message,marker-levels,marker-change-time,printer-up-time",
            query, StringComparison.Ordinal);
        Assert.Equal(CupsPrinterAdapter.DefaultMediaQueryPath, Path.Combine(linux, "nubarca-media-status.test"));
    }

    [Fact]
    public void A_Simulator_Count_Out_Of_Range_Is_Refused()
    {
        foreach (var bad in new int?[] { -1, CupsMarkers.MaxPlausiblePrints + 1 })
        {
            var options = new PrintAgentOptions { Adapter = "fake", FakeRemainingPrints = bad };
            Assert.Throws<InvalidOperationException>(() => options.NormalizeAndValidate(isLinux: false));
        }
    }
}

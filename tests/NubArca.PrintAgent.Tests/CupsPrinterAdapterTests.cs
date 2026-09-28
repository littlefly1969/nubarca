using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Execution;

namespace NubArca.PrintAgent.Tests;

public sealed class CupsPrinterAdapterTests
{
    private const string Photo = "NubArca-RX1HS";
    private const string Strip = "NubArca-RX1HS-STRIP";

    private const string BothIdle = """
        printer NubArca-RX1HS is idle.  enabled since Sat 27 Sep 2026 10:00:00 PM CEST
        	Form mounted:
        	Content types: any
        	Description: DNP DS-RX1HS
        	Alerts: none
        	Location:
        printer NubArca-RX1HS-STRIP is idle.  enabled since Sat 27 Sep 2026 10:00:00 PM CEST
        	Form mounted:
        	Description: DNP DS-RX1HS strips
        	Alerts: none
        """;

    private static (CupsPrinterAdapter Adapter, FakeProcessRunner Runner) Adapter(
        string lpstat, string? strip = Strip, Func<IReadOnlyList<string>, ProcessResult>? lp = null)
    {
        var runner = new FakeProcessRunner((file, args) => file switch
        {
            "lpstat" when args.SequenceEqual(["-r"]) => FakeProcessRunner.Ok("scheduler is running\n"),
            "lpstat" => FakeProcessRunner.Ok(lpstat),
            "lp" => lp?.Invoke(args) ?? FakeProcessRunner.Ok("request id is NubArca-RX1HS-42 (1 file(s))\n"),
            _ => ProcessResult.Missing(file),
        });
        return (new CupsPrinterAdapter(Photo, strip, runner, NullLogger<CupsPrinterAdapter>.Instance), runner);
    }

    private static DiscoveredPrinter Device(string key = Photo) => new(key, key, "DNP", key, "cups");

    [Fact]
    public async Task Photo_And_Strip_Queues_Are_One_Printer()
    {
        var (adapter, _) = Adapter(BothIdle);
        var printer = Assert.Single(await adapter.DiscoverAsync(default));
        Assert.Equal(Photo, printer.DeviceKey);
        Assert.Equal("cups", printer.AdapterKind);
    }

    [Fact]
    public async Task Without_A_Configured_Printer_Every_Queue_Is_Listed()
    {
        var runner = new FakeProcessRunner((_, _) => FakeProcessRunner.Ok(BothIdle + "\nprinter Office is idle.\n"));
        var adapter = new CupsPrinterAdapter(null, null, runner, NullLogger<CupsPrinterAdapter>.Instance);
        Assert.Equal([Photo, Strip, "Office"], (await adapter.DiscoverAsync(default)).Select(p => p.DeviceKey).Order());
    }

    [Fact]
    public async Task The_Strip_Queue_Decides_Whether_Strips_Are_Offered()
    {
        var (both, _) = Adapter(BothIdle);
        Assert.Equal(["10x15", "2x6x2"], (await both.GetCapabilitiesAsync(Device(), default)).Formats);
        Assert.True((await both.GetCapabilitiesAsync(Device(), default)).Color);

        // Configured but not in CUPS: never advertised.
        var (missing, _) = Adapter("printer NubArca-RX1HS is idle.\n");
        Assert.Equal(["10x15"], (await missing.GetCapabilitiesAsync(Device(), default)).Formats);

        // Not configured at all.
        var (none, _) = Adapter(BothIdle, strip: null);
        Assert.Equal(["10x15"], (await none.GetCapabilitiesAsync(Device(), default)).Formats);
    }

    [Theory]
    [InlineData("10x15", Photo)]
    [InlineData("2x6x2", Strip)]
    public async Task Each_Format_Goes_To_Its_Queue_With_Safe_Arguments(string format, string queue)
    {
        var (adapter, runner) = Adapter(BothIdle);
        var artifact = Path.GetTempFileName();
        try
        {
            var jobId = Guid.Parse("0d99a1b2-0000-0000-0000-000000000000");
            var result = await adapter.SubmitAsync(new PrintSubmission(jobId, Photo, artifact, "image/jpeg", format), default);

            Assert.True(result.Accepted);
            Assert.Equal("NubArca-RX1HS-42", result.SpoolReference);
            var lp = Assert.Single(runner.Calls, c => c.File == "lp");
            // One argv entry each; no shell, no concatenation.
            Assert.Equal(["-d", queue, "-t", "NubArca-0d99a1b2", "-o", "fit-to-page", artifact], lp.Args);
        }
        finally { File.Delete(artifact); }
    }

    [Fact]
    public async Task A_Strip_Without_Its_Queue_Is_Refused_Before_CUPS_Is_Asked()
    {
        var (adapter, runner) = Adapter(BothIdle, strip: null);
        var result = await adapter.SubmitAsync(
            new PrintSubmission(Guid.NewGuid(), Photo, "/nonexistent", "image/jpeg", "2x6x2"), default);
        Assert.False(result.Accepted);
        Assert.Equal("format_unsupported", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, c => c.File == "lp");
    }

    [Theory]
    [InlineData(1, "lp: The printer or class does not exist.", "printer_not_found")]
    [InlineData(1, "lp: Destination \"NubArca-RX1HS\" is not accepting jobs.", "printer_not_accepting")]
    [InlineData(1, "lp: Unable to connect to server: Connection refused", "cups_unavailable")]
    [InlineData(127, "lp: not found", "cups_unavailable")]
    [InlineData(1, "lp: something else went wrong", "submit_failed")]
    public async Task A_Refused_Job_Is_A_Stable_Code_Not_An_Exception(int exit, string stderr, string code)
    {
        var (adapter, _) = Adapter(BothIdle, lp: _ => FakeProcessRunner.Fail(stderr, exit));
        var artifact = Path.GetTempFileName();
        try
        {
            var result = await adapter.SubmitAsync(
                new PrintSubmission(Guid.NewGuid(), Photo, artifact, "image/jpeg", "10x15"), default);
            Assert.False(result.Accepted);
            Assert.Equal(code, result.FailureCode);
            Assert.Null(result.SpoolReference);
        }
        finally { File.Delete(artifact); }
    }

    [Fact]
    public async Task A_Missing_Artifact_Is_Not_Sent()
    {
        var (adapter, runner) = Adapter(BothIdle);
        var result = await adapter.SubmitAsync(
            new PrintSubmission(Guid.NewGuid(), Photo, "/nonexistent/artifact.jpg", "image/jpeg", "10x15"), default);
        Assert.Equal("artifact_missing", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, c => c.File == "lp");
    }

    [Fact]
    public async Task Without_CUPS_The_Agent_Reports_Offline_Instead_Of_Failing()
    {
        var runner = new FakeProcessRunner((file, _) => ProcessResult.Missing(file));
        var adapter = new CupsPrinterAdapter(Photo, Strip, runner, NullLogger<CupsPrinterAdapter>.Instance);
        Assert.Empty(await adapter.DiscoverAsync(default));
        var status = await adapter.GetStatusAsync(Device(), default);
        Assert.Equal("offline", status.State);
        Assert.Equal("CUPS unavailable", status.Detail);
        Assert.False(await adapter.IsAvailableAsync(default));

        var down = new FakeProcessRunner((_, _) => FakeProcessRunner.Fail("lpstat: Scheduler is not running."));
        var stopped = new CupsPrinterAdapter(Photo, Strip, down, NullLogger<CupsPrinterAdapter>.Instance);
        Assert.Equal("offline", (await stopped.GetStatusAsync(Device(), default)).State);
    }

    [Fact]
    public async Task A_Missing_Photo_Queue_Is_Offline_With_A_Reason()
    {
        var (adapter, _) = Adapter("printer Office is idle.\n");
        Assert.Empty(await adapter.DiscoverAsync(default));
        var status = await adapter.GetStatusAsync(Device(), default);
        Assert.Equal("offline", status.State);
        Assert.Equal("Queue missing in CUPS", status.Detail);
    }

    [Fact]
    public void Queue_State_Maps_To_What_The_Server_Accepts()
    {
        var queues = CupsOutput.ParseQueues("""
            printer Idle is idle.  enabled since Sat 27 Sep 2026
            	Alerts: none
            printer Busy now printing Busy-7.  enabled since Sat 27 Sep 2026
            	Sending data to printer.
            	Alerts: none
            printer Stopped disabled since Sat 27 Sep 2026 -
            	Printer stopped due to backend errors
            printer Unplugged is idle.  enabled since Sat 27 Sep 2026
            	Waiting for printer to become available.
            	Alerts: offline-report
            printer Empty is idle.  enabled since Sat 27 Sep 2026
            	Alerts: media-empty-error marker-supply-low-warning
            """);

        Assert.Equal("ready", CupsOutput.StatusOf(queues["Idle"]).State);
        Assert.Null(CupsOutput.StatusOf(queues["Idle"]).Detail);
        Assert.Equal("busy", CupsOutput.StatusOf(queues["Busy"]).State);
        Assert.Equal("Sending data to printer.", CupsOutput.StatusOf(queues["Busy"]).Detail);
        Assert.Equal("error", CupsOutput.StatusOf(queues["Stopped"]).State);
        Assert.Equal("Printer stopped due to backend errors", CupsOutput.StatusOf(queues["Stopped"]).Detail);
        Assert.Equal("offline", CupsOutput.StatusOf(queues["Unplugged"]).State);
        Assert.Equal("error", CupsOutput.StatusOf(queues["Empty"]).State);
        Assert.Equal("media-empty-error, marker-supply-low-warning", CupsOutput.StatusOf(queues["Empty"]).Detail);
        // Queue names are case-insensitive in CUPS.
        Assert.True(queues.ContainsKey("idle"));
    }

    [Fact]
    public void Request_Ids_And_Scheduler_State_Are_Read()
    {
        Assert.Equal("Q-12", CupsOutput.ParseRequestId("request id is Q-12 (1 file(s))\n"));
        Assert.Null(CupsOutput.ParseRequestId(""));
        Assert.True(CupsOutput.SchedulerRunning("scheduler is running\n"));
        Assert.False(CupsOutput.SchedulerRunning("scheduler is not running\n"));
    }

    [Fact]
    public void Cups_Is_The_Linux_Adapter_And_Refused_Elsewhere()
    {
        var options = new PrintAgentOptions { Adapter = PrintAdapterKinds.Cups, PrinterName = Photo };
        options.NormalizeAndValidate();
        if (OperatingSystem.IsLinux())
            Assert.IsType<CupsPrinterAdapter>(PrintAgentPlatform.CreatePrinterAdapter(options));
        else
            Assert.Throws<PlatformNotSupportedException>(() => PrintAgentPlatform.CreatePrinterAdapter(options));
    }
}

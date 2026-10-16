using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Execution;
using NubArca.PrintAgent.Journal;

namespace NubArca.PrintAgent.Tests;

/// <summary>
/// The simulated twin of a CUPS printer: its jobs reach the twin queue — same
/// driver, a backend that writes a file — and never paper; a job that should
/// be simulated and finds no twin is refused rather than printed; status and
/// media are the twin's while the printer is simulated, and a restart never
/// forgets which of its jobs were.
/// </summary>
public sealed class PrinterSimulationTests : IDisposable
{
    private const string Photo = "NubArca-RX1HS";
    private const string Strip = "NubArca-RX1HS-STRIP";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nubarca-sim-{Guid.NewGuid():N}");

    public PrinterSimulationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Queue(string name, string state = "idle") =>
        $"printer {name} is {state}.  enabled since Sat 27 Sep 2026 10:00:00 PM CEST\n\tAlerts: none\n";

    private static readonly string WithTwins =
        Queue(Photo) + Queue(Strip) + Queue(Photo + "-SIM") + Queue(Strip + "-SIM");

    private (CupsPrinterAdapter Adapter, FakeProcessRunner Runner) Adapter(string lpstat,
        bool only = false, string? configured = Photo, string? strip = Strip, string? ipptool = null)
    {
        var query = Path.Combine(_root, "media.test");
        File.WriteAllText(query, "{}");
        var runner = new FakeProcessRunner((file, args) => file switch
        {
            "lpstat" when args.SequenceEqual(["-r"]) => FakeProcessRunner.Ok("scheduler is running\n"),
            "lpstat" => FakeProcessRunner.Ok(lpstat),
            "lp" => FakeProcessRunner.Ok($"request id is {args[1]}-7 (1 file(s))\n"),
            "lpoptions" => FakeProcessRunner.Ok("PageSize/Media Size: *w288h432 w288h432-div2 w360h504 w432h576\n"),
            "ipptool" => ipptool is null ? FakeProcessRunner.Fail("no markers") : FakeProcessRunner.Ok(ipptool),
            _ => ProcessResult.Missing(file),
        });
        var adapter = new CupsPrinterAdapter(configured, strip, runner, NullLogger<CupsPrinterAdapter>.Instance,
            query, new PrintSimulationOptions { QueueSuffix = "-SIM", Only = only });
        return (adapter, runner);
    }

    private PrintSubmission Job(string format, bool simulated)
    {
        var artifact = Path.Combine(_root, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(artifact, [1, 2, 3]);
        return new PrintSubmission(Guid.NewGuid(), Photo, artifact, "image/png", format, simulated);
    }

    private static string? LpQueue(FakeProcessRunner runner) =>
        runner.Calls.Where(c => c.File == "lp").Select(c => c.Args[1]).LastOrDefault();

    [Theory]
    [InlineData("10x15", Photo + "-SIM")]
    [InlineData("2x6x2", Strip + "-SIM")]
    public async Task A_Simulated_Job_Goes_To_The_Twin_Of_Its_Queue(string format, string twin)
    {
        var (adapter, runner) = Adapter(WithTwins);
        var result = await adapter.SubmitAsync(Job(format, simulated: true), default);
        Assert.True(result.Accepted);
        Assert.Equal(twin, LpQueue(runner));
    }

    [Fact]
    public async Task Without_Its_Twin_A_Simulated_Job_Is_Refused_And_Nothing_Is_Printed()
    {
        var (adapter, runner) = Adapter(Queue(Photo) + Queue(Strip));
        var result = await adapter.SubmitAsync(Job("10x15", simulated: true), default);
        Assert.False(result.Accepted);
        Assert.Equal("simulation_unavailable", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, c => c.File == "lp");
    }

    [Fact]
    public async Task A_Real_Job_Goes_To_Paper_Even_While_The_Printer_Is_Simulated()
    {
        // Created before the owner switched: it was a real print, and stays one.
        var (adapter, runner) = Adapter(WithTwins);
        adapter.UseSimulation(new HashSet<string> { Photo });
        Assert.True((await adapter.SubmitAsync(Job("10x15", simulated: false), default)).Accepted);
        Assert.Equal(Photo, LpQueue(runner));
    }

    [Fact]
    public async Task The_Twin_Is_Offered_Only_When_It_Exists()
    {
        var device = new DiscoveredPrinter(Photo, Photo, "DNP", Photo, "cups");
        Assert.Equal(new PrinterSimulation(true, false), await Adapter(WithTwins).Adapter.GetSimulationAsync(device, default));
        Assert.Equal(PrinterSimulation.None,
            await Adapter(Queue(Photo) + Queue(Strip)).Adapter.GetSimulationAsync(device, default));
        Assert.True(Adapter(WithTwins).Adapter.SupportsSimulation);
    }

    [Fact]
    public async Task While_Simulated_The_Printer_Is_Its_Twin_Ready_With_Its_Media()
    {
        // The real DNP is unplugged; its twin is idle.
        var lpstat = Queue(Photo, "stopped") + Queue(Strip, "stopped") + Queue(Photo + "-SIM") + Queue(Strip + "-SIM");
        var plist = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ipptool-dnp-187.plist"));
        var (adapter, runner) = Adapter(lpstat, ipptool: plist);
        var device = new DiscoveredPrinter(Photo, Photo, "DNP", Photo, "cups");
        Assert.NotEqual("ready", (await adapter.GetStatusAsync(device, default)).State);

        adapter.UseSimulation(new HashSet<string> { Photo });
        Assert.Equal("ready", (await adapter.GetStatusAsync(device, default)).State);
        Assert.Equal(187, (await adapter.GetMediaStatusAsync(device, default)).RemainingPrints);
        // Asked of the twin, through the same IPP query as a real DNP.
        Assert.Contains(runner.Calls, c => c.File == "ipptool" && c.Args.Contains($"ipp://localhost/printers/{Photo}-SIM"));
        Assert.Contains("2x6x2", (await adapter.GetCapabilitiesAsync(device, default)).Formats);
    }

    [Fact]
    public async Task Before_Its_First_Job_The_Twin_Holds_A_Hundred_Prints()
    {
        var (adapter, _) = Adapter(WithTwins, ipptool: null);
        adapter.UseSimulation(new HashSet<string> { Photo });
        var media = await adapter.GetMediaStatusAsync(new DiscoveredPrinter(Photo, Photo, "DNP", Photo, "cups"), default);
        Assert.Equal(PrintSimulationOptions.RemainingPrints, media.RemainingPrints);
    }

    [Fact]
    public async Task A_Box_With_Only_Twins_Is_Always_Simulated_On_Its_Own_Queues()
    {
        const string twin = "NubArca-sala-SIM";
        var (adapter, runner) = Adapter(Queue(twin) + Queue("NubArca-sala-STRIP-SIM"), only: true,
            configured: twin, strip: "NubArca-sala-STRIP-SIM");
        var device = Assert.Single(await adapter.DiscoverAsync(default));
        Assert.True(adapter.IsSimulated(device.DeviceKey));
        Assert.Equal(new PrinterSimulation(true, true), await adapter.GetSimulationAsync(device, default));
        var job = Job("10x15", simulated: true) with { DeviceKey = twin };
        Assert.True((await adapter.SubmitAsync(job, default)).Accepted);
        Assert.Equal(twin, LpQueue(runner));
    }

    [Fact]
    public async Task An_Adapter_Without_A_Twin_Refuses_A_Simulated_Job()
    {
        var fake = new FakePrinterAdapter(Path.Combine(_root, "out"), TimeSpan.Zero);
        var result = await fake.SubmitAsync(Job("10x15", simulated: true), default);
        Assert.Equal("simulation_unavailable", result.FailureCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
        Assert.False(((IPrinterAdapter)fake).SupportsSimulation);
    }

    // --- The twins themselves ---------------------------------------------------------

    [Fact]
    public async Task The_Twins_Are_Made_With_The_Real_Driver_On_The_Simulation_Backend_Without_A_Printer()
    {
        var runner = new FakeProcessRunner((file, args) => (file, args.FirstOrDefault()) switch
        {
            ("lpinfo", "-m") => FakeProcessRunner.Ok(
                "gutenprint.5.3://dnp-dsrx1/expert DNP DS-RX1 - CUPS+Gutenprint v5.3.4\n"),
            ("lpoptions", _) => FakeProcessRunner.Ok("PageSize/Media Size: *w288h432 w288h432-div2 w360h504\n"),
            ("lpadmin", _) => FakeProcessRunner.Ok(),
            _ => ProcessResult.Missing(file),
        });
        var result = await new CupsQueueSetup(runner).RunSimulationAsync(Photo + "-SIM", Strip + "-SIM", default);

        Assert.True(result.PhotoQueue);
        Assert.True(result.StripQueue);
        // No DNP was looked for.
        Assert.DoesNotContain(runner.Calls, c => c.File == "lpinfo" && c.Args.SequenceEqual(["-v"]));
        var created = runner.Calls.Where(c => c.File == "lpadmin" && c.Args.Contains("-v")).ToList();
        Assert.Equal(2, created.Count);
        Assert.All(created, c =>
        {
            Assert.Contains(CupsQueueSetup.SimulationUri, c.Args);
            Assert.Contains("gutenprint.5.3://dnp-dsrx1/expert", c.Args);
        });
        Assert.Contains(runner.Calls, c => c.File == "lpadmin"
            && c.Args.SequenceEqual(["-p", Strip + "-SIM", "-o", $"PageSize={CupsQueueSetup.CutSize}"]));
    }

    [Fact]
    public async Task A_Restart_Remembers_Which_Jobs_Were_Simulated_Even_On_An_Older_Journal()
    {
        var path = Path.Combine(_root, "journal.db");
        // A journal written before simulation existed.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE executions (job_id TEXT PRIMARY KEY, claim_token TEXT NOT NULL, artifact_path TEXT NOT NULL,
                  device_key TEXT NOT NULL, content_type TEXT NOT NULL, format TEXT NOT NULL, state TEXT NOT NULL,
                  failure_code TEXT NULL, spool_reference TEXT NULL, updated_at TEXT NOT NULL);
                INSERT INTO executions VALUES ('11111111-1111-1111-1111-111111111111','c','/a','dnp','image/png','10x15','claimed',NULL,NULL,'2026-01-01');
                """;
            await create.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var journal = new ExecutionJournal(path);
        await journal.InitializeAsync(default);
        var old = Assert.Single(await journal.LoadPendingAsync(default));
        Assert.False(old.Simulated);

        var simulated = new JournalEntry(Guid.NewGuid(), "c2", "/b", "dnp", "image/png", "10x15",
            LocalExecutionStates.Claimed, null, null, Simulated: true);
        await journal.UpsertClaimedAsync(simulated, default);
        Assert.True((await journal.LoadPendingAsync(default)).Single(e => e.JobId == simulated.JobId).Simulated);
    }

    [Fact]
    public async Task With_Simulation_Requested_And_The_Twin_Gone_Nothing_Reaches_Any_Queue()
    {
        // The owner switched to the twin; the twin has since disappeared.
        var (adapter, runner) = Adapter(Queue(Photo) + Queue(Strip));
        adapter.UseSimulation(new HashSet<string> { Photo });
        var device = new DiscoveredPrinter(Photo, Photo, "DNP", Photo, "cups");
        // Reported through its (missing) twin: not ready, so no job is claimed for it…
        Assert.NotEqual("ready", (await adapter.GetStatusAsync(device, default)).State);
        Assert.Equal(PrinterSimulation.None, await adapter.GetSimulationAsync(device, default));
        // …and a simulated job that reaches it anyway is refused, not printed.
        foreach (var format in new[] { "10x15", "2x6x2" })
        {
            var result = await adapter.SubmitAsync(Job(format, simulated: true), default);
            Assert.Equal("simulation_unavailable", result.FailureCode);
        }
        Assert.DoesNotContain(runner.Calls, c => c.File == "lp");
    }

    [Fact]
    public async Task A_Simulated_Job_Is_Titled_By_Its_Whole_Id_And_A_Real_One_As_Before()
    {
        var (adapter, runner) = Adapter(WithTwins);
        var simulated = Job("10x15", simulated: true);
        await adapter.SubmitAsync(simulated, default);
        var real = Job("10x15", simulated: false);
        await adapter.SubmitAsync(real, default);
        var titles = runner.Calls.Where(c => c.File == "lp").Select(c => c.Args[3]).ToList();
        Assert.Equal([$"NubArca-{simulated.JobId:N}", $"NubArca-{real.JobId.ToString("N")[..8]}"], titles);
    }

    [Fact]
    public void A_Printers_Twin_Queues_Are_Its_Own_Photo_And_Strip_Twins()
    {
        var (adapter, _) = Adapter(WithTwins);
        Assert.Equal([Photo + "-SIM", Strip + "-SIM"], adapter.SimulatedQueuesOf(Photo));
        var (only, _) = Adapter(Queue("NubArca-sala-SIM"), only: true, configured: "NubArca-sala-SIM",
            strip: "NubArca-sala-STRIP-SIM");
        Assert.Equal(["NubArca-sala-SIM", "NubArca-sala-STRIP-SIM"], only.SimulatedQueuesOf("NubArca-sala-SIM"));
    }

    /// <summary>Two simulated printers on one host, each with a photo and a strip twin, and a job file in each.</summary>
    private (string Root, string[] A, string[] B) TwoPrinters()
    {
        var root = Path.Combine(_root, "spool");
        string[] a = ["NubArca-a-SIM", "NubArca-a-STRIP-SIM"];
        string[] b = ["NubArca-b-SIM", "NubArca-b-STRIP-SIM"];
        foreach (var queue in a.Concat(b)) Directory.CreateDirectory(Path.Combine(root, queue));
        return (root, a, b);
    }

    private static string Id(char c) => new(c, 32);

    private static void Output(string root, string queue, string id)
    {
        File.WriteAllText(Path.Combine(root, queue, id + ".prn"), "raster");
        File.WriteAllText(Path.Combine(root, queue, id + ".txt"), "queue=" + queue);
    }

    private static bool Has(string root, string queue, string id) => File.Exists(Path.Combine(root, queue, id + ".prn"));

    [Fact]
    public void Clearing_One_Printer_Deletes_Its_Cleared_Jobs_And_Nothing_Of_Another_Printer_Or_Of_Later_Jobs()
    {
        var (root, a, b) = TwoPrinters();
        Output(root, a[0], Id('1'));
        Output(root, a[1], Id('2'));
        Output(root, a[0], Id('3')); // printed after the clear: not named by it
        Output(root, b[0], Id('4'));
        Output(root, b[1], Id('1')); // even a same-named file in another printer's queue

        Assert.Equal(4, SimulatedOutput.Purge(root, a, [Id('1'), Id('2')], NullLogger.Instance));
        Assert.False(Has(root, a[0], Id('1')));
        Assert.False(Has(root, a[1], Id('2')));
        Assert.True(Has(root, a[0], Id('3')));
        Assert.True(Has(root, b[0], Id('4')));
        Assert.True(Has(root, b[1], Id('1')));
        // Again: nothing more to delete.
        Assert.Equal(0, SimulatedOutput.Purge(root, a, [Id('1'), Id('2')], NullLogger.Instance));
        // Nothing malformed is ever a path.
        Assert.Equal(0, SimulatedOutput.Purge(root, ["../NubArca-b-SIM", a[0]], ["../" + Id('4'), "*", ""], NullLogger.Instance));
        Assert.True(Has(root, b[0], Id('4')));
    }

    [Fact]
    public void Each_Clear_Is_Applied_Once_And_Only_To_The_Printer_It_Names()
    {
        var (root, a, b) = TwoPrinters();
        var (adapter, _) = Adapter(Queue(Photo), configured: Photo, strip: Strip);
        // This agent's printer is Photo; its twins are Photo-SIM and Strip-SIM.
        foreach (var queue in adapter.SimulatedQueuesOf(Photo)) Directory.CreateDirectory(Path.Combine(root, queue));
        Output(root, Photo + "-SIM", Id('a'));
        Output(root, Strip + "-SIM", Id('a'));
        Output(root, b[0], Id('a'));
        var purger = new SimulatedOutputPurger(root, NullLogger.Instance);

        Assert.Equal(4, purger.Apply([new(Photo, 1, [Id('a')])], adapter));
        Assert.True(Has(root, b[0], Id('a')));
        // Printed again under the same name after the clear: the same sequence is not applied twice.
        Output(root, Photo + "-SIM", Id('a'));
        Assert.Equal(0, purger.Apply([new(Photo, 1, [Id('a')])], adapter));
        Assert.True(Has(root, Photo + "-SIM", Id('a')));
        // A newer clear is.
        Assert.Equal(2, purger.Apply([new(Photo, 2, [Id('a')])], adapter));
        Assert.Equal(0, purger.Apply(null, adapter));
    }

    // --- The backend CUPS runs -----------------------------------------------------------

    private static string Backend => Path.Combine(AppContext.BaseDirectory, "linux", "nubarca-sim");

    private (int Exit, string Errors) RunBackend(string spool, string job, string options = "fit-to-page",
        string queue = Photo + "-SIM", string? title = null)
    {
        var info = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { Backend, job, "lp", title ?? "Test page", "1", options }) info.ArgumentList.Add(argument);
        info.Environment["PRINTER"] = queue;
        info.Environment["SIM_SPOOL_ROOT"] = spool;
        using var process = Process.Start(info)!;
        try
        {
            process.StandardInput.Write("RASTER-FROM-GUTENPRINT");
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A backend that refuses the job may exit before reading it; its exit code is the answer.
        }
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, errors);
    }

    [Fact]
    public void The_Backend_Keeps_What_The_Driver_Sent_And_Reports_Its_Media_As_A_DNP_Does()
    {
        if (!OperatingSystem.IsLinux()) return;
        var spool = Path.Combine(_root, "spool");
        var (exit, errors) = RunBackend(spool, "42", "PageSize=w360h504 fit-to-page", title: $"NubArca-{Id('c')}");
        Assert.Equal(0, exit);
        var written = Assert.Single(Directory.GetFiles(Path.Combine(spool, Photo + "-SIM"), "*.prn"));
        // Named by the NubArca job its title carries, so a clear can name it.
        Assert.Equal(Id('c') + ".prn", Path.GetFileName(written));
        Assert.Equal("RASTER-FROM-GUTENPRINT", File.ReadAllText(written));
        Assert.Contains("media=5x7", File.ReadAllText(Path.ChangeExtension(written, ".txt")));
        // The very message the agent reads from a real DNP's driver.
        var message = errors.Split('\n').Single(l => l.StartsWith("ATTR: marker-message=", StringComparison.Ordinal));
        var snapshot = new CupsMarkerSnapshot(
            [message["ATTR: marker-message=".Length..].Trim('"')], [100], null, null);
        Assert.Equal(100, CupsMarkers.StatusOf(snapshot).RemainingPrints);
    }

    [Fact]
    public void The_Backend_Keeps_Only_The_Latest_Jobs_And_Refuses_A_Strange_Queue()
    {
        if (!OperatingSystem.IsLinux()) return;
        var spool = Path.Combine(_root, "spool");
        for (var job = 1; job <= 22; job++) Assert.Equal(0, RunBackend(spool, job.ToString()).Exit);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(spool, Photo + "-SIM"), "*.prn").Length);
        Assert.Equal(1, RunBackend(spool, "23", queue: "../escape").Exit);
        Assert.False(Directory.Exists(Path.Combine(_root, "escape")));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// A printer its owner lends to another account, and the paper that decides
/// what the printer is given — through the real endpoints and a real enrolled
/// station, so what is proven is what an agent and two people would meet.
/// </summary>
public sealed class PrinterSharingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrinterSharingTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private sealed record People(
        HttpClient Owner, Guid OwnerId, HttpClient Mario, Guid MarioId, HttpClient Stranger);

    private async Task<People> PeopleAsync()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var (marioId, mario) = await _factory.CreateAuthenticatedClientAsync("mario@example.com");
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("anna@example.com");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var user in db.Users.Where(u => u.Id == ownerId || u.Id == marioId))
            user.DisplayName = user.Id == ownerId ? "Stefano" : "Mario";
        await db.SaveChangesAsync();
        return new People(owner, ownerId, mario, marioId, stranger);
    }

    /// <summary>An enrolled station whose one printer prints 10x15 and 20x15 and cuts strips.</summary>
    private async Task<Printer> PrinterAsync(HttpClient owner)
    {
        var created = await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" });
        created.EnsureSuccessStatusCode();
        var station = await created.Content.ReadFromJsonAsync<JsonElement>();
        var stationId = station.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = station.GetProperty("enrollmentToken").GetString(), agentVersion = "0.4.0",
        });
        enrolled.EnsureSuccessStatusCode();
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stationCredential").GetString()!;
        (await Agent(HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "0.4.0",
            devices = new[]
            {
                new
                {
                    deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                    adapterKind = "fake",
                    capabilities = new { formats = new[] { "10x15", "20x15", "2x6x2" }, color = true },
                    observedState = "ready",
                },
            },
        })).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deviceId = await db.PrinterDevices.Where(d => d.PrintStationId == stationId).Select(d => d.Id).SingleAsync();
        return new Printer(stationId, deviceId, credential);
    }

    private Task<HttpResponseMessage> Agent(HttpMethod method, string url, string credential, object json)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(json) };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    /// <summary>The job the agent is handed next, or null when none may be.</summary>
    private async Task<Guid?> ClaimAsync(Printer printer)
    {
        var response = await Agent(HttpMethod.Post, "/api/print-agent/jobs/claim", printer.Credential,
            new { adapterKind = "fake" });
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
    }

    private static Task<HttpResponseMessage> SetPaper(HttpClient user, Printer printer, string paper) =>
        user.PutAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/paper",
            new { paperSize = paper });

    private static Task<HttpResponseMessage> TestPrint(HttpClient user, Printer printer) =>
        user.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/test-jobs",
            new { printerDeviceId = printer.DeviceId });

    private static Task<HttpResponseMessage> Share(HttpClient owner, Printer printer, string email, int? maxSheets = null) =>
        owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email, maxSheets });

    private static async Task<JsonElement> OwnerDevice(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/print/stations"))[0].GetProperty("devices")[0];

    private static async Task<JsonElement> OwnerStation(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/print/stations"))[0];

    private static async Task<List<JsonElement>> SharedWith(HttpClient user) =>
        (await user.GetFromJsonAsync<JsonElement>("/api/print/shared-printers")).EnumerateArray().ToList();

    // --- The paper decides what the printer is given ----------------------------

    [Fact]
    public async Task A_Sheet_Waits_For_Its_Paper_And_Prints_When_It_Is_Loaded()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);

        // A 10x15 test page is queued while 10x15 is in…
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Owner, printer)).StatusCode);
        // …and then someone loads 20x15. The sheet must not reach a printer that
        // would waste it or stop on it: it waits.
        (await SetPaper(people.Owner, printer, "20x15")).EnsureSuccessStatusCode();
        Assert.Null(await ClaimAsync(printer));
        var queued = (await OwnerStation(people.Owner)).GetProperty("queue")[0];
        Assert.Equal("10x15", queued.GetProperty("waitingForPaper").GetString());

        // The paper comes back: the sheet goes out, as it was.
        (await SetPaper(people.Owner, printer, "10x15")).EnsureSuccessStatusCode();
        Assert.NotNull(await ClaimAsync(printer));
    }

    [Theory]
    [InlineData("2x6x2", "10x15", true)]
    [InlineData("2x6x2", "20x15", false)]
    [InlineData("20x15", "20x15", true)]
    [InlineData("20x15", "10x15", false)]
    [InlineData("13x18", "10x15", false)]
    public async Task Each_Format_Waits_For_The_Paper_It_Is_A_Sheet_Of(string format, string loaded, bool claimed)
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PrinterDevices.SingleAsync(d => d.Id == printer.DeviceId)).LoadedPaperSize = loaded;
            db.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(), OwnerUserId = people.OwnerId, PrintStationId = printer.StationId,
                PrinterDeviceId = printer.DeviceId, Kind = PrintJobKinds.Diagnostic, Format = format,
                State = PrintJobStates.Ready, RenderSpecificationJson = "{}", ArtifactStorageKey = "artifact",
                ArtifactContentType = "image/png", ArtifactByteLength = 1, CreatedAt = DateTime.UtcNow,
                RenderedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        // The twin strip is a 10x15 sheet the printer cuts; every other
        // format is a sheet of its own paper.
        Assert.Equal(claimed, await ClaimAsync(printer) is not null);
    }

    // --- Lending a printer --------------------------------------------------------

    [Fact]
    public async Task An_Owner_Lends_A_Printer_By_Address_And_Ends_It()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);

        var created = await Share(people.Owner, printer, " Mario@Example.com ", maxSheets: 50);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var share = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Mario", share.GetProperty("granteeName").GetString());
        Assert.Equal(50, share.GetProperty("maxSheets").GetInt32());

        // The owner sees whom it is lent to; Mario sees what he may print on,
        // and whose it is; nobody else sees anything.
        var lent = (await OwnerDevice(people.Owner)).GetProperty("shares")[0];
        Assert.Equal("mario@example.com", lent.GetProperty("granteeEmail").GetString());
        var shared = Assert.Single(await SharedWith(people.Mario));
        Assert.Equal("Stefano", shared.GetProperty("ownerName").GetString());
        Assert.Equal("DNP DS-RX1HS", shared.GetProperty("displayName").GetString());
        Assert.Equal("10x15", shared.GetProperty("loadedPaperSize").GetString());
        Assert.Equal(["10x15", "20x15"], shared.GetProperty("papers").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(await SharedWith(people.Stranger));
        // A loan is not a station: Mario still has none of his own.
        Assert.Equal(0, (await people.Mario.GetFromJsonAsync<JsonElement>("/api/print/stations")).GetArrayLength());

        // Refusals the page can speak.
        async Task<string> Refused(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        Assert.Equal("recipient_not_found", await Refused(await Share(people.Owner, printer, "nobody@example.com")));
        Assert.Equal("recipient_is_owner", await Refused(await Share(people.Owner, printer, "stefano@example.com")));
        Assert.Equal("invalid_ceiling", await Refused(await Share(people.Owner, printer, "anna@example.com", 0)));
        var twice = await Share(people.Owner, printer, "mario@example.com");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("already_shared", await Refused(twice));

        // Ended: from now on Mario sees nothing, and it is recorded.
        var shareId = share.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await people.Owner.DeleteAsync($"/api/print/shares/{shareId}")).StatusCode);
        Assert.Empty(await SharedWith(people.Mario));
        Assert.Equal(0, (await OwnerDevice(people.Owner)).GetProperty("shares").GetArrayLength());

        // Lent again: a new loan with its own ceiling, starting from nothing.
        var again = await (await Share(people.Owner, printer, "mario@example.com", 10))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("usedSheets").GetInt32());
        Assert.NotEqual(shareId, again.GetProperty("id").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AuditLogs.Where(a => a.UserId == people.OwnerId).Select(a => a.Action).ToListAsync();
        Assert.Equal(2, actions.Count(a => a == AuditActions.PrinterShareCreate));
        Assert.Single(actions, a => a == AuditActions.PrinterShareRevoke);
    }

    [Fact]
    public async Task The_Loan_And_The_Machine_Stay_The_Owners()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var share = await (await Share(people.Owner, printer, "mario@example.com"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var shareId = share.GetProperty("id").GetGuid();

        // Mario may not lend it on, change or end the loan, or touch the machine.
        Assert.Equal(HttpStatusCode.NotFound, (await Share(people.Mario, printer, "anna@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await people.Mario.PutAsJsonAsync($"/api/print/shares/{shareId}", new { maxSheets = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await people.Mario.DeleteAsync($"/api/print/shares/{shareId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await people.Mario.PutAsJsonAsync(
            $"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/calibration",
            new { brightness = 1.1, contrast = 1, gamma = 1, saturation = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await people.Mario.PutAsJsonAsync(
            $"/api/print/stations/{printer.StationId}/desired-state", new { desiredState = "paused" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await people.Mario.DeleteAsync($"/api/print/stations/{printer.StationId}")).StatusCode);

        // Somebody it is not lent to may not even set the paper or print.
        Assert.Equal(HttpStatusCode.NotFound, (await SetPaper(people.Stranger, printer, "20x15")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TestPrint(people.Stranger, printer)).StatusCode);

        // The owner changes the ceiling — never under what is already spent.
        var raised = await people.Owner.PutAsJsonAsync($"/api/print/shares/{shareId}", new { maxSheets = 20 });
        Assert.Equal(20, (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maxSheets").GetInt32());
    }

    [Fact]
    public async Task The_Person_It_Is_Lent_To_Sets_The_Paper_And_Prints_A_Test_Page_On_Their_Loan()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var share = await (await Share(people.Owner, printer, "mario@example.com", maxSheets: 1))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Mario bought 20x15: he says so, and the owner sees who changed it.
        (await SetPaper(people.Mario, printer, "20x15")).EnsureSuccessStatusCode();
        var device = await OwnerDevice(people.Owner);
        Assert.Equal("20x15", device.GetProperty("loadedPaperSize").GetString());
        Assert.Equal("Mario", device.GetProperty("loadedPaperChangedBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, device.GetProperty("loadedPaperChangedAt").ValueKind);
        Assert.Equal("Mario", Assert.Single(await SharedWith(people.Mario)).GetProperty("loadedPaperChangedBy").GetString());

        // A test page of the new paper, on his loan: one sheet of one.
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Mario, printer)).StatusCode);
        var spent = await TestPrint(people.Mario, printer);
        Assert.Equal(HttpStatusCode.Conflict, spent.StatusCode);
        Assert.Equal("share_exhausted",
            (await spent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.PrinterDeviceId == printer.DeviceId);
        // It is Mario's sheet, on the paper now in the printer.
        Assert.Equal(people.MarioId, job.OwnerUserId);
        Assert.Equal("20x15", job.Format);
        Assert.Equal(1, (await db.PrinterShares.AsNoTracking().SingleAsync(s => s.Id == share.GetProperty("id").GetGuid())).UsedSheets);
        var paperSet = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.PrinterPaperSet).Select(a => a.UserId).ToListAsync();
        Assert.Contains(people.MarioId, paperSet);
    }

    [Fact]
    public async Task The_Owner_Clears_A_Lent_Printers_Queue_And_A_Loan_Ended_Stops_Its_Retries()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var share = await (await Share(people.Owner, printer, "mario@example.com"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Mario, printer)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Owner, printer)).StatusCode);

        // The queue says whose each sheet is — the owner's own carry no name.
        var queue = (await OwnerStation(people.Owner)).GetProperty("queue").EnumerateArray().ToList();
        Assert.Equal(2, queue.Count);
        Assert.Equal("Mario", queue[0].GetProperty("ownerName").GetString());
        Assert.Equal(JsonValueKind.Null, queue[1].GetProperty("ownerName").ValueKind);

        // Mario may not cancel the owner's sheet; the owner may cancel Mario's.
        var marios = queue[0].GetProperty("id").GetGuid();
        var owners = queue[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await people.Mario.PostAsync($"/api/print/jobs/{owners}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await people.Owner.PostAsync($"/api/print/jobs/{marios}/cancel", null)).StatusCode);

        // A sheet of Mario's that failed: while he may print he may send it
        // again; once the loan is over, only the owner can.
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Mario, printer)).StatusCode);
        Guid failed;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.PrintJobs.Where(j => j.OwnerUserId == people.MarioId && j.State == PrintJobStates.Ready)
                .SingleAsync();
            job.State = PrintJobStates.Failed;
            job.FailureCode = "printer_error";
            await db.SaveChangesAsync();
            failed = job.Id;
        }
        await people.Owner.DeleteAsync($"/api/print/shares/{share.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.Conflict, (await people.Mario.PostAsync($"/api/print/jobs/{failed}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await people.Owner.PostAsync($"/api/print/jobs/{failed}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task What_A_Loan_Has_Printed_Stays_In_The_Owners_Summary_Across_Loans()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var first = await (await Share(people.Owner, printer, "mario@example.com"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Mario, printer)).StatusCode);
        await people.Owner.DeleteAsync($"/api/print/shares/{first.GetProperty("id").GetGuid()}");
        await Share(people.Owner, printer, "mario@example.com");
        (await SetPaper(people.Mario, printer, "20x15")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Mario, printer)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await TestPrint(people.Owner, printer)).StatusCode);

        // Per person, not per party: the owner first, then whoever it was lent to.
        var usage = (await OwnerDevice(people.Owner)).GetProperty("usage").EnumerateArray().ToList();
        Assert.Equal(2, usage.Count);
        Assert.True(usage[0].GetProperty("isYou").GetBoolean());
        var mario = usage[1];
        Assert.Equal("Mario", mario.GetProperty("name").GetString());
        Assert.Equal(2, mario.GetProperty("sheets").GetInt32());
        Assert.Equal(2, mario.GetProperty("tests").GetInt32());
        Assert.Equal(1, mario.GetProperty("byPaper").GetProperty("10x15").GetInt32());
        Assert.Equal(1, mario.GetProperty("byPaper").GetProperty("20x15").GetInt32());
        // The live loan's own ceiling counts only what it took.
        Assert.Equal(1, (await OwnerDevice(people.Owner)).GetProperty("shares")[0].GetProperty("usedSheets").GetInt32());

        // Finished sheets stay in the summary; they leave the queue. One of
        // Mario's came out, the owner's failed.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var marios = await db.PrintJobs.Where(j => j.OwnerUserId == people.MarioId)
                .OrderBy(j => j.CreatedAt).ToListAsync();
            marios[0].State = PrintJobStates.Completed;
            var owners = await db.PrintJobs.SingleAsync(j => j.OwnerUserId != people.MarioId);
            owners.State = PrintJobStates.Failed;
            owners.FailureCode = "printer_error";
            await db.SaveChangesAsync();
        }
        var station = await OwnerStation(people.Owner);
        Assert.Equal(1, station.GetProperty("queueCount").GetInt32());
        Assert.Single(station.GetProperty("queue").EnumerateArray());
        Assert.Equal("printer_error", station.GetProperty("lastError").GetString());
        var after = station.GetProperty("devices")[0].GetProperty("usage").EnumerateArray().ToList();
        Assert.Equal(1, after[0].GetProperty("sheets").GetInt32());
        Assert.Equal(0, after[0].GetProperty("completed").GetInt32());
        Assert.Equal(2, after[1].GetProperty("sheets").GetInt32());
        Assert.Equal(1, after[1].GetProperty("completed").GetInt32());
    }

    [Fact]
    public async Task A_Loan_Ends_With_Its_Owners_Account_Or_Station()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        await Share(people.Owner, printer, "mario@example.com");
        Assert.Single(await SharedWith(people.Mario));

        // The station revoked by its owner lends nothing.
        await people.Owner.DeleteAsync($"/api/print/stations/{printer.StationId}");
        Assert.Empty(await SharedWith(people.Mario));
        Assert.Equal(HttpStatusCode.NotFound, (await SetPaper(people.Mario, printer, "20x15")).StatusCode);
    }

    // --- A loan's standing is decided where the sheet is taken ------------------

    private async Task<(Guid ShareId, PrinterUse Use)> LentUseAsync(People people, Printer printer, int? maxSheets = 5)
    {
        var share = await (await Share(people.Owner, printer, "mario@example.com", maxSheets))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var scope = _factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IPrinterAccess>();
        var use = await access.ForUserAsync(people.MarioId, printer.StationId, printer.DeviceId, CancellationToken.None);
        Assert.NotNull(use);
        return (share.GetProperty("id").GetGuid(), use!);
    }

    private async Task<PrinterSheetResult> TakeAsync(Guid shareId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPrinterAccess>()
            .TryTakeSheetAsync(shareId, CancellationToken.None);
    }

    private async Task<PrinterShare> ShareRowAsync(Guid shareId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterShares.AsNoTracking().SingleAsync(s => s.Id == shareId);
    }

    [Fact]
    public async Task A_Station_Revoked_After_The_Check_Takes_No_Sheet_And_Ends_Its_Loans()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var (shareId, use) = await LentUseAsync(people, printer);
        Assert.Equal(shareId, use.ShareId);

        // Between the guest's studio resolving the printer and the sheet being
        // taken, the owner revokes the station.
        Assert.Equal(HttpStatusCode.NoContent,
            (await people.Owner.DeleteAsync($"/api/print/stations/{printer.StationId}")).StatusCode);
        Assert.Equal(PrinterSheetResult.Revoked, await TakeAsync(shareId));

        // The loan itself is over, in the same transaction, and recorded as such.
        var row = await ShareRowAsync(shareId);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(people.OwnerId, row.RevokedByUserId);
        Assert.Equal(0, row.UsedSheets);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var revoke = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.PrinterShareRevoke && a.UserId == people.OwnerId);
        Assert.Contains("station_revoked", revoke.MetadataJson);
        Assert.Contains(shareId.ToString(), revoke.MetadataJson);
    }

    [Fact]
    public async Task The_Sheet_Is_Refused_By_The_Station_Itself_Even_With_The_Share_Row_Untouched()
    {
        // The second guard: the statement that takes the sheet reads the
        // station too, so a station revoked by any path stops the sheet.
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var (shareId, _) = await LentUseAsync(people, printer);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PrintStations.SingleAsync(s => s.Id == printer.StationId)).RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal(PrinterSheetResult.Revoked, await TakeAsync(shareId));
        Assert.Equal(0, (await ShareRowAsync(shareId)).UsedSheets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Disabled_Account_After_The_Check_Takes_No_Sheet(bool lender)
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var (shareId, _) = await LentUseAsync(people, printer);
        await _factory.DisableUserAsync(lender ? people.OwnerId : people.MarioId);

        // Refused as the loan being over, not as a spent ceiling: nobody can
        // fix this by raising the ceiling.
        Assert.Equal(PrinterSheetResult.Revoked, await TakeAsync(shareId));
        Assert.Equal(0, (await ShareRowAsync(shareId)).UsedSheets);
    }

    [Fact]
    public async Task A_Spent_Ceiling_On_A_Live_Loan_Is_Still_Exhausted()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var (shareId, _) = await LentUseAsync(people, printer, maxSheets: 1);
        Assert.Equal(PrinterSheetResult.Taken, await TakeAsync(shareId));
        Assert.Equal(PrinterSheetResult.Exhausted, await TakeAsync(shareId));
        Assert.Equal(1, (await ShareRowAsync(shareId)).UsedSheets);
    }

    // --- A ceiling is changed only when the request says to ---------------------

    [Fact]
    public async Task A_Ceiling_Change_Must_Say_What_The_Ceiling_Is()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var (shareId, _) = await LentUseAsync(people, printer, maxSheets: 20);
        var url = $"/api/print/shares/{shareId}";

        // No body, and a body that leaves the ceiling out: refused, the ceiling
        // unchanged — neither is a request to remove it.
        using (var empty = new HttpRequestMessage(HttpMethod.Put, url))
        {
            empty.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await people.Owner.SendAsync(empty)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await people.Owner.PutAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(20, (await ShareRowAsync(shareId)).MaxSheets);

        // Saying null, on purpose, removes it.
        Assert.Equal(HttpStatusCode.OK,
            (await people.Owner.PutAsJsonAsync(url, new { maxSheets = (int?)null })).StatusCode);
        Assert.Null((await ShareRowAsync(shareId)).MaxSheets);
        Assert.Equal(HttpStatusCode.OK, (await people.Owner.PutAsJsonAsync(url, new { maxSheets = 30 })).StatusCode);
        Assert.Equal(30, (await ShareRowAsync(shareId)).MaxSheets);
    }
}

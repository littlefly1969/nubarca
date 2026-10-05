using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// An owner prints one of their own photographs directly — through the real
/// endpoint, a real enrolled station, the real renderer and the agent's own
/// download, so what is proven is what reaches the printer.
/// </summary>
public sealed class OwnerPhotoPrintTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public OwnerPhotoPrintTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private sealed record People(HttpClient Owner, Guid OwnerId, HttpClient Mario, Guid MarioId, HttpClient Stranger, Guid StrangerId);

    private async Task<People> PeopleAsync()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var (marioId, mario) = await _factory.CreateAuthenticatedClientAsync("mario@example.com");
        var (strangerId, stranger) = await _factory.CreateAuthenticatedClientAsync("anna@example.com");
        return new People(owner, ownerId, mario, marioId, stranger, strangerId);
    }

    private async Task<Printer> PrinterAsync(HttpClient owner, int? remainingPrints = 187)
    {
        var created = await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" });
        created.EnsureSuccessStatusCode();
        var station = await created.Content.ReadFromJsonAsync<JsonElement>();
        var stationId = station.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = station.GetProperty("enrollmentToken").GetString(), agentVersion = "0.5.0",
        });
        enrolled.EnsureSuccessStatusCode();
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        (await Heartbeat(credential, remainingPrints)).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new Printer(stationId,
            await db.PrinterDevices.Where(d => d.PrintStationId == stationId).Select(d => d.Id).SingleAsync(), credential);
    }

    private Task<HttpResponseMessage> Heartbeat(string credential, int? remainingPrints, string state = "ready") =>
        Agent(HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "0.5.0",
            devices = new[]
            {
                new
                {
                    deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                    adapterKind = "cups",
                    capabilities = new { formats = new[] { "10x15", "13x18", "20x15" }, color = true },
                    observedState = state,
                    mediaRemaining = new { available = remainingPrints is not null, remainingPrints, ageSeconds = 0 },
                },
            },
        });

    private Task<HttpResponseMessage> Agent(HttpMethod method, string url, string credential, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null) request.Content = JsonContent.Create(json);
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    private static byte[] Jpeg(int width, int height)
    {
        // Each photograph its own bytes: identical bytes are ONE blob, and a
        // test setting one photo's metadata would rewrite another's.
        using var image = new Image<Rgb24>(width, height, new Rgb24(200, 30, 30));
        image[0, 0] = new Rgb24((byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0);
        image[1, 0] = new Rgb24((byte)Random.Shared.Next(256), 0, (byte)Random.Shared.Next(256));
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        return stream.ToArray();
    }

    /// <summary>A photograph in the owner's library with the metadata a real one carries.</summary>
    private async Task<Guid> PhotoAsync(HttpClient client, int width = 300, int height = 200,
        DateTime? dateTaken = null, string category = MediaCategories.Image)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Jpeg(width, height));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", $"p{Guid.NewGuid():N}.jpg");
        var uploaded = await client.PostAsync("/api/files", form);
        uploaded.EnsureSuccessStatusCode();
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == id);
        file.Width = width;
        file.Height = height;
        var meta = await db.BlobMetadata.SingleOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId);
        if (meta is null)
        {
            meta = new BlobMetadata { Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId };
            db.BlobMetadata.Add(meta);
        }
        meta.MediaCategory = category;
        meta.DetectedContentType = category == MediaCategories.Image ? "image/jpeg" : "video/mp4";
        meta.Width = width;
        meta.Height = height;
        meta.DateTaken = dateTaken;
        await db.SaveChangesAsync();
        return id;
    }

    private static object Body(Guid fileId, Printer printer, string paper = "10x15", string orientation = "landscape",
        double zoom = 1, double cx = 0.5, double cy = 0.5, bool includeDate = false, string locale = "it",
        string timeZone = "Europe/Rome") => new
    {
        fileItemId = fileId, printStationId = printer.StationId, printerDeviceId = printer.DeviceId,
        expectedPaperSize = paper, orientation, placement = new { centerX = cx, centerY = cy, zoom },
        includeDate, dateLocale = locale, timeZone,
    };

    private static async Task<HttpResponseMessage> Submit(HttpClient client, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print/photo-jobs") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The sheet the agent would print: claimed and downloaded as the agent does.</summary>
    private async Task<Image<Rgb24>> PrintedSheetAsync(Printer printer)
    {
        var claim = await Agent(HttpMethod.Post, "/api/print-agent/jobs/claim", printer.Credential, new { adapterKind = "cups" });
        claim.EnsureSuccessStatusCode();
        var job = await claim.Content.ReadFromJsonAsync<JsonElement>();
        var download = new HttpRequestMessage(HttpMethod.Get, job.GetProperty("artifactUrl").GetString());
        download.Headers.Add("X-NubArca-Print-Credential", printer.Credential);
        download.Headers.Add("X-NubArca-Print-Claim", job.GetProperty("claimToken").GetString());
        var bytes = await (await _factory.CreateClient().SendAsync(download)).Content.ReadAsByteArrayAsync();
        return Image.Load<Rgb24>(bytes);
    }

    // --- printing ----------------------------------------------------------

    [Fact]
    public async Task An_Owner_Prints_Their_Own_Photograph_And_Gets_One_Job()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Owner);

        var response = await Submit(people.Owner, Body(photo, printer));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = accepted.GetProperty("jobId").GetGuid();
        Assert.Equal(jobId.ToString("N")[..8], accepted.GetProperty("shortCode").GetString());
        Assert.Equal(0, accepted.GetProperty("queueAhead").GetInt32());
        // The printer's own count, informative, never decremented here.
        Assert.Equal(187, accepted.GetProperty("mediaRemainingPrints").GetInt32());

        var job = await Db(db => db.PrintJobs.SingleAsync(j => j.Id == jobId));
        Assert.Equal(PrintJobKinds.OwnerPhoto, job.Kind);
        Assert.Equal("10x15", job.Format);
        Assert.Equal(photo, job.FileItemId);
        var spec = JsonDocument.Parse(job.RenderSpecificationJson).RootElement;
        Assert.Equal("owner-photo", spec.GetProperty("type").GetString());
        Assert.Equal("white", spec.GetProperty("background").GetString());
        Assert.Equal("none", spec.GetProperty("dateSource").GetString());
        var source = await Db(db => db.PrintJobSources.SingleAsync(s => s.PrintJobId == jobId));
        Assert.Equal(1, source.PlacementZoom);
        // No party, no party budget, nothing guest-facing touched.
        Assert.Equal(0, await Db(db => db.PartyPrintRequests.CountAsync()));
        Assert.Equal(0, await Db(db => db.PartyPrintProfiles.CountAsync()));
    }

    [Theory]
    [InlineData("10x15", "landscape", 1800, 1200)]
    [InlineData("10x15", "portrait", 1200, 1800)]
    [InlineData("13x18", "portrait", 1500, 2100)]
    [InlineData("20x15", "landscape", 2400, 1800)]
    public async Task The_Sheet_Is_The_Loaded_Paper_Turned_As_Asked(string paper, string orientation, int width, int height)
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PutAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/paper",
            new { paperSize = paper })).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Owner);

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(people.Owner, Body(photo, printer, paper, orientation))).StatusCode);
        using var sheet = await PrintedSheetAsync(printer);
        Assert.Equal((width, height), (sheet.Width, sheet.Height));
    }

    [Fact]
    public async Task Zoomed_Out_The_Photograph_Sits_On_White_And_Fill_Covers_The_Sheet()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        // A landscape photograph on a portrait sheet, whole: white above and below.
        var photo = await PhotoAsync(people.Owner, 300, 200);
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, 1200.0 / 1800);
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(people.Owner, Body(photo, printer, orientation: "portrait", zoom: contain))).StatusCode);
        using (var sheet = await PrintedSheetAsync(printer))
        {
            var top = sheet[600, 20];
            Assert.True(top.R > 245 && top.G > 245 && top.B > 245, $"band is white, not {top}");
            var middle = sheet[600, 900];
            Assert.True(middle.R > 150 && middle.G < 90, $"photograph in the middle, not {middle}");
        }

        // Filled, the same photograph covers the whole sheet.
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(people.Owner, Body(photo, printer, orientation: "portrait"))).StatusCode);
        using var filled = await PrintedSheetAsync(printer);
        var corner = filled[10, 10];
        Assert.True(corner.R > 150 && corner.G < 90, $"filled to the edge, not {corner}");
    }

    [Fact]
    public async Task A_Lent_Printer_Prints_And_Its_Ceiling_Is_The_Loans_Not_The_Medias()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 1 })).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Mario);

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(people.Mario, Body(photo, printer))).StatusCode);
        var spent = await Submit(people.Mario, Body(photo, printer));
        Assert.Equal(HttpStatusCode.Conflict, spent.StatusCode);
        Assert.Equal("share_exhausted", await Error(spent));
        // The physical count is untouched by the loan, and the loan by it.
        Assert.Equal(187, await Db(db => db.PrinterDevices.Where(d => d.Id == printer.DeviceId)
            .Select(d => d.MediaRemainingPrints).SingleAsync()));
        Assert.Equal(1, await Db(db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
    }

    /// <summary>
    /// Reshapes an uploaded file into one of the kinds the Library meets:
    /// detected, legacy (no metadata row at all), unrecognised bytes, a legacy
    /// non-image, a video.
    /// </summary>
    private async Task ShapeAsync(Guid fileId, string shape, bool keepDimensions = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == fileId);
        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        switch (shape)
        {
            case "detected":
                break;
            case "legacy":
                db.BlobMetadata.Remove(meta);
                file.MimeType = "image/jpeg";
                break;
            case "unrecognised":
                meta.DetectedContentType = null;
                file.MimeType = "image/jpeg";
                break;
            case "legacy-pdf":
                db.BlobMetadata.Remove(meta);
                file.MimeType = "application/pdf";
                break;
            case "video":
                meta.MediaCategory = MediaCategories.Video;
                meta.DetectedContentType = "video/mp4";
                break;
        }
        if (!keepDimensions)
        {
            file.Width = null;
            file.Height = null;
        }
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("detected")]
    [InlineData("legacy")]
    [InlineData("unrecognised")]
    [InlineData("legacy-pdf")]
    [InlineData("video")]
    public async Task The_Print_Takes_Exactly_What_The_Library_Shows_As_A_Photograph(string shape)
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var file = await PhotoAsync(people.Owner);
        await ShapeAsync(file, shape);

        var listed = (await people.Owner.GetFromJsonAsync<JsonElement>("/api/media?kind=all"))
            .GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetGuid() == file)
            .Select(i => i.GetProperty("kind").GetString())
            .SingleOrDefault();
        var printed = await Submit(people.Owner, Body(file, printer));

        // One rule: the dock offers Print on a Library photograph, and the
        // server prints exactly those.
        if (listed == "image")
        {
            Assert.Equal(HttpStatusCode.Accepted, printed.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, printed.StatusCode);
            Assert.Equal("not_image", await Error(printed));
        }
        Assert.Equal(shape is "detected" or "legacy", listed == "image");
    }

    [Fact]
    public async Task A_Legacy_Photograph_With_No_Stored_Shape_Is_Framed_By_Its_Own_Pixels()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 5 })).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Mario, 300, 200);
        await ShapeAsync(photo, "legacy", keepDimensions: false);
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, 1800.0 / 1200);
        var standing = PhotoPlacementGeometry.ContainZoom(1.5, 1200.0 / 1800);

        // The whole photograph on a standing sheet: held to the decoded 3:2.
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(people.Mario, Body(photo, printer, orientation: "portrait", zoom: standing))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(people.Mario, Body(photo, printer, zoom: contain))).StatusCode);
        // Further out than the whole photograph: refused, and the loan's sheet comes back.
        var refused = await Submit(people.Mario, Body(photo, printer, orientation: "portrait", zoom: standing * 0.9));
        Assert.Equal("invalid_placement", await Error(refused));
        Assert.Equal(2, await Db(db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
        Assert.Equal(2, await Db(db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task A_Photograph_That_Is_Not_Yours_Missing_Or_Trashed_Is_One_Answer()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var strangers = await PhotoAsync(people.Stranger);
        var trashed = await PhotoAsync(people.Owner);
        await Db(async db =>
        {
            (await db.FileItems.SingleAsync(f => f.Id == trashed)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        foreach (var id in new[] { strangers, trashed, Guid.NewGuid() })
        {
            var refused = await Submit(people.Owner, Body(id, printer));
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("not_found", await Error(refused));
        }
        var video = await PhotoAsync(people.Owner, category: MediaCategories.Video);
        var notImage = await Submit(people.Owner, Body(video, printer));
        Assert.Equal("not_image", await Error(notImage));
        Assert.Equal(0, await Db(db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task Nobody_Prints_On_A_Printer_That_Is_Not_Theirs_Revoked_Or_Gone()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Stranger);
        Assert.Equal("printer_not_found", await Error(await Submit(people.Stranger, Body(photo, printer))));

        var own = await PhotoAsync(people.Owner);
        (await people.Owner.DeleteAsync($"/api/print/stations/{printer.StationId}")).EnsureSuccessStatusCode();
        Assert.Equal("printer_not_found", await Error(await Submit(people.Owner, Body(own, printer))));
    }

    [Fact]
    public async Task An_Offline_Printer_Is_Refused_Before_Anything_Is_Spent()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 3 })).EnsureSuccessStatusCode();
        (await Heartbeat(printer.Credential, 187, state: "offline")).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Mario);
        var refused = await Submit(people.Mario, Body(photo, printer));
        Assert.Equal("printer_offline", await Error(refused));
        Assert.Equal(0, await Db(db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
    }

    [Fact]
    public async Task A_Paper_Changed_While_Composing_Prints_Nothing_And_Spends_Nothing()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 3 })).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Mario);
        // Composed for 10x15; the operator loads 20x15 meanwhile.
        (await people.Owner.PutAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/paper",
            new { paperSize = "20x15" })).EnsureSuccessStatusCode();

        var refused = await Submit(people.Mario, Body(photo, printer, "10x15"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("paper_changed", await Error(refused));
        Assert.Equal(0, await Db(db => db.PrintJobs.CountAsync()));
        Assert.Equal(0, await Db(db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
        Assert.Equal(0, await Db(db => db.OwnerPhotoPrintRequests.CountAsync()));
    }

    [Fact]
    public async Task A_Placement_Further_Out_Than_The_Whole_Photograph_Is_Refused()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Owner, 300, 200);
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, 1200.0 / 1800);
        Assert.Equal("invalid_placement",
            await Error(await Submit(people.Owner, Body(photo, printer, orientation: "portrait", zoom: contain * 0.9))));
        Assert.Equal("invalid_placement",
            await Error(await Submit(people.Owner, Body(photo, printer, zoom: 4.5))));
        Assert.Equal("invalid_orientation",
            await Error(await Submit(people.Owner, Body(photo, printer, orientation: "sideways"))));
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(people.Owner, Body(photo, printer, orientation: "portrait", zoom: contain))).StatusCode);
    }

    // --- one sheet, whatever the network does --------------------------------

    [Fact]
    public async Task The_Same_Key_Is_The_Same_Sheet_And_A_Different_Composition_Under_It_Is_Refused()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Owner);
        var other = await PrinterAsync(people.Owner);

        var first = await Submit(people.Owner, Body(photo, printer), "key-1");
        var retry = await Submit(people.Owner, Body(photo, printer), "key-1");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid());
        Assert.Equal(1, await Db(db => db.PrintJobs.CountAsync()));

        foreach (var changed in new[]
        {
            Body(photo, printer, zoom: 1.5),
            Body(photo, printer, cx: 0.2),
            Body(photo, printer, includeDate: true),
            Body(photo, other),
            Body(photo, printer, orientation: "portrait"),
        })
        {
            var refused = await Submit(people.Owner, changed, "key-1");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("idempotency_conflict", await Error(refused));
        }
        Assert.Equal(1, await Db(db => db.PrintJobs.CountAsync()));
    }

    // A double tap AT THE SAME INSTANT is proven on real PostgreSQL
    // (OwnerPhotoPrintRacePostgresTests): this host shares one SQLite
    // connection, so truly overlapping requests cannot be run here.

    // --- a failure spends nothing -----------------------------------------------

    private async Task<OwnerPhotoPrintResult> SubmitWithAsync(Guid user, object body, IPrintPhotoSourceReader? sources,
        NubArca.Api.Storage.IDerivedBlobStorage? artifacts)
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var service = new OwnerPhotoPrintService(db, new PrinterAccess(db),
            sources ?? provider.GetRequiredService<IPrintPhotoSourceReader>(),
            provider.GetRequiredService<PrintArtifactRenderer>(),
            artifacts ?? provider.GetRequiredService<NubArca.Api.Storage.IDerivedBlobStorage>(),
            TimeProvider.System, provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PrintOptions>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerPhotoPrintService>.Instance);
        var request = JsonSerializer.Deserialize<OwnerPhotoPrintSubmitRequest>(
            JsonSerializer.Serialize(body), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return await service.SubmitAsync(user, request, Guid.NewGuid().ToString("N"), default);
    }

    private sealed class GarbageSources : IPrintPhotoSourceReader
    {
        public Task<PrintPhotoSources?> OpenAsync(Guid o, IReadOnlyList<Guid> f, CancellationToken c) =>
            Task.FromResult<PrintPhotoSources?>(new PrintPhotoSources(f.Select(_ => new byte[] { 1, 2, 3, 4 }).ToList()));
    }

    private sealed class FailingStore : NubArca.Api.Storage.IDerivedBlobStorage
    {
        public Task<NubArca.Api.Storage.BlobWriteResult> WriteAsync(Stream s, CancellationToken c = default) => throw new IOException("disk");
        public Task<NubArca.Api.Storage.StagedBlobWrite> StageAsync(Stream s, CancellationToken c = default) => throw new IOException("disk");
        public Task<NubArca.Api.Storage.BlobWriteResult> PublishAsync(NubArca.Api.Storage.StagedBlobWrite s, CancellationToken c = default) => throw new IOException("disk");
        public Task<Stream> OpenReadAsync(string k, CancellationToken c = default) => throw new IOException("disk");
        public Task<bool> ExistsAsync(string k, CancellationToken c = default) => Task.FromResult(false);
        public Task DeleteAsync(string k, CancellationToken c = default) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetLastWriteTimeUtcAsync(string k, CancellationToken c = default) => Task.FromResult<DateTimeOffset?>(null);
        public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task A_Render_Or_A_Store_That_Fails_Gives_The_Loans_Sheet_Back()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        (await people.Owner.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "mario@example.com", maxSheets = 1 })).EnsureSuccessStatusCode();
        var photo = await PhotoAsync(people.Mario);

        var garbled = await SubmitWithAsync(people.MarioId, Body(photo, printer), new GarbageSources(), null);
        Assert.Equal(OwnerPhotoPrintErrors.RenderFailed, garbled.Error);
        var stored = await SubmitWithAsync(people.MarioId, Body(photo, printer), null, new FailingStore());
        Assert.Equal(OwnerPhotoPrintErrors.RenderFailed, stored.Error);

        // Nothing accepted, nothing spent: the loan's only sheet is still there…
        Assert.Equal(0, await Db(db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
        Assert.Equal(0, await Db(db => db.PrintJobs.CountAsync()));
        Assert.Equal(0, await Db(db => db.OwnerPhotoPrintRequests.CountAsync()));
        // …and prints.
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(people.Mario, Body(photo, printer))).StatusCode);
    }

    // --- the date ------------------------------------------------------------

    private static async Task<JsonElement> DateOf(HttpClient client, Guid photo, string zone = "Europe/Rome") =>
        await client.GetFromJsonAsync<JsonElement>($"/api/print/photo-jobs/date?fileItemId={photo}&timeZone={zone}");

    [Fact]
    public async Task The_Date_Is_The_Owners_Then_The_Cameras_Then_Today_And_Never_The_Upload()
    {
        var people = await PeopleAsync();
        var camera = await PhotoAsync(people.Owner, dateTaken: new DateTime(2019, 7, 1, 23, 30, 0, DateTimeKind.Utc));
        var none = await PhotoAsync(people.Owner);

        var embedded = await DateOf(people.Owner, camera);
        Assert.Equal("2019-07-01", embedded.GetProperty("date").GetString()); // the camera's wall clock, no shift
        Assert.Equal("embedded", embedded.GetProperty("source").GetString());

        var today = await DateOf(people.Owner, none);
        Assert.Equal("today", today.GetProperty("source").GetString());
        Assert.NotEqual("uploaded", today.GetProperty("source").GetString());

        await Db(async db =>
        {
            db.FileItemUserMetadata.Add(new FileItemUserMetadata
            {
                Id = Guid.NewGuid(), FileItemId = camera, CreatedAt = DateTime.UtcNow,
                DateTakenOverride = new DateTime(2020, 12, 25, 10, 0, 0, DateTimeKind.Utc),
            });
            return await db.SaveChangesAsync();
        });
        var user = await DateOf(people.Owner, camera);
        Assert.Equal("2020-12-25", user.GetProperty("date").GetString());
        Assert.Equal("user", user.GetProperty("source").GetString());

        // Someone else's photograph has no date to tell.
        Assert.Equal(HttpStatusCode.NotFound,
            (await people.Stranger.GetAsync($"/api/print/photo-jobs/date?fileItemId={camera}&timeZone=Europe/Rome")).StatusCode);
    }

    [Fact]
    public void Today_Is_The_Persons_Day_Not_The_Servers()
    {
        var late = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 2),
            OwnerPhotoPrintDates.Resolve(null, null, late, OwnerPhotoPrintDates.Zone("Europe/Rome")!).Date);
        Assert.Equal(new DateOnly(2026, 10, 1),
            OwnerPhotoPrintDates.Resolve(null, null, late, OwnerPhotoPrintDates.Zone("America/New_York")!).Date);
    }

    [Theory]
    [InlineData("it", "01/07/2019")]
    [InlineData("es", "01/07/2019")]
    [InlineData("de", "01.07.2019")]
    [InlineData("en", "07/01/2019")]
    public void The_Date_Is_Written_The_Same_Way_The_Preview_Writes_It(string locale, string expected) =>
        Assert.Equal(expected, OwnerPhotoPrintDates.Format(new DateOnly(2019, 7, 1), locale));

    [Fact]
    public async Task An_Unknown_Timezone_Is_Refused_Rather_Than_Guessed()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Owner);
        var refused = await Submit(people.Owner, Body(photo, printer, includeDate: true, timeZone: "Mars/Olympus"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_timezone", await Error(refused));
        Assert.Equal("invalid_timezone", await Error(
            await people.Owner.GetAsync($"/api/print/photo-jobs/date?fileItemId={photo}&timeZone=Mars/Olympus")));
    }

    [Fact]
    public async Task With_The_Date_On_It_Is_Printed_On_The_Photograph_And_Off_It_Leaves_No_Mark()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        // A wide photograph whole on a portrait sheet: white bands above and below.
        var photo = await PhotoAsync(people.Owner, 300, 200, new DateTime(2019, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, 1200.0 / 1800);

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(people.Owner,
            Body(photo, printer, orientation: "portrait", zoom: contain, includeDate: true))).StatusCode);
        var job = await Db(db => db.PrintJobs.OrderByDescending(j => j.CreatedAt).FirstAsync());
        var spec = JsonDocument.Parse(job.RenderSpecificationJson).RootElement;
        Assert.Equal("2019-07-01", spec.GetProperty("resolvedDate").GetString());
        Assert.Equal("embedded", spec.GetProperty("dateSource").GetString());

        using var dated = await PrintedSheetAsync(printer);
        // The visible photograph is the middle band; the date's corner sits on it.
        var visible = PhotoPlacementGeometry.Visible(
            PhotoPlacementGeometry.Place(1.5, 1200.0 / 1800, new PhotoPlacement(0.5, 0.5, contain)));
        var anchor = PrintArtifactRenderer.DateAnchor(1200, 1800, visible);
        Assert.True(anchor.Y < visible.Bottom * 1800, "the date sits on the photograph, not on the band");
        Assert.True(BrightPixels(dated, (int)anchor.X - 160, (int)anchor.Y - 50, 160, 50) > 20, "the date was drawn");
        // The band under the photograph stays clean.
        Assert.Equal(0, BrightPixelsNotWhite(dated, 0, (int)(visible.Bottom * 1800) + 5, 1200, 100));

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(people.Owner,
            Body(photo, printer, orientation: "portrait", zoom: contain))).StatusCode);
        using var plain = await PrintedSheetAsync(printer);
        Assert.Equal(0, BrightPixels(plain, (int)anchor.X - 160, (int)anchor.Y - 50, 160, 50));
    }

    /// <summary>Near-white pixels inside the (red) photograph: the date's letters.</summary>
    private static int BrightPixels(Image<Rgb24> image, int x, int y, int w, int h)
    {
        var count = 0;
        for (var py = Math.Max(0, y); py < Math.Min(image.Height, y + h); py++)
            for (var px = Math.Max(0, x); px < Math.Min(image.Width, x + w); px++)
                if (image[px, py] is { R: > 230, G: > 200, B: > 200 }) count++;
        return count;
    }

    /// <summary>Pixels in a band that are neither white nor near it — anything drawn on it.</summary>
    private static int BrightPixelsNotWhite(Image<Rgb24> image, int x, int y, int w, int h)
    {
        var count = 0;
        for (var py = Math.Max(0, y); py < Math.Min(image.Height, y + h); py++)
            for (var px = Math.Max(0, x); px < Math.Min(image.Width, x + w); px++)
                if (image[px, py] is { R: < 235 } or { G: < 235 } or { B: < 235 }) count++;
        return count;
    }
}

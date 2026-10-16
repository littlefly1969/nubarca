using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NubArca.Api.Ai.SubjectCutout;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Ai.SubjectCutout;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// "Esce dalla foto": an owner prints a photograph whose subject breaks out
/// over the frame's top edge — through the real endpoint, the real cutout
/// service (with a stand-in model that calls the middle third of the photo the
/// subject), the real composer and the agent's own download.
/// </summary>
public sealed class OwnerPopOutPrintTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;
    private readonly MiddleThirdModel _model = new();

    public OwnerPopOutPrintTests()
    {
        // A setting gives the class its own host, so the stand-in never leaks.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:SubjectCutout:QueueTimeoutSeconds"] = "0",
        });
        _factory.ConfigureExtraServices = services =>
            services.Replace(ServiceDescriptor.Singleton<ISubjectMaskModel>(_model));
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    // The popOut slot of a standing 10x15: 96,264 → 1104,1530 (1008 x 1266).
    private const int SlotTop = 264;

    [Fact]
    public async Task The_Subject_Breaks_Out_Over_The_Top_Edge_And_The_Background_Stays_Inside()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var photo = await PhotoAsync(owner);

        // Framed at the photograph's foot: its top fifth runs above the slot.
        var response = await Submit(owner, Body(photo, printer, cy: 1));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var sheet = await PrintedSheetAsync(printer);
        Assert.Equal((1200, 1800), (sheet.Width, sheet.Height));
        // Above the slot, the subject's column is the photograph…
        AssertRed(sheet[600, SlotTop - 100], "subject above the frame");
        AssertRed(sheet[600, SlotTop - 5], "subject meets the frame without a gap");
        // …beside it, only paper: the background never leaves the frame.
        AssertWhite(sheet[200, SlotTop - 100], "background stays inside");
        AssertWhite(sheet[1000, SlotTop - 100], "background stays inside");
        // Nothing above where the photograph itself ends, nor at the safe edge.
        AssertWhite(sheet[600, 10], "the photograph's own top");
        // And inside the frame, the photograph as ever.
        AssertRed(sheet[200, 900], "the photograph in its slot");

        var job = await Db(db => db.PrintJobs.SingleAsync());
        Assert.Equal(PrintJobKinds.OwnerPhoto, job.Kind);
        var spec = JsonDocument.Parse(job.RenderSpecificationJson).RootElement;
        Assert.Equal("popOut", spec.GetProperty("style").GetString());
        Assert.Equal(1, _model.Runs);
    }

    [Fact]
    public async Task A_Framing_That_Leaves_Nothing_Above_The_Frame_Breaks_Nothing_Out()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var photo = await PhotoAsync(owner);

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(owner, Body(photo, printer, cy: 0))).StatusCode);
        using var sheet = await PrintedSheetAsync(printer);
        AssertWhite(sheet[600, SlotTop - 100], "nothing above the frame");
        AssertRed(sheet[600, SlotTop + 20], "the photograph in its slot");
    }

    [Fact]
    public async Task Without_The_Cutout_Model_It_Is_Refused_And_Nothing_Is_Printed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var photo = await PhotoAsync(owner);
        _model.Available = false;

        var response = await Submit(owner, Body(photo, printer, cy: 1));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OwnerPhotoPrintErrors.SubjectUnavailable, await Error(response));
        Assert.Equal(0, await Db(db => db.PrintJobs.CountAsync()));

        // The other styles never ask for it.
        var framed = await Submit(owner, Body(photo, printer, cy: 1, style: "framed"));
        Assert.Equal(HttpStatusCode.Accepted, framed.StatusCode);
    }

    [Fact]
    public async Task A_Photograph_With_No_Subject_Is_Refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var photo = await PhotoAsync(owner);
        _model.Subject = false;

        var response = await Submit(owner, Body(photo, printer, cy: 1));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OwnerPhotoPrintErrors.NoSubject, await Error(response));
        Assert.Equal(0, await Db(db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task Only_A_Single_Photograph_And_Never_Through_A_Share()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var photo = await PhotoAsync(owner);

        var grid = await Submit(owner, new
        {
            printStationId = printer.StationId, printerDeviceId = printer.DeviceId, expectedPaperSize = "10x15",
            layout = "grid4", style = "popOut",
            photos = Enumerable.Range(0, 4).Select(_ => new { fileItemId = photo, placement = new { centerX = 0.5, centerY = 0.5, zoom = 1 } }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, grid.StatusCode);
        Assert.Equal(OwnerPhotoPrintErrors.InvalidLayout, await Error(grid));

        // A share's visitor prints the owner's photograph, but never with the
        // owner's private AI.
        using var scope = _factory.Services.CreateScope();
        var prints = scope.ServiceProvider.GetRequiredService<IOwnerPhotoPrintService>();
        var request = JsonSerializer.Deserialize<OwnerPhotoPrintSubmitRequest>(
            JsonSerializer.Serialize(Body(photo, printer, cy: 1)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var shared = await prints.SubmitAsync(ownerId, request, Guid.NewGuid().ToString("N"), default,
            new OwnerPrintOrigin(Guid.NewGuid()));
        Assert.Equal(OwnerPhotoPrintErrors.InvalidLayout, shared.Error);
        Assert.Equal(0, _model.Runs);
    }

    // --- helpers -------------------------------------------------------------

    private static void AssertRed(Rgb24 pixel, string what) =>
        Assert.True(pixel.R > 150 && pixel.G < 90 && pixel.B < 90, $"{what}: expected the photograph, got {pixel}");

    private static void AssertWhite(Rgb24 pixel, string what) =>
        Assert.True(pixel.R > 225 && pixel.G > 225 && pixel.B > 225, $"{what}: expected paper, got {pixel}");

    private static object Body(Guid fileId, Printer printer, double cy, string style = "popOut") => new
    {
        fileItemId = fileId, printStationId = printer.StationId, printerDeviceId = printer.DeviceId,
        expectedPaperSize = "10x15", orientation = "portrait", style,
        placement = new { centerX = 0.5, centerY = cy, zoom = 1 },
    };

    private static async Task<HttpResponseMessage> Submit(HttpClient client, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print/photo-jobs") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<Printer> PrinterAsync(HttpClient owner)
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
        (await Agent(HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "0.5.0",
            devices = new[]
            {
                new
                {
                    deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                    adapterKind = "cups",
                    capabilities = new { formats = new[] { "10x15", "13x18", "20x15" }, color = true },
                    observedState = "ready",
                    mediaRemaining = new { available = true, remainingPrints = 100, ageSeconds = 0 },
                },
            },
        })).EnsureSuccessStatusCode();
        var deviceId = await Db(db => db.PrinterDevices.Where(d => d.PrintStationId == stationId).Select(d => d.Id).SingleAsync());
        return new Printer(stationId, deviceId, credential);
    }

    private Task<HttpResponseMessage> Agent(HttpMethod method, string url, string credential, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null) request.Content = JsonContent.Create(json);
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }

    /// <summary>A standing red photograph in the owner's library, with the metadata a real one carries.</summary>
    private async Task<Guid> PhotoAsync(HttpClient client, int width = 400, int height = 600)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(200, 30, 30));
        image[0, 0] = new Rgb24((byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0);
        using var jpeg = new MemoryStream();
        image.Save(jpeg, new JpegEncoder());
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(jpeg.ToArray());
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
        meta.MediaCategory = MediaCategories.Image;
        meta.DetectedContentType = "image/jpeg";
        meta.Width = width;
        meta.Height = height;
        await db.SaveChangesAsync();
        return id;
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

    /// <summary>Calls the middle third of the photograph, top to bottom, its subject.</summary>
    private sealed class MiddleThirdModel : ISubjectMaskModel
    {
        public volatile bool Available = true;
        public volatile bool Subject = true;
        private int _runs;

        public int Runs => _runs;

        public string? UnavailableReason() => Available ? null : "subject-cutout-disabled";

        public float[] PredictLogits(float[] input, int size)
        {
            Interlocked.Increment(ref _runs);
            return SubjectCutoutImagingTests.Square(size, (x, _) =>
                Subject && x >= size / 3 && x < size * 2 / 3 ? 12f : -12f);
        }
    }
}

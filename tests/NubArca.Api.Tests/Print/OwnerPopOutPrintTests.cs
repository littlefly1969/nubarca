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
using NubArca.Api.Tests.Metadata;
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
    private readonly RedPointModel _points = new();

    public OwnerPopOutPrintTests()
    {
        // A setting gives the class its own host, so the stand-in never leaks.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:SubjectCutout:QueueTimeoutSeconds"] = "0",
        });
        _factory.ConfigureExtraServices = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISubjectMaskModel>(_model));
            services.Replace(ServiceDescriptor.Singleton<ISubjectPointModel>(_points));
        };
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

    // The photographs below are blue with red people; the stand-in model sees
    // the photo it is given and calls its red the subject (RedLogits). On the
    // standing 10x15 a 2:3 photograph framed at its foot is 1008 px wide from
    // x 96, so a fraction f of its width is at sheet x 96 + 1008 f.

    [Theory]
    [InlineData((ushort)1, 400)]
    [InlineData((ushort)6, 400)]
    [InlineData((ushort)8, 400)]
    // An original far larger than the medium preview the mask is made on.
    [InlineData((ushort)1, 2000)]
    [InlineData((ushort)6, 2000)]
    public async Task The_Subject_Breaks_Out_Exactly_Where_The_Upright_Photograph_Has_It(ushort orientation, int width)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        // One person standing at x 0.55..0.85 of the upright photograph.
        using var upright = People(width, (0.55, 0.85));
        var photo = await PhotoAsync(owner, upright, orientation);
        _model.ByColour = true;

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(owner, Body(photo, printer, cy: 1))).StatusCode);

        using var sheet = await PrintedSheetAsync(printer);
        // The person (sheet x 650..953) above the frame, edge to edge…
        var above = SlotTop - 100;
        AssertRed(sheet[662, above], "the person's left edge above the frame");
        AssertRed(sheet[800, above], "the person above the frame");
        AssertRed(sheet[941, above], "the person's right edge above the frame");
        // …and not a pixel of the blue beside them.
        AssertWhite(sheet[636, above], "paper left of the person");
        AssertWhite(sheet[300, above], "paper where the photograph is background");
        AssertWhite(sheet[990, above], "paper right of the person");
        // In the frame, the same photograph, the same way up.
        AssertRed(sheet[800, 900], "the person in the frame");
        AssertBlue(sheet[300, 900], "the background in the frame");
    }

    [Fact]
    public async Task Across_The_Frame_Edge_A_Display_P3_Photograph_Continues_Without_A_Seam()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        // A Display P3 person (x 0.55..0.85) with a dark stripe down their
        // middle (x 0.69..0.71): colour and line must run straight across.
        using var upright = People(400, (0.55, 0.85));
        for (var y = 0; y < 600; y++)
        for (var x = 276; x < 284; x++)
            upright[x, y] = new Rgb24(120, 20, 20);
        var p3 = PrintColourSpaceTests.Profile(PrintColourSpaceTests.DisplayP3, PrintColourSpaceTests.SrgbParametricCurve());
        var photo = await PhotoAsync(owner, upright, 1, p3);
        _model.ByColour = true;

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(owner, Body(photo, printer, cy: 1))).StatusCode);

        using var sheet = await PrintedSheetAsync(printer);
        var (justAbove, justBelow) = (SlotTop - 6, SlotTop + 6);
        foreach (var x in new[] { 680, 760, 920 })
        {
            var (a, b) = (sheet[x, justAbove], sheet[x, justBelow]);
            Assert.True(Math.Abs(a.R - b.R) <= 3 && Math.Abs(a.G - b.G) <= 3 && Math.Abs(a.B - b.B) <= 3,
                $"x {x}: {a} above the edge, {b} below it");
            // Both converted to sRGB: Display P3 (210,30,30) is a deeper red than sRGB's.
            Assert.True(a.R > 220, $"x {x}: expected the converted red, got {a}");
        }
        Assert.Equal(Darkest(sheet, justBelow, 760, 820), Darkest(sheet, justAbove, 760, 820));
    }

    [Fact]
    public async Task The_Touched_Person_Is_The_One_That_Breaks_Out()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        // Two people: x 0.1..0.35 (sheet 197..449) and 0.65..0.9 (751..1003).
        using var upright = People(400, (0.1, 0.35), (0.65, 0.9));
        var photo = await PhotoAsync(owner, upright);
        _model.ByColour = true;
        var above = SlotTop - 100;

        // Untouched, both break out.
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(owner, Body(photo, printer, cy: 1))).StatusCode);
        using (var both = await PrintedSheetAsync(printer))
        {
            AssertRed(both[320, above], "the left person");
            AssertRed(both[880, above], "the right person");
        }

        // Touched on the right one, only they do.
        var touched = await Submit(owner, Body(photo, printer, cy: 1, subject: new { x = 0.775, y = 0.5 }));
        Assert.Equal(HttpStatusCode.Accepted, touched.StatusCode);
        using (var right = await PrintedSheetAsync(printer))
        {
            AssertWhite(right[320, above], "the left person stays in the frame");
            AssertRed(right[880, above], "the touched person breaks out");
            AssertRed(right[320, 900], "the left person is still in the photograph");
        }
        Assert.Equal((0.775f, 0.5f), _points.LastPoint);
        var spec = JsonDocument.Parse((await Db(db => db.PrintJobs.OrderByDescending(j => j.CreatedAt).FirstAsync()))
            .RenderSpecificationJson).RootElement.GetProperty("subject");
        Assert.Equal(0.775, spec.GetProperty("x").GetDouble(), 3);

        // Touched on the wall between them — though the point model proposes
        // both people — nothing is lifted, and nothing is printed.
        var wall = await Submit(owner, Body(photo, printer, cy: 1, subject: new { x = 0.5, y = 0.5 }));
        Assert.Equal(HttpStatusCode.Conflict, wall.StatusCode);
        Assert.Equal(OwnerPhotoPrintErrors.NoSubject, await Error(wall));
        Assert.Equal(2, await Db(db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task A_Touched_Point_Is_Only_For_Pop_Out_On_The_Photograph_And_Part_Of_The_Composition()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        using var upright = People(400, (0.1, 0.35), (0.65, 0.9));
        var photo = await PhotoAsync(owner, upright);
        _model.ByColour = true;

        var framed = await Submit(owner, Body(photo, printer, cy: 1, style: "framed", subject: new { x = 0.2, y = 0.5 }));
        Assert.Equal(HttpStatusCode.BadRequest, framed.StatusCode);
        Assert.Equal(OwnerPhotoPrintErrors.InvalidRequest, await Error(framed));
        var off = await Submit(owner, Body(photo, printer, cy: 1, subject: new { x = 1.5, y = 0.5 }));
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);

        // The same key for another touched subject is another composition.
        var key = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Accepted,
            (await Submit(owner, Body(photo, printer, cy: 1, subject: new { x = 0.2, y = 0.5 }), key)).StatusCode);
        var other = await Submit(owner, Body(photo, printer, cy: 1, subject: new { x = 0.8, y = 0.5 }), key);
        Assert.Equal(OwnerPhotoPrintErrors.IdempotencyConflict, await Error(other));
        Assert.Equal(1, await Db(db => db.PrintJobs.CountAsync()));
    }

    // --- helpers -------------------------------------------------------------

    private static void AssertRed(Rgb24 pixel, string what) =>
        Assert.True(pixel.R > 150 && pixel.G < 90 && pixel.B < 90, $"{what}: expected the photograph, got {pixel}");

    private static void AssertBlue(Rgb24 pixel, string what) =>
        Assert.True(pixel.B > 150 && pixel.R < 90, $"{what}: expected the background, got {pixel}");

    // The x of the darkest pixel of a row between `from` and `to`.
    private static int Darkest(Image<Rgb24> sheet, int y, int from, int to) =>
        Enumerable.Range(from, to - from).MinBy(x => sheet[x, y].R + sheet[x, y].G + sheet[x, y].B);

    /// <summary>A standing 2:3 photograph: blue, with a red person standing full height in each span.</summary>
    private static Image<Rgb24> People(int width, params (double From, double To)[] people)
    {
        var height = width * 3 / 2;
        var image = new Image<Rgb24>(width, height, new Rgb24(30, 60, 200));
        foreach (var (from, to) in people)
        {
            for (var y = 0; y < height; y++)
            for (var x = (int)(from * width); x < (int)(to * width); x++)
                image[x, y] = new Rgb24(210, 30, 30);
        }
        return image;
    }

    private static void AssertWhite(Rgb24 pixel, string what) =>
        Assert.True(pixel.R > 225 && pixel.G > 225 && pixel.B > 225, $"{what}: expected paper, got {pixel}");

    private static object Body(Guid fileId, Printer printer, double cy, string style = "popOut", object? subject = null) => new
    {
        fileItemId = fileId, printStationId = printer.StationId, printerDeviceId = printer.DeviceId,
        expectedPaperSize = "10x15", orientation = "portrait", style,
        placement = new { centerX = 0.5, centerY = cy, zoom = 1 },
        subject,
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
        return await PhotoAsync(client, image);
    }

    /// <summary>
    /// <paramref name="upright"/> in the owner's library, stored turned by EXIF
    /// <paramref name="orientation"/> as a phone stores it, and with the
    /// metadata the import would have read from it.
    /// </summary>
    private async Task<Guid> PhotoAsync(HttpClient client, Image<Rgb24> upright, ushort orientation = 1, byte[]? icc = null)
    {
        var bytes = ImageFixtures.OrientedJpeg(upright, orientation, icc);
        var turned = orientation is 6 or 8;
        var (width, height) = turned ? (upright.Height, upright.Width) : (upright.Width, upright.Height);
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
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
        meta.Orientation = orientation;
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
        // Whatever is red in the photo it is given, instead (RedLogits).
        public volatile bool ByColour;
        private int _runs;

        public int Runs => _runs;

        public string? UnavailableReason() => Available ? null : "subject-cutout-disabled";

        public float[] PredictLogits(float[] input, int size)
        {
            Interlocked.Increment(ref _runs);
            if (ByColour) return SubjectCutoutImagingTests.RedLogits(input, size);
            return SubjectCutoutImagingTests.Square(size, (x, _) =>
                Subject && x >= size / 3 && x < size * 2 / 3 ? 12f : -12f);
        }
    }

    /// <summary>
    /// Around the touched point it proposes what SAM does: a small patch under
    /// the finger, and every red person in the photograph together — even for
    /// a touch on the wall between them.
    /// </summary>
    private sealed class RedPointModel : ISubjectPointModel
    {
        public (float X, float Y) LastPoint;

        public string? UnavailableReason() => null;

        public float[] PredictCandidates(float[] input, float x, float y, out int count)
        {
            LastPoint = (x, y);
            const int edge = SubjectPointModelShape.CandidateEdge;
            const int scale = SubjectPointModelShape.InputEdge / edge;
            var red = SubjectCutoutImagingTests.RedLogits(input, SubjectPointModelShape.InputEdge);
            var (px, py) = ((int)(x * edge), (int)(y * edge));
            count = 2;
            return SubjectCutoutImagingTests.Candidates(edge,
                (cx, cy) => Math.Abs(cx - px) < 4 && Math.Abs(cy - py) < 4,
                (cx, cy) => red[(cy * scale) * SubjectPointModelShape.InputEdge + cx * scale] > 0);
        }
    }
}

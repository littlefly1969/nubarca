using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// A printer's tone compensation: what it does to a sheet, where it is stored,
/// and that the test page an owner prints to judge it carries it.
/// </summary>
public sealed class PrintCalibrationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PrintCalibrationTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private static Image<Rgb24> Grey(byte level) => new(4, 4, new Rgb24(level, level, level));

    [Fact]
    public void Neutral_Changes_Nothing()
    {
        using var image = Grey(128);
        PrintCalibration.Neutral.ApplyTo(image);
        Assert.Equal(new Rgb24(128, 128, 128), image[0, 0]);
        Assert.True(PrintCalibration.Neutral.IsNeutral);
    }

    [Fact]
    public void Gamma_Lightens_The_Midtones_And_Keeps_Black_And_White()
    {
        var lighter = new PrintCalibration(1, 1, 1.3, 1);
        using var mid = Grey(128);
        using var black = Grey(0);
        using var white = Grey(255);
        lighter.ApplyTo(mid);
        lighter.ApplyTo(black);
        lighter.ApplyTo(white);
        Assert.True(mid[0, 0].R > 140, $"mid-grey became {mid[0, 0].R}");
        Assert.Equal(0, black[0, 0].R);
        Assert.Equal(255, white[0, 0].R);

        using var darker = Grey(128);
        new PrintCalibration(1, 1, 0.8, 1).ApplyTo(darker);
        Assert.True(darker[0, 0].R < 118, $"mid-grey became {darker[0, 0].R}");
    }

    [Fact]
    public void Brightness_Moves_Everything()
    {
        using var image = Grey(128);
        new PrintCalibration(1.2, 1, 1, 1).ApplyTo(image);
        Assert.True(image[0, 0].R > 140);
    }

    [Fact]
    public void Only_Gentle_Corrections_Are_Accepted()
    {
        Assert.True(new PrintCalibration(1.3, 0.7, 1.6, 0.5).IsValid);
        Assert.False(new PrintCalibration(1.31, 1, 1, 1).IsValid);
        Assert.False(new PrintCalibration(1, 1, 0.5, 1).IsValid);
        Assert.False(new PrintCalibration(1, 1, 1, double.NaN).IsValid);
    }

    [Fact]
    public async Task A_Guest_Sheet_Carries_The_Printers_Calibration()
    {
        using var source = new Image<Rgba32>(1000, 1400, new Rgba32(128, 128, 128));
        using var ms = new MemoryStream();
        await source.SaveAsJpegAsync(ms);
        var composition = new PartyPrintComposition(PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(ms.ToArray(), 0, 0, 1, 1)], "Festa", null);
        var composer = new PartyPrintComposer();

        using var neutral = Image.Load<Rgb24>(await composer.RenderAsync(composition, default));
        using var lighter = Image.Load<Rgb24>(await composer.RenderAsync(
            composition with { Calibration = new PrintCalibration(1, 1, 1.4, 1) }, default));

        var centre = new Point(neutral.Width / 2, neutral.Height / 3);
        Assert.True(lighter[centre.X, centre.Y].R > neutral[centre.X, centre.Y].R + 15,
            $"{neutral[centre.X, centre.Y].R} → {lighter[centre.X, centre.Y].R}");
    }

    [Fact]
    public async Task The_Test_Page_Has_A_Grey_Wedge_That_Shows_The_Calibration()
    {
        var renderer = new PrintArtifactRenderer();
        using var neutral = Image.Load<Rgb24>(await renderer.RenderDiagnosticAsync(
            "Box", "DS-RX1", DateTime.UnixEpoch, "10x15", "abc12345", default));
        using var lighter = Image.Load<Rgb24>(await renderer.RenderDiagnosticAsync(
            "Box", "DS-RX1", DateTime.UnixEpoch, "10x15", "abc12345", default,
            new PrintCalibration(1, 1, 1.3, 1)));

        var (x, y) = WedgeStep(5); // the 50% grey step
        Assert.Equal(128, neutral[x, y].R);
        Assert.True(lighter[x, y].R > 140);
        // The ends of the wedge are the printer's black and white, whatever the gamma.
        var (bx, by) = WedgeStep(0);
        Assert.Equal(0, lighter[bx, by].R);
    }

    private static (int X, int Y) WedgeStep(int step)
    {
        var width = PrintArtifactRenderer.StripWidth / PrintArtifactRenderer.WedgeSteps;
        return (PrintArtifactRenderer.StripLeft + (step * width) + (width / 2),
            PrintArtifactRenderer.WedgeTop + (PrintArtifactRenderer.WedgeHeight / 2));
    }

    [Fact]
    public async Task The_Owner_Sets_It_And_The_Test_Page_Prints_With_It()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (stationId, credential) = await EnrolledStationAsync(owner);
        (await Agent(HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "test",
            devices = new[] { new { deviceKey = "rx1", displayName = "DS-RX1", manufacturer = "DNP", model = "DS-RX1",
                adapterKind = "cups", capabilities = new { formats = new[] { "10x15" }, color = true }, observedState = "ready" } },
        })).EnsureSuccessStatusCode();
        var device = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!
            .Single().GetProperty("devices")[0];
        var deviceId = device.GetProperty("id").GetGuid();
        Assert.Equal(1, device.GetProperty("calibration").GetProperty("gamma").GetDouble());

        var url = $"/api/print/stations/{stationId}/devices/{deviceId}/calibration";
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(url,
            new { brightness = 1, contrast = 1, gamma = 3, saturation = 1 })).StatusCode);
        var saved = await owner.PutAsJsonAsync(url, new { brightness = 1, contrast = 1, gamma = 1.3, saturation = 1 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // A heartbeat reports the printer again and must not undo the owner's setting.
        (await Agent(HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "test",
            devices = new[] { new { deviceKey = "rx1", displayName = "DS-RX1", manufacturer = "DNP", model = "DS-RX1",
                adapterKind = "cups", capabilities = new { formats = new[] { "10x15" }, color = true }, observedState = "ready" } },
        })).EnsureSuccessStatusCode();
        var reread = (await owner.GetFromJsonAsync<JsonElement[]>("/api/print/stations"))!
            .Single().GetProperty("devices")[0];
        Assert.Equal(1.3, reread.GetProperty("calibration").GetProperty("gamma").GetDouble());

        // Somebody else's printer is not found, not merely refused.
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync(url,
            new { brightness = 1, contrast = 1, gamma = 1.3, saturation = 1 })).StatusCode);

        // The test page the owner prints to judge it is rendered with it.
        (await owner.PostAsJsonAsync($"/api/print/stations/{stationId}/test-jobs",
            new { printerDeviceId = deviceId })).EnsureSuccessStatusCode();
        var claim = await (await Agent(HttpMethod.Post, "/api/print-agent/jobs/claim", credential,
            new { adapterKind = "cups" })).Content.ReadFromJsonAsync<JsonElement>();
        var download = new HttpRequestMessage(HttpMethod.Get,
            $"/api/print-agent/jobs/{claim.GetProperty("jobId").GetGuid()}/artifact");
        download.Headers.Add("X-NubArca-Print-Credential", credential);
        download.Headers.Add("X-NubArca-Print-Claim", claim.GetProperty("claimToken").GetString()!);
        var bytes = await (await _factory.CreateClient().SendAsync(download)).Content.ReadAsByteArrayAsync();
        using var page = Image.Load<Rgb24>(bytes);
        var (x, y) = WedgeStep(5);
        Assert.True(page[x, y].R > 140, $"the 50% step printed as {page[x, y].R}");
    }

    private async Task<(Guid Id, string Credential)> EnrolledStationAsync(HttpClient owner)
    {
        var created = await (await owner.PostAsJsonAsync("/api/print/stations", new { name = "Box" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var stationId = created.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = created.GetProperty("enrollmentToken").GetString(), agentVersion = "test",
        });
        var body = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        return (stationId, body.GetProperty("stationCredential").GetString()!);
    }

    private Task<HttpResponseMessage> Agent(HttpMethod method, string url, string credential, object json)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(json) };
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return _factory.CreateClient().SendAsync(request);
    }
}

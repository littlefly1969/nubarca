using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Plates;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Integration coverage for the ALPR Onnx provider with MISSING model files: the
// worker records a safe model-missing error code and NO absolute model path
// leaks into the domain job or any API response.
public sealed class PlateAlprOnnxProviderTests : IDisposable
{
    // A recognizable fake path so a leak would be obvious in an assertion.
    private const string DetectorPath = "/opt/nonexistent/plates/detector-SECRET.onnx";
    private const string OcrPath = "/opt/nonexistent/plates/ocr-SECRET.onnx";

    private readonly SqliteWebApplicationFactory _factory;

    public PlateAlprOnnxProviderTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Plates:Alpr:Provider"] = "Onnx",
            ["Plates:Alpr:DetectorModelPath"] = DetectorPath,
            ["Plates:Alpr:OcrModelPath"] = OcrPath,
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task Onnx_Missing_Model_Fails_Safely_Without_Leaking_Path()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var part = new ByteArrayContent(Png(40));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "p.png" } };
        var plate = (await (await client.PostAsync("/api/plates/images", multipart))
            .Content.ReadFromJsonAsync<PlateImageListItem>())!.Id;

        var jobSummary = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
        }

        // Domain job failed with the sanitized detector-missing code.
        using var read = _factory.Services.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PlateAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == jobSummary!.Id);
        Assert.Equal(PlateAnalysisJobStatuses.Failed, job.Status);
        Assert.Equal(PlateAnalysisErrorCodes.DetectorModelMissing, job.ErrorCode);

        // The safe message and every API response must not leak the model path.
        Assert.DoesNotContain("detector-SECRET", job.ErrorMessageSafe ?? string.Empty);
        Assert.DoesNotContain("/opt/nonexistent", job.ErrorMessageSafe ?? string.Empty);

        var detailBody = await (await client.GetAsync($"/api/plates/images/{plate}")).Content.ReadAsStringAsync();
        var latestBody = await (await client.GetAsync($"/api/plates/images/{plate}/analysis/latest")).Content.ReadAsStringAsync();
        foreach (var body in new[] { detailBody, latestBody })
        {
            Assert.DoesNotContain("detector-SECRET", body);
            Assert.DoesNotContain("ocr-SECRET", body);
            Assert.DoesNotContain("/opt/nonexistent", body);
            Assert.DoesNotContain(".onnx", body);
        }

        // The background job itself still SUCCEEDS (the domain job carries the outcome).
        var bgStatus = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.PlatesAnalyze).Select(j => j.Status).SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, bgStatus);
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Cli;

// Slice 55 — the `metadata backfill` operator CLI command, wired through the
// real dispatcher with a SQLite-backed service provider (the factory registers
// MetadataBackfillService).
public sealed class MetadataBackfillCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MetadataBackfillCliTests()
    {
        _factory = new SqliteWebApplicationFactory();
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

    private async Task<Guid> UploadPendingImageAsync(HttpClient client)
    {
        var part = new ByteArrayContent(Png(12));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "p.png" } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        // Force it into a "pending, no version" state so backfill sees it.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == summary!.Id);
        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.ExtractionStatus = MetadataStatuses.Pending;
        meta.ExtractionVersion = null;
        await db.SaveChangesAsync();
        return file.BlobObjectId;
    }

    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Backfill_DryRun_Reports_Candidates_Without_Changing_Rows()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var blob = await UploadPendingImageAsync(client);

        var (exit, stdout, stderr) = await RunCli("metadata", "backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("dry-run", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = await db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Pending, meta.ExtractionStatus);
        Assert.Null(meta.ExtractionVersion);
    }

    [Fact]
    public async Task Backfill_Processes_Pending_Rows()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var blob = await UploadPendingImageAsync(client);

        var (exit, stdout, _) = await RunCli("metadata", "backfill");

        Assert.Equal(0, exit);
        Assert.Contains("processed", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = await db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Completed, meta.ExtractionStatus);
        Assert.NotNull(meta.ExtractionVersion);
    }

    [Fact]
    public async Task Backfill_With_Invalid_Limit_Returns_64()
    {
        var (exit, _, stderr) = await RunCli("metadata", "backfill", "--limit", "0");
        Assert.Equal(64, exit);
        Assert.Contains("--limit", stderr);
    }

    [Fact]
    public async Task Backfill_Without_Database_Returns_78()
    {
        // A bare provider with no MetadataBackfillService → config error.
        var collection = new ServiceCollection();
        collection.AddSingleton(TimeProvider.System);
        using var provider = collection.BuildServiceProvider();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            new[] { "metadata", "backfill" }, stdout, stderr, () => provider);

        Assert.Equal(78, exit);
        Assert.Contains("not configured", stderr.ToString());
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Cli;

// Slice 63 — `media derivatives backfill` operator CLI command.
public sealed class MediaDerivativesBackfillCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MediaDerivativesBackfillCliTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var file = await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
        return file.Id;
    }

    private async Task DropThumbnailRowsAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.FileThumbnails
            .Where(t => t.FileItemId == fileItemId)
            .ExecuteDeleteAsync();
    }

    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<int> CountThumbsAsync(Guid fileItemId, string size)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileThumbnails.AsNoTracking()
            .CountAsync(t => t.FileItemId == fileItemId && t.Size == size);
    }

    [Fact]
    public async Task Backfill_DryRun_Reports_Counts_Without_Creating_Rows()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        // Drop the eager small thumbnail (slice 19) so the prewarm sees a
        // missing-medium row too. Posters never get generated at upload.
        await DropThumbnailRowsAsync(video);
        var image = await UploadAsync(owner, ImageFixtures.PlainPng(), "p.png", "image/png");
        await DropThumbnailRowsAsync(image);

        var (exit, stdout, stderr) = await RunCli("media", "derivatives", "backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("dry-run", stdout);
        Assert.Equal(0, await CountThumbsAsync(video, ThumbnailSizes.Poster));
        Assert.Equal(0, await CountThumbsAsync(image, ThumbnailSizes.Small));
    }

    [Fact]
    public async Task Backfill_Creates_Missing_Poster_For_Video()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");
        Assert.Equal(0, await CountThumbsAsync(video, ThumbnailSizes.Poster));

        var (exit, stdout, stderr) = await RunCli("media", "derivatives", "backfill");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(1, await CountThumbsAsync(video, ThumbnailSizes.Poster));
        Assert.Contains("processed", stdout);
    }

    [Fact]
    public async Task Backfill_Creates_Missing_Image_Thumbnails_And_Medium_Preview()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var image = await UploadAsync(owner, ImageFixtures.PlainPng(), "p.png", "image/png");
        // Drop both eager + lazy derivatives so the prewarm has work for the
        // small (slice 19) and medium (slice 59) sizes.
        await DropThumbnailRowsAsync(image);

        var (exit, _, _) = await RunCli("media", "derivatives", "backfill");
        Assert.Equal(0, exit);

        Assert.Equal(1, await CountThumbsAsync(image, ThumbnailSizes.Small));
        Assert.Equal(1, await CountThumbsAsync(image, ThumbnailSizes.Medium));
    }

    [Fact]
    public async Task Backfill_Is_Idempotent_On_Already_Materialised_Derivatives()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        // First run populates the poster.
        Assert.Equal(0, (await RunCli("media", "derivatives", "backfill")).exit);
        Assert.Equal(1, await CountThumbsAsync(video, ThumbnailSizes.Poster));

        // Second run sees the row already exists and is a no-op.
        var (exit2, stdout2, _) = await RunCli("media", "derivatives", "backfill");
        Assert.Equal(0, exit2);
        Assert.Equal(1, await CountThumbsAsync(video, ThumbnailSizes.Poster));
        Assert.Contains("done", stdout2);
    }

    [Fact]
    public async Task Backfill_Invalid_Limit_Returns_64()
    {
        var (exit, _, stderr) = await RunCli("media", "derivatives", "backfill", "--limit", "not-a-number");
        Assert.Equal(64, exit);
        Assert.Contains("--limit", stderr);
    }

    [Fact]
    public async Task Backfill_Logs_Contain_No_File_Names_Or_Paths()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "secret-cliptag.mp4", "video/mp4");

        var (exit, stdout, _) = await RunCli("media", "derivatives", "backfill");
        Assert.Equal(0, exit);
        // No raw file names or storage internals — only counts.
        Assert.DoesNotContain("secret-cliptag", stdout);
        Assert.DoesNotContain("objects/", stdout);
        Assert.DoesNotContain(video.ToString(), stdout);
    }

    // Slice 99 — `media derivatives failures` + retry gating via the CLI.
    [Fact]
    public async Task Failures_Reports_Codes_And_RetryFailed_Reattempts()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        // A decodable-header / undecodable-body PNG: identify succeeds, decode
        // fails → a permanent decode_failed diagnostic after the backfill.
        var image = await UploadAsync(owner, ImageFixtures.UndecodablePng(1), "secret-name.png", "image/png");
        await DropThumbnailRowsAsync(image);

        Assert.Equal(0, (await RunCli("media", "derivatives", "backfill")).exit);

        // Default re-run is blocked by the permanent diagnostic (no new attempt).
        var (_, skipStdout, _) = await RunCli("media", "derivatives", "backfill");
        Assert.Contains("done", skipStdout);

        // `failures` surfaces the code + counts, with no names / paths / ids.
        var (failExit, failStdout, failStderr) = await RunCli("media", "derivatives", "failures");
        Assert.Equal(0, failExit);
        Assert.Equal(string.Empty, failStderr);
        Assert.Contains("decode_failed", failStdout);
        Assert.Contains("failed_permanent", failStdout);
        Assert.DoesNotContain("secret-name", failStdout);
        Assert.DoesNotContain(image.ToString(), failStdout);
        Assert.DoesNotContain("objects/", failStdout);

        // --retry-failed re-attempts the blocked diagnostic (attempt count grows).
        var (retryExit, retryStdout, _) = await RunCli("media", "derivatives", "backfill", "--retry-failed");
        Assert.Equal(0, retryExit);
        Assert.Contains("retry-failed", retryStdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.DerivativeDiagnostics.AsNoTracking()
            .Where(d => d.FileItemId == image && d.Size == ThumbnailSizes.Small)
            .Select(d => d.AttemptCount).SingleAsync();
        Assert.Equal(2, attempts);
    }

    // Slice 100 — `media derivatives benchmark` compares backends, read-only.
    [Fact]
    public async Task Benchmark_Compares_Backends_Without_Leaking_Or_Mutating()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(owner, ImageFixtures.PlainPng(64 + i, 48 + i), $"secret-bench-{i}.png", "image/png");
        }
        var thumbsBefore = await CountAllThumbsAsync();

        var (exit, stdout, stderr) = await RunCli("media", "derivatives", "benchmark", "--limit", "10");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("imagesharp", stdout);
        // No file names / paths / ids leak into benchmark output.
        Assert.DoesNotContain("secret-bench", stdout);
        Assert.DoesNotContain("objects/", stdout);
        // Read-only: the benchmark renders in memory and stores nothing.
        Assert.Equal(thumbsBefore, await CountAllThumbsAsync());
    }

    private async Task<int> CountAllThumbsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileThumbnails.CountAsync();
    }
}

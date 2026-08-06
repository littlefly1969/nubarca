using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 99: the Storage Stats endpoint exposes the derivative-diagnostics
// distribution (never-attempted vs failed-permanent vs …) and must do so
// without leaking any identifier — ids, storage keys, sha, paths, or raw
// metadata.
public sealed class DerivativeDiagnosticsStatsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public DerivativeDiagnosticsStatsEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> CreateAdminClientAsync(string email = "owner@example.com")
    {
        var userId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(userId);
        return await _factory.LoginAsync(email);
    }

    private async Task<FileItem> SeedMissingImageAsync(Guid ownerId, string name, int size)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        // No small thumbnail → the image is "missing small/medium"; the PNG is
        // auto-detected as image/png so it counts as an image candidate.
        return await files.CreateAsync(
            ownerId, null, name, "image/png",
            new MemoryStream(ImageFixtures.PlainPng(size, size)),
            generateSmallThumbnail: false);
    }

    [Fact]
    public async Task Stats_Distinguish_NeverAttempted_From_Recorded_Failures_Without_Leaking_Identifiers()
    {
        var client = await CreateAdminClientAsync();
        var ownerId = await _factory.SeedUserAsync("media@example.com");

        var a = await SeedMissingImageAsync(ownerId, "a.png", 20);
        var b = await SeedMissingImageAsync(ownerId, "b.png", 24);

        // Record a permanent failure for ONE of the two missing images (size
        // small). The other stays never-attempted.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var diagnostics = scope.ServiceProvider.GetRequiredService<DerivativeDiagnosticsService>();
            await diagnostics.RecordAsync(
                a.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedPermanent,
                DerivativeErrorCodes.DecodeFailed, "image/png", "PNG",
                DerivativeBackends.ImageSharp, DerivativeGenerators.ImageVersion);
        }

        var response = await client.GetAsync("/api/admin/storage-stats?physical=false");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<StorageStatsResponse>();
        Assert.NotNull(body);
        var diag = body!.DerivativeDiagnostics;
        Assert.NotNull(diag);

        // 2 images missing small; 1 recorded failed → 1 never-attempted.
        Assert.Equal(2, body.Derivatives.ImagesMissingSmall);
        Assert.Equal(1, diag!.Small.Recorded);
        Assert.Equal(1, diag.Small.FailedPermanent);
        Assert.Equal(1, diag.Small.NeverAttempted);
        Assert.Contains(diag.Small.ByErrorCode, c => c.Code == DerivativeErrorCodes.DecodeFailed && c.Count == 1);
        Assert.Contains(diag.Small.TopFormats, f => f.DetectedContentType == "image/png");

        // No-leak: the raw JSON carries no ids, storage keys, sha, or paths.
        var raw = await response.Content.ReadAsStringAsync();
        var storageKey = await StorageKeyOfAsync(a.BlobObjectId);
        Assert.DoesNotContain(a.Id.ToString("N"), raw);
        Assert.DoesNotContain(a.Id.ToString("D"), raw);
        Assert.DoesNotContain(b.Id.ToString("N"), raw);
        Assert.DoesNotContain(storageKey, raw);
        Assert.DoesNotContain("StorageKey", raw);
        Assert.DoesNotContain("storageKey", raw);
        Assert.DoesNotContain(_factory.StorageRoot, raw);
    }

    private async Task<string> StorageKeyOfAsync(Guid blobObjectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobObjectId).Select(b => b.StorageKey).SingleAsync();
    }
}

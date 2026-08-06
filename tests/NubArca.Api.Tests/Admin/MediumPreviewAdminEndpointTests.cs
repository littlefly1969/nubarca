using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Admin;

public sealed class MediumPreviewAdminEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MediumPreviewAdminEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Rebuild_As_NonAdmin_Returns_403()
    {
        await _factory.SeedUserAsync("user@example.com");
        var client = await _factory.LoginAsync("user@example.com");

        var response = await client.PostAsync("/api/admin/media/previews/medium/rebuild", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Status_As_Admin_Returns_Configured_Max_Edge()
    {
        var userId = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(userId);
        var client = await _factory.LoginAsync("admin@example.com");

        var response = await client.GetAsync("/api/admin/media/previews/medium/status");

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1920, doc.RootElement.GetProperty("mediumPreviewMaxEdge").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("job").ValueKind);
    }

    [Fact]
    public async Task Status_Uses_Configured_Max_Edge_Override()
    {
        using var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["MediaDerivatives:MediumPreviewMaxEdge"] = "640",
            });
        factory.EnsureDatabaseCreated();
        var userId = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(userId);
        var client = await factory.LoginAsync("admin@example.com");

        var response = await client.GetAsync("/api/admin/media/previews/medium/status");

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(640, doc.RootElement.GetProperty("mediumPreviewMaxEdge").GetInt32());
    }

    [Fact]
    public async Task Rebuild_As_Admin_Enqueues_Job_And_Does_Not_Delete_Inline()
    {
        var userId = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(userId);
        var fileId = await SeedImageWithMediumPreviewAsync(userId);
        var client = await _factory.LoginAsync("admin@example.com");

        var response = await client.PostAsync("/api/admin/media/previews/medium/rebuild", null);

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1920, doc.RootElement.GetProperty("mediumPreviewMaxEdge").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.BackgroundJobs.AsNoTracking()
            .AnyAsync(j => j.Type == JobTypes.MediumPreviewRegenerate && j.Status == JobStatuses.Queued));
        Assert.True(await db.FileThumbnails.AsNoTracking()
            .AnyAsync(t => t.FileItemId == fileId && t.Size == ThumbnailSizes.Medium));
    }

    private async Task<Guid> SeedImageWithMediumPreviewAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var sourceBlobId = Guid.NewGuid();
        var previewBlobId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        db.BlobObjects.AddRange(
            new BlobObject
            {
                Id = sourceBlobId,
                Sha256 = new string('a', 64),
                StorageKey = "aa/bb/source",
                SizeBytes = 1024,
                ReferenceCount = 1,
                CreatedAt = now,
            },
            new BlobObject
            {
                Id = previewBlobId,
                Sha256 = new string('b', 64),
                StorageKey = "bb/cc/preview",
                SizeBytes = 128,
                ReferenceCount = 1,
                CreatedAt = now,
            });
        db.FileItems.Add(new FileItem
        {
            Id = fileId,
            OwnerUserId = ownerUserId,
            BlobObjectId = sourceBlobId,
            Name = "photo.jpg",
            MimeType = "image/jpeg",
            SizeBytes = 1024,
            Width = 4000,
            Height = 2000,
            CreatedAt = now,
            EffectiveDateTaken = now,
        });
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = sourceBlobId,
            SizeBytes = 1024,
            MediaCategory = MediaCategories.Image,
            DetectedContentType = "image/jpeg",
            DetectedFormat = "JPEG",
            Width = 4000,
            Height = 2000,
            PixelCount = 8_000_000,
        });
        db.FileThumbnails.Add(new FileThumbnail
        {
            Id = Guid.NewGuid(),
            FileItemId = fileId,
            BlobObjectId = previewBlobId,
            Size = ThumbnailSizes.Medium,
            Width = 1280,
            Height = 640,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return fileId;
    }
}

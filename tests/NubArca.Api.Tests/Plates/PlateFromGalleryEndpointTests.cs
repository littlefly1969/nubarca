using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Plates;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// POST /api/plates/images/from-gallery: owner-scoped, no byte copy (blob
// reference acquire), idempotent active membership, partial added/skipped
// result, no automatic analysis, no gallery mutation, cross-owner safe.
public sealed class PlateFromGalleryEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PlateFromGalleryEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Item(string id, string originalFileName, string status, int platesCount);
    private sealed record Skip(string itemId, string reason);
    private sealed record AddResult(List<Item> added, List<Skip> skipped);

    private async Task<(Guid fileId, Guid blobId)> SeedGalleryImageAsync(Guid ownerId, int refCount = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = refCount, CreatedAt = DateTime.UtcNow,
        });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
            Name = "photo.png", MimeType = "image/png", SizeBytes = 1,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (fileId, blobId);
    }

    [Fact]
    public async Task Requires_Auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(
            "/api/plates/images/from-gallery", new { fileItemIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Empty_Ids_Is_BadRequest()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsJsonAsync(
            "/api/plates/images/from-gallery", new { fileItemIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Adds_Gallery_Image_Without_Copying_Bytes_And_Without_Analysis()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var (fileId, blobId) = await SeedGalleryImageAsync(ownerId);

        var resp = await client.PostAsJsonAsync(
            "/api/plates/images/from-gallery", new { fileItemIds = new[] { fileId } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = (await resp.Content.ReadFromJsonAsync<AddResult>())!;
        Assert.Single(result.added);
        Assert.Empty(result.skipped);
        // No analysis started: freshly added, no detections.
        Assert.Equal(PlateImageStatuses.Uploaded, result.added[0].status);
        Assert.Equal(0, result.added[0].platesCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // One owner-private plate referencing the SAME blob (no byte copy).
        var plate = await db.PlateImages.AsNoTracking().SingleAsync(p => p.OwnerUserId == ownerId);
        Assert.Equal(blobId, plate.BlobObjectId);
        // Blob now has TWO references (gallery FileItem + plate).
        Assert.Equal(2, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
        // Gallery FileItem is untouched.
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == fileId && f.DeletedAt == null));
    }

    [Fact]
    public async Task Is_Idempotent_On_Active_Membership()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var (fileId, blobId) = await SeedGalleryImageAsync(ownerId);

        await client.PostAsJsonAsync("/api/plates/images/from-gallery", new { fileItemIds = new[] { fileId } });
        var again = await client.PostAsJsonAsync("/api/plates/images/from-gallery", new { fileItemIds = new[] { fileId } });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Still exactly one plate; refcount did not climb past 2.
        Assert.Equal(1, await db.PlateImages.CountAsync(p => p.OwnerUserId == ownerId));
        Assert.Equal(2, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
    }

    [Fact]
    public async Task Foreign_File_Is_Skipped_Not_Added()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (bobId, _) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var (bobFileId, _) = await SeedGalleryImageAsync(bobId);

        var resp = await alice.PostAsJsonAsync(
            "/api/plates/images/from-gallery", new { fileItemIds = new[] { bobFileId } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = (await resp.Content.ReadFromJsonAsync<AddResult>())!;
        Assert.Empty(result.added);
        Assert.Single(result.skipped);
        Assert.Equal(bobFileId.ToString(), result.skipped[0].itemId, ignoreCase: true);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.PlateImages.CountAsync(p => p.OwnerUserId == aliceId));
    }

    [Fact]
    public async Task Deleting_Source_Gallery_File_Keeps_The_Plate()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var (fileId, blobId) = await SeedGalleryImageAsync(ownerId);
        await client.PostAsJsonAsync("/api/plates/images/from-gallery", new { fileItemIds = new[] { fileId } });

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.SoftDeleteAsync(ownerId, fileId);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Plate survives; blob keeps the plate's single remaining reference.
            Assert.Equal(1, await db.PlateImages.CountAsync(p => p.BlobObjectId == blobId));
            Assert.Equal(1, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
        }
    }
}

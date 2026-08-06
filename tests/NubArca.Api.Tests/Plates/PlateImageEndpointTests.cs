using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Plates;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Plates;

public sealed class PlateImageEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PlateImageEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // --- Helpers ---

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> PostPlateAsync(
        HttpClient client, byte[] bytes, string name, string contentType = "image/png")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        return await client.PostAsync("/api/plates/images", multipart);
    }

    private static async Task<PlateImageListItem> UploadPlateAsync(
        HttpClient client, string name = "plate.png", int dim = 24)
    {
        var response = await PostPlateAsync(client, Png(dim), name);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlateImageListItem>())!;
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private static async Task<string> RawAsync(HttpClient client, string url)
        => await (await client.GetAsync(url)).Content.ReadAsStringAsync();

    // --- Auth ---

    [Fact]
    public async Task All_Plate_Endpoints_Require_Auth()
    {
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/plates/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostPlateAsync(anon, Png(16), "x.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/plates/images/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/plates/images/{id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/plates/images/{id}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/plates/images/{id}/original")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"/api/plates/images/{id}")).StatusCode);
    }

    // --- Upload creates an owner-private plate image ---

    [Fact]
    public async Task Upload_Creates_OwnerPrivate_PlateImage()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await PostPlateAsync(client, Png(48), "targa-01.png");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var item = (await response.Content.ReadFromJsonAsync<PlateImageListItem>())!;
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal("targa-01.png", item.OriginalFileName);
        Assert.Equal("image/png", item.ContentType);
        Assert.Equal(48, item.Width);
        Assert.Equal(48, item.Height);
        Assert.Equal(PlateImageStatuses.Uploaded, item.Status);
        Assert.Contains($"/api/plates/images/{item.Id}/thumbnail", item.ThumbnailUrl);
        Assert.Contains($"/api/plates/images/{item.Id}/preview", item.PreviewUrl);

        var row = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == item.Id));
        Assert.NotNull(row);
        Assert.Equal(ownerId, row!.OwnerUserId);
        Assert.NotEqual(Guid.Empty, row.BlobObjectId);
        Assert.StartsWith(PlateContainerKey.Prefix, row.LogicalContainerKey);
    }

    // --- Listing returns only the current owner ---

    [Fact]
    public async Task List_Returns_Only_Current_Owner_Images()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var a1 = await UploadPlateAsync(alice, "a1.png");
        var a2 = await UploadPlateAsync(alice, "a2.png", dim: 32);
        var b1 = await UploadPlateAsync(bob, "b1.png");

        var aliceList = (await alice.GetFromJsonAsync<List<PlateImageListItem>>("/api/plates/images"))!;
        Assert.Equal(2, aliceList.Count);
        Assert.Contains(aliceList, x => x.Id == a1.Id);
        Assert.Contains(aliceList, x => x.Id == a2.Id);
        Assert.DoesNotContain(aliceList, x => x.Id == b1.Id);

        var bobList = (await bob.GetFromJsonAsync<List<PlateImageListItem>>("/api/plates/images"))!;
        Assert.Single(bobList);
        Assert.Equal(b1.Id, bobList[0].Id);
    }

    // --- Cross-owner detail / preview / original / delete all 404 ---

    [Fact]
    public async Task Foreign_Owner_Cannot_Read_Preview_Original_Or_Delete()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var plate = await UploadPlateAsync(alice, "secret.png");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate.Id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate.Id}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate.Id}/original")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/plates/images/{plate.Id}")).StatusCode);

        // Alice's plate is untouched by Bob's failed delete.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/plates/images/{plate.Id}")).StatusCode);
    }

    // --- Detail + media serve for the owner ---

    [Fact]
    public async Task Owner_Can_Read_Detail_Preview_Thumbnail_And_Original()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, "car.png", dim: 64);

        var detailResponse = await client.GetAsync($"/api/plates/images/{plate.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = (await detailResponse.Content.ReadFromJsonAsync<PlateImageDetail>())!;
        Assert.Equal(plate.Id, detail.Id);
        Assert.Equal(0, detail.AnalysisSummary.PlatesCount);
        Assert.False(detail.AnalysisSummary.FacesRedactedAvailable);
        Assert.Equal(PlateImageStatuses.AnalysisNotStarted, detail.AnalysisSummary.AnalysisStatus);
        Assert.Contains($"/api/plates/images/{plate.Id}/original", detail.OriginalUrl);

        var preview = await client.GetAsync($"/api/plates/images/{plate.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.True((await preview.Content.ReadAsByteArrayAsync()).Length > 0);

        var thumb = await client.GetAsync($"/api/plates/images/{plate.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);

        var original = await client.GetAsync($"/api/plates/images/{plate.Id}/original");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
        Assert.True((await original.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    // --- Delete removes the reference and releases the blob ---

    [Fact]
    public async Task Delete_Removes_Plate_Reference_And_Releases_Blob()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, "gone.png");

        var blobId = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plate.Id).Select(p => p.BlobObjectId).FirstAsync());

        var delete = await client.DeleteAsync($"/api/plates/images/{plate.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var stillThere = await InDbAsync(db => db.PlateImages.AsNoTracking().AnyAsync(p => p.Id == plate.Id));
        Assert.False(stillThere);

        // The reference was released, so the (now-orphaned) blob is
        // janitor-eligible (ReferenceCount == 0) — not deleted inline.
        var refCount = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobId).Select(b => (long?)b.ReferenceCount).FirstOrDefaultAsync());
        Assert.Equal(0, refCount);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plates/images/{plate.Id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_Does_Not_Release_Blob_Still_Referenced_By_Another_Plate()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        // Identical bytes → the content-addressed store dedups onto ONE blob
        // referenced by two plate rows (refcount 2).
        var bytes = Png(40);
        var first = (await (await PostPlateAsync(client, bytes, "dup-1.png")).Content
            .ReadFromJsonAsync<PlateImageListItem>())!;
        var second = (await (await PostPlateAsync(client, bytes, "dup-2.png")).Content
            .ReadFromJsonAsync<PlateImageListItem>())!;

        var blobId = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == first.Id).Select(p => p.BlobObjectId).FirstAsync());
        var otherBlobId = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == second.Id).Select(p => p.BlobObjectId).FirstAsync());
        Assert.Equal(blobId, otherBlobId);

        await client.DeleteAsync($"/api/plates/images/{first.Id}");

        // Blob is still referenced by the second plate — not reclaimable.
        var refCount = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
        Assert.Equal(1, refCount);

        // The second plate still serves its original.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/plates/images/{second.Id}/original")).StatusCode);
    }

    // --- Plates never enter Files / Gallery ---

    [Fact]
    public async Task PlateImage_Creates_No_FileItem_And_Is_Absent_From_Gallery_And_Files()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, "hidden.png");

        // No FileItem is ever created for a plate.
        var fileCount = await InDbAsync(db => db.FileItems.IgnoreQueryFilters()
            .CountAsync(f => f.OwnerUserId == ownerId));
        Assert.Equal(0, fileCount);

        // Gallery (images) and the root files listing never surface the plate id.
        var gallery = await RawAsync(client, "/api/images");
        Assert.DoesNotContain(plate.Id.ToString(), gallery, StringComparison.OrdinalIgnoreCase);

        var files = await RawAsync(client, "/api/folders/children");
        Assert.DoesNotContain(plate.Id.ToString(), files, StringComparison.OrdinalIgnoreCase);
    }

    // --- No blob/storage/path/hash internals leak ---

    [Fact]
    public async Task Responses_Do_Not_Expose_Blob_Or_Container_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, "leak-check.png");

        var (sha, storageKey, blobId, containerKey) = await InDbAsync(async db =>
        {
            var p = await db.PlateImages.AsNoTracking().FirstAsync(x => x.Id == plate.Id);
            var b = await db.BlobObjects.AsNoTracking().FirstAsync(x => x.Id == p.BlobObjectId);
            return (b.Sha256, b.StorageKey, p.BlobObjectId.ToString(), p.LogicalContainerKey);
        });

        var listBody = await RawAsync(client, "/api/plates/images");
        var detailBody = await RawAsync(client, $"/api/plates/images/{plate.Id}");

        foreach (var body in new[] { listBody, detailBody })
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            // Concrete secret VALUES for this plate must never appear.
            Assert.DoesNotContain(sha, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storageKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(blobId, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(containerKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("logicalContainerKey", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerUserId", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Upload validation ---

    [Fact]
    public async Task Upload_Rejects_Non_Image_And_Leaves_No_Reference()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await PostPlateAsync(client, "this is not an image"u8.ToArray(), "note.txt", "text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var plateCount = await InDbAsync(db => db.PlateImages.CountAsync(p => p.OwnerUserId == ownerId));
        Assert.Equal(0, plateCount);

        // The transiently-stored blob (if any) must not remain referenced.
        var pinned = await InDbAsync(db => db.BlobObjects.AsNoTracking().AnyAsync(b => b.ReferenceCount > 0));
        Assert.False(pinned);
    }

    // --- Blob-reference accounting stays consistent (janitor safety) ---

    [Fact]
    public async Task Blob_Reference_Audit_Counts_Plate_Reference()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadPlateAsync(client, "counted.png");

        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>();
        var report = await audit.AuditAsync();

        // The plate blob's stored ReferenceCount already matches the computed
        // truth (which now includes plate_images): nothing is under-counted, so
        // repair would never zero a live plate blob.
        Assert.Equal(0, report.ZeroRefWithRealReferences);
        Assert.Equal(report.TotalBlobs, report.MatchedReferenceCount);
    }

    // --- Container key is deterministic per owner and never exposed ---

    [Fact]
    public async Task Container_Key_Is_Deterministic_Per_Owner()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var a1 = await UploadPlateAsync(alice, "a1.png");
        var a2 = await UploadPlateAsync(alice, "a2.png", dim: 30);
        var b1 = await UploadPlateAsync(bob, "b1.png");

        var keys = await InDbAsync(async db => new
        {
            A1 = await db.PlateImages.AsNoTracking().Where(p => p.Id == a1.Id).Select(p => p.LogicalContainerKey).FirstAsync(),
            A2 = await db.PlateImages.AsNoTracking().Where(p => p.Id == a2.Id).Select(p => p.LogicalContainerKey).FirstAsync(),
            B1 = await db.PlateImages.AsNoTracking().Where(p => p.Id == b1.Id).Select(p => p.LogicalContainerKey).FirstAsync(),
        });

        Assert.Equal(keys.A1, keys.A2);          // same owner → same container
        Assert.NotEqual(keys.A1, keys.B1);       // different owners → different container
        Assert.NotEqual(aliceId, bobId);
        // The key must not be inferrable to the raw owner id.
        Assert.DoesNotContain(aliceId.ToString("N"), keys.A1, StringComparison.OrdinalIgnoreCase);
    }
}

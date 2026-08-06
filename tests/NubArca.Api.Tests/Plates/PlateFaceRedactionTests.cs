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

// End-to-end coverage for the owner-private, privacy-only face redaction slice:
// auth, cross-owner isolation, real server-side redaction (bytes change,
// dimensions preserved), persisted owner-private boxes, the derived-media cache
// (hit/invalidate/blob-audit), safe errors, cascade cleanup, and the strict
// isolation invariants (no People/Face identity, no FileItem/Gallery/Files).
public sealed class PlateFaceRedactionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PlateFaceRedactionTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Plates:FaceRedaction:Enabled"] = "true",
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // --- Helpers ---

    // High-frequency checkerboard so pixelation of any region changes bytes.
    private static byte[] Checkerboard(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = ((x + y) & 1) == 0 ? new Rgba32(20, 40, 200) : new Rgba32(240, 220, 30);
                }
            }
        });
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<PlateImageListItem> UploadAsync(HttpClient client, string name = "plate.png", int dim = 200)
    {
        var part = new ByteArrayContent(Checkerboard(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/plates/images", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlateImageListItem>())!;
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private static async Task<byte[]> BytesAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static (int Width, int Height) Dimensions(byte[] bytes)
    {
        var info = Image.Identify(bytes);
        return (info.Width, info.Height);
    }

    // --- Auth ---

    [Fact]
    public async Task Redacted_Media_Requires_Auth()
    {
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/plates/images/{id}/preview?blurFaces=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/plates/images/{id}/original?blurFaces=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/plates/images/{id}/thumbnail?blurFaces=true")).StatusCode);
    }

    // --- Cross-owner isolation (generic 404) ---

    [Fact]
    public async Task Foreign_Owner_Gets_404_For_Redacted_Media()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var plate = await UploadAsync(alice, "secret.png");

        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/plates/images/{plate.Id}/preview?blurFaces=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/plates/images/{plate.Id}/original?blurFaces=true")).StatusCode);
    }

    // --- blurFaces=false serves the normal owner-private media ---

    [Fact]
    public async Task BlurFaces_False_Serves_Normal_Media()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);

        var preview = await client.GetAsync($"/api/plates/images/{plate.Id}/preview?blurFaces=false");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);

        // No redaction boxes or cache rows are created by a non-redacted request.
        Assert.Equal(0, await InDbAsync(db => db.PlateFaceRedactionBoxes.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.PlateRedactedMedia.CountAsync()));
    }

    // --- Disabled → safe 409, never the unredacted image ---

    [Fact]
    public async Task BlurFaces_True_When_Disabled_Returns_Safe_409()
    {
        // A separate factory with redaction DISABLED (the production default).
        using var disabled = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Plates:FaceRedaction:Enabled"] = "false",
        });
        disabled.EnsureDatabaseCreated();
        var userId = await disabled.SeedUserAsync();
        var client = await disabled.LoginAsync();

        var part = new ByteArrayContent(Checkerboard(120));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "x.png" } };
        var created = (await (await client.PostAsync("/api/plates/images", multipart)).Content
            .ReadFromJsonAsync<PlateImageListItem>())!;

        var response = await client.GetAsync($"/api/plates/images/{created.Id}/preview?blurFaces=true");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("face_redaction_not_configured", body);
        // Safe error: no stack trace, no model path, no storage internals.
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("DetectorModelPath", body, StringComparison.OrdinalIgnoreCase);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }

        // The detail DTO reports redaction unavailable (never serves it silently).
        var detail = (await client.GetFromJsonAsync<PlateImageDetail>($"/api/plates/images/{created.Id}"))!;
        Assert.False(detail.Redaction.Available);
        _ = userId;
    }

    // --- blurFaces=true detects+persists owner-private boxes and serves redacted media ---

    [Fact]
    public async Task BlurFaces_True_Persists_Boxes_And_Serves_Redacted_Media()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);

        var response = await client.GetAsync($"/api/plates/images/{plate.Id}/preview?blurFaces=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);

        var boxes = await InDbAsync(db => db.PlateFaceRedactionBoxes.AsNoTracking()
            .Where(b => b.OwnerUserId == ownerId && b.PlateImageId == plate.Id).ToListAsync());
        Assert.NotEmpty(boxes);
        Assert.All(boxes, b =>
        {
            Assert.Equal(ownerId, b.OwnerUserId);
            Assert.InRange(b.BoundingBoxX, 0.0, 1.0);
            Assert.InRange(b.BoundingBoxWidth, 0.0, 1.0);
            Assert.Equal("plate-face-redaction-v1", b.ModelProfileKey);
        });

        // Detail now reports availability + the persisted face count for the owner.
        var detail = (await client.GetFromJsonAsync<PlateImageDetail>($"/api/plates/images/{plate.Id}"))!;
        Assert.True(detail.Redaction.Available);
        Assert.Equal(boxes.Count, detail.Redaction.FacesCount);
    }

    // --- Redacted bytes differ from the unredacted rendition; dimensions preserved ---

    [Fact]
    public async Task Redacted_Preview_Differs_And_Preserves_Dimensions()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client, dim: 200);

        var normal = await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview");
        var redacted = await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        Assert.False(normal.AsSpan().SequenceEqual(redacted));
        Assert.Equal(Dimensions(normal), Dimensions(redacted));
    }

    [Fact]
    public async Task Redacted_Original_Differs_And_Preserves_Dimensions()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client, dim: 200);

        var original = await BytesAsync(client, $"/api/plates/images/{plate.Id}/original");
        var redacted = await BytesAsync(client, $"/api/plates/images/{plate.Id}/original?blurFaces=true");

        Assert.False(original.AsSpan().SequenceEqual(redacted));
        // Original PNG is 200x200; redacted (JPEG) preserves the dimensions.
        Assert.Equal((200, 200), Dimensions(original));
        Assert.Equal((200, 200), Dimensions(redacted));
    }

    [Fact]
    public async Task Redacted_Thumbnail_Is_Served()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client, dim: 200);

        var response = await client.GetAsync($"/api/plates/images/{plate.Id}/thumbnail?blurFaces=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    // --- Cache: a hit reuses the stored rendition (no new blob / row) ---

    [Fact]
    public async Task Redacted_Media_Is_Cached_And_Reused()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);

        var first = await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");
        var cacheAfterFirst = await InDbAsync(db => db.PlateRedactedMedia.AsNoTracking()
            .Where(m => m.OwnerUserId == ownerId && m.PlateImageId == plate.Id
                && m.SourceKind == PlateRedactionSourceKinds.Preview)
            .SingleAsync());
        var refAfterFirst = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == cacheAfterFirst.BlobObjectId).Select(b => b.ReferenceCount).SingleAsync());

        var second = await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        var cacheAfterSecond = await InDbAsync(db => db.PlateRedactedMedia.AsNoTracking()
            .Where(m => m.OwnerUserId == ownerId && m.PlateImageId == plate.Id
                && m.SourceKind == PlateRedactionSourceKinds.Preview)
            .SingleAsync());
        var refAfterSecond = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == cacheAfterFirst.BlobObjectId).Select(b => b.ReferenceCount).SingleAsync());

        Assert.True(first.AsSpan().SequenceEqual(second)); // identical bytes served
        Assert.Equal(cacheAfterFirst.Id, cacheAfterSecond.Id); // same cache row (no re-render)
        Assert.Equal(refAfterFirst, refAfterSecond); // no extra blob reference
        Assert.Equal(1, await InDbAsync(db => db.PlateRedactedMedia.CountAsync(
            m => m.PlateImageId == plate.Id && m.SourceKind == PlateRedactionSourceKinds.Preview)));
    }

    // --- Cache invalidates when the redaction profile changes ---

    [Fact]
    public async Task Cache_And_Boxes_Invalidate_When_Profile_Changes()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);

        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");
        var staleCacheId = await InDbAsync(db => db.PlateRedactedMedia.AsNoTracking()
            .Where(m => m.PlateImageId == plate.Id).Select(m => m.Id).SingleAsync());

        // Simulate a profile bump: the persisted rows now carry an OLD profile key.
        await InDbAsync(async db =>
        {
            await db.PlateFaceRedactionBoxes.Where(b => b.PlateImageId == plate.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ModelProfileKey, "stale-profile"));
            await db.PlateRedactedMedia.Where(m => m.PlateImageId == plate.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProfileKey, "stale-profile"));
            return 0;
        });

        // A new request re-detects under the CURRENT profile and drops the stale cache.
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        Assert.Equal(0, await InDbAsync(db => db.PlateFaceRedactionBoxes.CountAsync(
            b => b.PlateImageId == plate.Id && b.ModelProfileKey == "stale-profile")));
        Assert.Equal(0, await InDbAsync(db => db.PlateRedactedMedia.CountAsync(
            m => m.PlateImageId == plate.Id && m.ProfileKey == "stale-profile")));
        var fresh = await InDbAsync(db => db.PlateRedactedMedia.AsNoTracking()
            .Where(m => m.PlateImageId == plate.Id && m.ProfileKey == "plate-face-redaction-v1")
            .SingleAsync());
        Assert.NotEqual(staleCacheId, fresh.Id);
        _ = ownerId;
    }

    // --- Blob-reference audit accounts for cached redacted media ---

    [Fact]
    public async Task Blob_Reference_Audit_Counts_Redacted_Cache_Reference()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>();
        var report = await audit.AuditAsync();

        Assert.Equal(0, report.ZeroRefWithRealReferences);
        Assert.Equal(report.TotalBlobs, report.MatchedReferenceCount);
    }

    // --- Delete cascades boxes + cache rows and releases the cache blob ---

    [Fact]
    public async Task Delete_Cascades_Boxes_And_Cache_And_Releases_Blob()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        var cacheBlobId = await InDbAsync(db => db.PlateRedactedMedia.AsNoTracking()
            .Where(m => m.PlateImageId == plate.Id).Select(m => m.BlobObjectId).FirstAsync());

        var delete = await client.DeleteAsync($"/api/plates/images/{plate.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(0, await InDbAsync(db => db.PlateFaceRedactionBoxes.CountAsync(b => b.PlateImageId == plate.Id)));
        Assert.Equal(0, await InDbAsync(db => db.PlateRedactedMedia.CountAsync(m => m.PlateImageId == plate.Id)));

        // The derived cache blob's reference was released → janitor-eligible.
        var refCount = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == cacheBlobId).Select(b => (long?)b.ReferenceCount).FirstOrDefaultAsync());
        Assert.Equal(0, refCount);
        _ = ownerId;
    }

    // --- No People/Face identity artifacts, no FileItem, no Gallery/Files leakage ---

    [Fact]
    public async Task Redaction_Creates_No_Face_Person_Or_File_Artifacts()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/original?blurFaces=true");

        Assert.Equal(0, await InDbAsync(db => db.FaceDetections.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.FaceEmbeddings.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.FaceClusters.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.People.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.PersonFaceAssignments.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.FileItems.IgnoreQueryFilters().CountAsync(f => f.OwnerUserId == ownerId)));

        // Still absent from Gallery + Files.
        var gallery = await (await client.GetAsync("/api/images")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(plate.Id.ToString(), gallery, StringComparison.OrdinalIgnoreCase);
        var files = await (await client.GetAsync("/api/folders/children")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(plate.Id.ToString(), files, StringComparison.OrdinalIgnoreCase);
    }

    // --- Detail DTO exposes no boxes / internals ---

    [Fact]
    public async Task Detail_Redaction_Summary_Exposes_No_Boxes_Or_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadAsync(client);
        await BytesAsync(client, $"/api/plates/images/{plate.Id}/preview?blurFaces=true");

        var body = await (await client.GetAsync($"/api/plates/images/{plate.Id}")).Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
        // Redaction boxes are baked into media, never serialized.
        Assert.DoesNotContain("boundingBox", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redactionBox", body, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 58 — strip embedded image metadata as a strong file mutation.
// Verifies: blobs are immutable, the dedup-aware StoreAsync either reuses
// or creates a new blob, only the caller's FileItem is updated, other
// FileItems sharing the old blob are unaffected, user metadata is
// preserved, the new BlobMetadata is regenerated from the stripped bytes
// (no GPS / serials / raw doc), and DTO responses carry no storage
// internals.
public sealed class MetadataStripEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MetadataStripEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    [Fact]
    public async Task Strip_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync(
            $"/api/files/{Guid.NewGuid()}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Strip_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync(
            $"/api/files/{Guid.NewGuid()}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Strip_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(
            alice, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.PostAsync(
            $"/api/files/{aliceFile.Id}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Strip_SoftDeleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var delete = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Strip_NonImage_File_Returns_415()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, "plain notes"u8.ToArray(), "notes.txt", "text/plain");

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Strip_Spoofed_NonImage_With_Image_Mime_Returns_415()
    {
        // Client lies about MimeType but the bytes don't decode as an image,
        // so BlobMetadata.DetectedContentType is null → 415 (no spoofing).
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, "not really jpeg"u8.ToArray(), "fake.jpg", "image/jpeg");

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Strip_Image_Swaps_To_New_Blob_With_Different_Sha256()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");
        var oldBlobId = file.BlobObjectId;

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await InDbAsync(db => db.FileItems.AsNoTracking()
            .SingleAsync(f => f.Id == file.Id));
        Assert.NotEqual(oldBlobId, updated.BlobObjectId);

        // The old blob's row still exists; SHA-256 of the new blob is
        // different from the old one.
        var (oldSha, newSha) = await InDbAsync(async db =>
        {
            var oldB = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == oldBlobId);
            var newB = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == updated.BlobObjectId);
            return (oldB.Sha256, newB.Sha256);
        });
        Assert.NotEqual(oldSha, newSha);
    }

    [Fact]
    public async Task Strip_Old_Blob_Refcount_Drops_To_Zero_When_Only_Reference()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");
        var oldBlobId = file.BlobObjectId;

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (oldRefcount, newRefcount) = await InDbAsync(async db =>
        {
            var oldB = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == oldBlobId);
            var fileNow = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id);
            var newB = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == fileNow.BlobObjectId);
            return (oldB.ReferenceCount, newB.ReferenceCount);
        });

        Assert.Equal(0, oldRefcount);
        Assert.Equal(1, newRefcount);
    }

    [Fact]
    public async Task Strip_Does_Not_Affect_Other_Users_FileItem_Sharing_Old_Blob()
    {
        var bytes = ImageFixtures.JpegWithExif(includeGps: true);
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");

        var aliceFile = await UploadAsync(alice, bytes, "a.jpg", "image/jpeg");
        var bobFile = await UploadAsync(bob, bytes, "b.jpg", "image/jpeg");
        Assert.Equal(aliceFile.BlobObjectId, bobFile.BlobObjectId); // dedup

        var sharedBlobId = aliceFile.BlobObjectId;

        // Alice strips.
        var aliceStrip = await aliceClient.PostAsync(
            $"/api/files/{aliceFile.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, aliceStrip.StatusCode);

        // Bob's FileItem still points at the original (un-stripped) blob.
        var bobNow = await InDbAsync(db => db.FileItems.AsNoTracking()
            .SingleAsync(f => f.Id == bobFile.Id));
        Assert.Equal(sharedBlobId, bobNow.BlobObjectId);

        // The shared blob row still exists, refcount = 1 (Bob's reference).
        var sharedRefcount = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == sharedBlobId)
            .Select(b => (long?)b.ReferenceCount)
            .SingleOrDefaultAsync());
        Assert.Equal(1, sharedRefcount);

        // Bob's view of the metadata still shows the original embedded data
        // (the blob's BlobMetadata was untouched).
        var bobMeta = await bobClient.GetFromJsonAsync<FileMetadataResponse>(
            $"/api/files/{bobFile.Id}/metadata");
        Assert.True(bobMeta!.Blob.Embedded!.HasGps);
        Assert.Equal(ImageFixtures.CameraModel, bobMeta.Blob.Embedded.CameraModel);
    }

    [Fact]
    public async Task Strip_Preserves_User_Metadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");

        // Set rich user metadata on the file first.
        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new
            {
                title = "My favourite",
                description = "trip to the park",
                tags = new[] { "park", "summer" },
                rating = 4,
                favorite = true,
                locationOverride = "Backyard",
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        var meta = await client.GetFromJsonAsync<FileMetadataResponse>(
            $"/api/files/{file.Id}/metadata");
        Assert.Equal("My favourite", meta!.User.Title);
        Assert.Equal("trip to the park", meta.User.Description);
        Assert.Equal(new[] { "park", "summer" }, meta.User.Tags.ToArray());
        Assert.Equal(4, meta.User.Rating);
        Assert.True(meta.User.Favorite);
        Assert.Equal("Backyard", meta.User.LocationOverride);
        Assert.Equal("Backyard", meta.Effective.Location);
    }

    [Fact]
    public async Task Strip_New_BlobMetadata_Has_No_Gps_Serials_Or_RawDoc()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        // BlobMetadata for the NEW blob carries no embedded GPS / serials /
        // raw document. The OLD blob's BlobMetadata still has them — that's
        // the blob-immutability contract.
        var fileNow = await InDbAsync(db => db.FileItems.AsNoTracking()
            .SingleAsync(f => f.Id == file.Id));
        var newMeta = await InDbAsync(db => db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == fileNow.BlobObjectId));
        Assert.Null(newMeta.GpsLatitude);
        Assert.Null(newMeta.GpsLongitude);
        Assert.Null(newMeta.BodySerialNumber);
        Assert.Null(newMeta.LensSerialNumber);
        Assert.Null(newMeta.Software);
        Assert.Null(newMeta.CameraModel);

        // The raw extraction document may still describe the JPEG container
        // (compression type, dimensions) — that's format-only data, not
        // user-identifying. What MUST NOT survive: any sensitive ASCII
        // strings the original embedded EXIF carried.
        if (newMeta.RawMetadataJson is string raw)
        {
            Assert.DoesNotContain(ImageFixtures.BodySerial, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(ImageFixtures.LensSerial, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(ImageFixtures.Software, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(ImageFixtures.CameraMake, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(ImageFixtures.CameraModel, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(ImageFixtures.LensModel, raw, StringComparison.Ordinal);
            // GPS coordinates: the latitude in the fixture is 51 deg N. Any
            // residue of the parsed coordinate would print "51".
            Assert.DoesNotContain("Latitude", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Longitude", raw, StringComparison.Ordinal);
        }

        // Curated DTO surfaces this: HasGps = false on the new blob.
        var dto = await client.GetFromJsonAsync<FileMetadataResponse>(
            $"/api/files/{file.Id}/metadata");
        Assert.NotNull(dto!.Blob.Embedded);
        Assert.False(dto.Blob.Embedded!.HasGps);
    }

    [Fact]
    public async Task Strip_Audit_Row_Written()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        var hasAudit = await InDbAsync(db => db.AuditLogs.AnyAsync(
            a => a.Action == "file.metadata_strip_embedded"
                && a.EntityType == "file"
                && a.EntityId == file.Id
                && a.UserId == owner));
        Assert.True(hasAudit);
    }

    [Fact]
    public async Task Strip_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "leak.jpg", "image/jpeg");

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        // Slice 57's centralized list, plus the literal sensitive ASCII
        // strings the fixture embedded. The Response is the curated
        // FileMetadataResponse, so it must not carry storage internals nor
        // any of the now-stripped sensitive embedded fields.
        var forbidden = MetadataExposurePolicy.ForbiddenInResponses
            .Concat(new[]
            {
                ImageFixtures.BodySerial,
                ImageFixtures.LensSerial,
                ImageFixtures.Software,
            })
            .ToArray();

        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Strip_Idempotent_Second_Call_Reuses_Blob()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var first = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var blobAfterFirst = await InDbAsync(db => db.FileItems.AsNoTracking()
            .Where(f => f.Id == file.Id).Select(f => f.BlobObjectId).SingleAsync());
        var blobCountAfterFirst = await InDbAsync(db => db.BlobObjects.AsNoTracking().CountAsync());

        var second = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var blobAfterSecond = await InDbAsync(db => db.FileItems.AsNoTracking()
            .Where(f => f.Id == file.Id).Select(f => f.BlobObjectId).SingleAsync());
        var blobCountAfterSecond = await InDbAsync(db => db.BlobObjects.AsNoTracking().CountAsync());

        // Re-stripping the same image must NOT create another BlobObject row,
        // and the FileItem keeps pointing at the same stripped blob.
        Assert.Equal(blobAfterFirst, blobAfterSecond);
        Assert.Equal(blobCountAfterFirst, blobCountAfterSecond);
    }

    [Fact]
    public async Task Strip_Old_Blob_Reclaimable_By_BlobJanitor_When_Unreferenced()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");
        var oldBlobId = file.BlobObjectId;

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        // Backdate the old blob's CreatedAt so it falls past the janitor's
        // grace window, then run the janitor once.
        var ancient = DateTime.UtcNow.AddDays(-30);
        await InDbAsync(async db =>
        {
            return await db.BlobObjects
                .Where(b => b.Id == oldBlobId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedAt, _ => ancient));
        });

        // The factory's BlobJanitor is registered with the default
        // Enabled=false. Construct one explicitly with Enabled=true so the
        // single RunOnceAsync call actually executes.
        var janitor = new BlobJanitor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BlobJanitorOptions { Enabled = true, GraceMinutes = 0 }),
            TimeProvider.System,
            NullLogger<BlobJanitor>.Instance);
        await janitor.RunOnceAsync(CancellationToken.None);

        var stillExists = await InDbAsync(db => db.BlobObjects.AsNoTracking()
            .AnyAsync(b => b.Id == oldBlobId));
        Assert.False(stillExists);
    }

    [Fact]
    public async Task Strip_Regenerates_Thumbnail_From_New_Bytes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");

        var beforeThumb = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id));

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        // A single thumbnail row still exists, with a NEW row id and (likely)
        // a different thumbnail blob since the source bytes changed.
        var afterThumb = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id));

        Assert.NotEqual(beforeThumb.Id, afterThumb.Id);

        // The thumbnail endpoint serves the regenerated bytes.
        var thumbResponse = await client.GetAsync(
            $"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, thumbResponse.StatusCode);
    }

    [Fact]
    public async Task Strip_Updates_FileSize_From_New_Bytes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "photo.jpg", "image/jpeg");
        var originalSize = file.SizeBytes;

        var strip = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/strip-embedded", content: null);
        Assert.Equal(HttpStatusCode.OK, strip.StatusCode);

        var fileNow = await InDbAsync(db => db.FileItems.AsNoTracking()
            .SingleAsync(f => f.Id == file.Id));
        Assert.NotEqual(originalSize, fileNow.SizeBytes);
        Assert.True(fileNow.SizeBytes > 0);
    }
}

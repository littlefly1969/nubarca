using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 54 — embedded image metadata extraction wired through the upload path
// and the owner-scoped metadata endpoint. Verifies exhaustive internal storage
// + curated, privacy-safe DTO exposure.
public sealed class EmbeddedMetadataEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public EmbeddedMetadataEndpointTests()
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

    private static async Task<FileMetadataResponse> GetMetadataAsync(HttpClient client, Guid fileId)
    {
        var response = await client.GetAsync($"/api/files/{fileId}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileMetadataResponse>())!;
    }

    [Fact]
    public async Task Upload_Jpeg_Exposes_Curated_Embedded_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Equal(MetadataStatuses.Completed, meta.Blob.ExtractionStatus);
        Assert.NotNull(meta.Blob.Embedded);
        var e = meta.Blob.Embedded!;
        Assert.Equal(new DateTime(2023, 6, 15, 14, 30, 0, DateTimeKind.Utc), e.DateTaken);
        Assert.Equal("DateTimeOriginal", e.DateTakenSource);
        Assert.Equal(6, e.Orientation);
        Assert.Equal(ImageFixtures.CameraMake, e.CameraMake);
        Assert.Equal(ImageFixtures.CameraModel, e.CameraModel);
        Assert.Equal(ImageFixtures.LensModel, e.LensModel);
        Assert.Equal(400, e.Iso);
        Assert.NotNull(e.Aperture);
        Assert.Equal(2.8, e.Aperture!.Value, precision: 2);
        Assert.Contains("1/250", e.ExposureTime!);
        Assert.Equal(50.0, e.FocalLength!.Value, precision: 2);
        Assert.Equal("sRGB", e.ColorSpace);
        Assert.False(e.HasGps); // this fixture has no GPS
    }

    [Fact]
    public async Task Gps_Image_Reports_HasGps_But_Never_Coordinates()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        var meta = await GetMetadataAsync(client, file.Id);
        Assert.True(meta.Blob.Embedded!.HasGps);

        // Internally, the coordinates + serials ARE stored on the blob row.
        var row = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.NotNull(row.GpsLatitude);
        Assert.NotNull(row.GpsLongitude);
        Assert.Equal(ImageFixtures.BodySerial, row.BodySerialNumber);
        Assert.NotNull(row.RawMetadataJson);
    }

    [Fact]
    public async Task Metadata_Response_Does_Not_Leak_Sensitive_Or_Internal_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(includeGps: true), "leak.jpg", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        // Sensitive embedded fields + the raw document must not appear.
        var forbidden = new[]
        {
            ImageFixtures.BodySerial, ImageFixtures.LensSerial,
            ImageFixtures.Software, // software tag is withheld until privacy slice
            "gpsLatitude", "GpsLatitude", "gps_latitude",
            "gpsLongitude", "GpsLongitude",
            "bodySerialNumber", "BodySerialNumber",
            "lensSerialNumber", "LensSerialNumber",
            "rawMetadataJson", "RawMetadataJson", "raw_metadata_json",
            "software", "Software",
            // storage internals
            "StorageKey", "storageKey", "BlobObjectId", "blobObjectId",
            "OwnerUserId", "ownerUserId", "Sha256", "sha256", "objects/",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Corrupt_Exif_Upload_Succeeds_And_Metadata_Is_Readable()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        // Must not throw on upload.
        var file = await UploadAsync(owner, ImageFixtures.JpegWithCorruptExif(), "corrupt.jpg", "image/jpeg");

        // Endpoint must respond (no 500), with a non-fatal extraction status.
        var meta = await GetMetadataAsync(client, file.Id);
        Assert.Contains(meta.Blob.ExtractionStatus, new[]
        {
            MetadataStatuses.Completed, MetadataStatuses.Failed, MetadataStatuses.Skipped,
        });
    }

    [Fact]
    public async Task Extraction_Version_And_Timestamp_Are_Recorded()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "v.jpg", "image/jpeg");

        var row = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(EmbeddedImageMetadataExtractor.Version, row.ExtractionVersion);
        Assert.NotNull(row.ExtractedAt);
    }

    [Fact]
    public async Task Deduped_Uploads_Share_Embedded_Metadata()
    {
        var bytes = ImageFixtures.JpegWithExif();

        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, bytes, "a.jpg", "image/jpeg");

        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");
        var bobFile = await UploadAsync(bob, bytes, "b.jpg", "image/jpeg");

        Assert.Equal(aliceFile.BlobObjectId, bobFile.BlobObjectId);
        Assert.Equal(1, await InDbAsync(db => db.BlobMetadata.CountAsync()));

        var aliceMeta = await GetMetadataAsync(aliceClient, aliceFile.Id);
        var bobMeta = await GetMetadataAsync(bobClient, bobFile.Id);
        Assert.Equal(aliceMeta.Blob.Embedded!.DateTaken, bobMeta.Blob.Embedded!.DateTaken);
        Assert.Equal(aliceMeta.Blob.Embedded!.CameraModel, bobMeta.Blob.Embedded!.CameraModel);
    }

    [Fact]
    public async Task User_Metadata_Edit_Does_Not_Change_Embedded_Blob_Metadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "edit.jpg", "image/jpeg");

        var before = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));

        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new { title = "My Photo", favorite = true, tags = new[] { "trip" } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var after = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(before.DateTaken, after.DateTaken);
        Assert.Equal(before.CameraModel, after.CameraModel);
        Assert.Equal(before.GpsLatitude, after.GpsLatitude);
        Assert.Equal(before.RawMetadataJson, after.RawMetadataJson);
    }

    [Fact]
    public async Task Rename_And_Move_Do_Not_Change_Embedded_Blob_Metadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "orig.jpg", "image/jpeg");

        var before = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));

        var rename = await client.PatchAsJsonAsync($"/api/files/{file.Id}/rename", new { name = "renamed.jpg" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var folder = await client.PostAsJsonAsync("/api/folders", new { name = "trip" });
        var folderId = (await folder.Content.ReadFromJsonAsync<FolderIdProbe>())!.Id;
        var move = await client.PatchAsJsonAsync($"/api/files/{file.Id}/move", new { parentFolderId = folderId });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        var afterFile = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id));
        Assert.Equal(file.BlobObjectId, afterFile.BlobObjectId);

        var after = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.DateTaken, after.DateTaken);
        Assert.Equal(before.CameraModel, after.CameraModel);
        Assert.Equal(before.Orientation, after.Orientation);
    }

    [Fact]
    public async Task Non_Image_Upload_Skips_Extraction_With_No_Embedded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "plain notes"u8.ToArray(), "notes.txt", "text/plain");

        var meta = await GetMetadataAsync(client, file.Id);
        Assert.Equal(MetadataStatuses.Skipped, meta.Blob.ExtractionStatus);
        Assert.Null(meta.Blob.Embedded);

        var row = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Null(row.RawMetadataJson);
        Assert.Null(row.CameraModel);
    }

    [Fact]
    public async Task ReExtract_Repopulates_Embedded_Fields_For_One_Blob()
    {
        // Slice-55 prep hook: clear the embedded fields then re-extract.
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "re.jpg", "image/jpeg");

        await InDbAsync(async db =>
        {
            await db.BlobMetadata
                .Where(m => m.BlobObjectId == file.BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.CameraModel, _ => (string?)null)
                    .SetProperty(m => m.DateTaken, _ => (DateTime?)null)
                    .SetProperty(m => m.ExtractionStatus, _ => MetadataStatuses.Pending));
            return 0;
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var files = (FileItemService)scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var ok = await files.ReExtractEmbeddedMetadataAsync(file.BlobObjectId);
            Assert.True(ok);
        }

        var row = await InDbAsync(db => db.BlobMetadata
            .AsNoTracking().SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(MetadataStatuses.Completed, row.ExtractionStatus);
        Assert.Equal(ImageFixtures.CameraModel, row.CameraModel);
        Assert.NotNull(row.DateTaken);
    }

    private sealed record FolderIdProbe(Guid Id);
}

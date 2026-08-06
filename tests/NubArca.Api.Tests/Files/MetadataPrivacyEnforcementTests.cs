using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 57 — end-to-end enforcement of MetadataExposurePolicy. Uploads an
// image whose blob carries GPS coordinates, body/lens serial numbers, and a
// software tag, then asserts that NONE of those fields appear in any of the
// owner / share / public-download / admin / gallery / file-listing / search
// responses. The owner CAN see curated camera/lens model + a HasGps boolean,
// per MetadataAudience.Owner.
public sealed class MetadataPrivacyEnforcementTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MetadataPrivacyEnforcementTests()
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

    // Single needle list shared across every assertion below. Per-test extras
    // (literal serial strings from ImageFixtures) are added per-call.
    private static IEnumerable<string> ForbiddenIn(MetadataAudience audience)
    {
        // Owner is allowed to see curated camera make/model/lens model and a
        // HasGps boolean, but never sensitive embedded fields or storage
        // internals. Other audiences are even more restrictive — both lists
        // collapse onto MetadataExposurePolicy.ForbiddenInResponses.
        _ = audience; // current rule is identical across audiences below
        return MetadataExposurePolicy.ForbiddenInResponses;
    }

    private static async Task AssertNoForbiddenAsync(
        HttpResponseMessage response, MetadataAudience audience, params string[] extra)
    {
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        foreach (var needle in ForbiddenIn(audience).Concat(extra))
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Owner_Metadata_Endpoint_Exposes_HasGps_But_Not_Coordinates_Serials_Software()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        // Internally the row carries the sensitive fields.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.BlobMetadata.AsNoTracking()
                .SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
            Assert.NotNull(row.GpsLatitude);
            Assert.NotNull(row.GpsLongitude);
            Assert.Equal(ImageFixtures.BodySerial, row.BodySerialNumber);
            Assert.Equal(ImageFixtures.LensSerial, row.LensSerialNumber);
            Assert.Equal(ImageFixtures.Software, row.Software);
        }

        // The owner DTO exposes a HasGps boolean and curated camera fields…
        var response = await client.GetAsync($"/api/files/{file.Id}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var meta = (await response.Content.ReadFromJsonAsync<FileMetadataResponse>())!;
        Assert.NotNull(meta.Blob.Embedded);
        Assert.True(meta.Blob.Embedded!.HasGps);
        Assert.Equal(ImageFixtures.CameraMake, meta.Blob.Embedded.CameraMake);
        Assert.Equal(ImageFixtures.CameraModel, meta.Blob.Embedded.CameraModel);

        // …but not the sensitive fields nor any storage internals nor the
        // literal serial / software strings.
        await AssertNoForbiddenAsync(
            response, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software);
    }

    [Fact]
    public async Task Owner_Metadata_Endpoint_Allows_Manual_Location_Override_But_Not_Embedded_Gps()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        // Owner manually sets a location. This text is fully owner-controlled
        // and goes into UserMetadataView.LocationOverride; it is NOT promoted
        // from embedded GPS coordinates.
        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata", new { locationOverride = "Backyard" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var response = await client.GetAsync($"/api/files/{file.Id}/metadata");
        var meta = (await response.Content.ReadFromJsonAsync<FileMetadataResponse>())!;
        Assert.Equal("Backyard", meta.User.LocationOverride);
        Assert.Equal("Backyard", meta.Effective.Location);
        Assert.True(meta.Blob.Embedded!.HasGps);
        // Embedded GPS coordinates are NOT promoted into effective location.
        Assert.DoesNotContain("51", meta.Effective.Location, StringComparison.Ordinal);

        await AssertNoForbiddenAsync(
            response, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software);
    }

    [Fact]
    public async Task Public_Share_Download_Does_Not_Expose_Metadata_Through_Envelope()
    {
        // Public share download serves the original file bytes. The bytes
        // themselves may contain embedded EXIF — that is documented behaviour
        // (ShareLinkBytesIncludeEmbeddedMetadata = true). What this test
        // covers is the HTTP envelope: status, headers, and any metadata
        // JSON envelope MUST NOT carry sensitive field names. The body itself
        // is the raw image bytes; we don't search those.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        var created = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        var link = (await created.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var downloadResponse = await anonymous.GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        // No metadata JSON envelope here — Results.File is a stream — but the
        // headers must not leak field names either.
        var headers = string.Join("\n",
            downloadResponse.Headers.Concat(downloadResponse.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
        // The download filename comes from FileItem.Name only, so the literal
        // serial / software / GPS strings must not appear in headers either.
        Assert.DoesNotContain(ImageFixtures.BodySerial, headers, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.LensSerial, headers, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.Software, headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_Link_Listing_Does_Not_Expose_Embedded_Metadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        await client.PostAsJsonAsync($"/api/files/{file.Id}/share-links", new { });

        // Both the per-file listing and the global owner-scoped listing.
        var perFile = await client.GetAsync($"/api/files/{file.Id}/share-links");
        Assert.Equal(HttpStatusCode.OK, perFile.StatusCode);
        await AssertNoForbiddenAsync(
            perFile, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "Exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");

        var global = await client.GetAsync("/api/share-links");
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
        await AssertNoForbiddenAsync(
            global, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "Exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");
    }

    [Fact]
    public async Task Gallery_Listing_Does_Not_Expose_Embedded_Or_Sensitive_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "owner.jpg", "image/jpeg");

        var response = await client.GetAsync("/api/images");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertNoForbiddenAsync(
            response, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");
    }

    [Fact]
    public async Task Folder_Listing_And_Search_Do_Not_Expose_Embedded_Or_Sensitive_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "owner.jpg", "image/jpeg");

        var children = await client.GetAsync("/api/folders/children");
        Assert.Equal(HttpStatusCode.OK, children.StatusCode);
        await AssertNoForbiddenAsync(
            children, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");

        var search = await client.GetAsync("/api/search?q=owner");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        await AssertNoForbiddenAsync(
            search, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");
    }

    [Fact]
    public async Task Trash_Listing_Does_Not_Expose_Embedded_Or_Sensitive_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "trash.jpg", "image/jpeg");

        var delete = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var trash = await client.GetAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, trash.StatusCode);
        await AssertNoForbiddenAsync(
            trash, MetadataAudience.Owner,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "exif", "EXIF",
            "cameraModel", "CameraModel",
            "dateTaken", "DateTaken");
    }

    [Fact]
    public async Task Admin_Storage_Stats_Are_Aggregate_Only_And_Carry_No_Per_File_Data()
    {
        var ownerId = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(ownerId);
        var client = await _factory.LoginAsync("admin@example.com");

        // Seed something so the counters are non-zero.
        await UploadAsync(
            ownerId, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        var response = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await AssertNoForbiddenAsync(
            response, MetadataAudience.AdminAggregate,
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            "geo.jpg",                                  // no per-file name
            ownerId.ToString());                        // no per-user id

        // Defence-in-depth: an admin stats DTO must not start carrying GUID
        // identifiers in a future slice without an explicit policy update.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\":", body, StringComparison.Ordinal);
    }
}

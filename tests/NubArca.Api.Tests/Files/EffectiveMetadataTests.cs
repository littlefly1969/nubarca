using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 56 — effective metadata layering, audit on metadata edit, and the
// gallery DateTaken sort honouring the user override.
public sealed class EffectiveMetadataTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public EffectiveMetadataTests()
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

    private async Task<FileItem> UploadAsync(Guid ownerId, string name, int dim = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, "image/png", new MemoryStream(Png(dim)));
    }

    private async Task SetEmbeddedDateAsync(Guid blobObjectId, DateTime? dateTaken)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == blobObjectId);
        meta.DateTaken = dateTaken;

        // Slice 88: mutating the embedded date directly bypasses the service
        // re-extraction path, so refresh the denormalized EffectiveDateTaken for
        // active files on this blob that aren't pinned by a user override —
        // exactly what RecomputeEffectiveDatesForBlobAsync does in production.
        var files = await db.FileItems
            .Where(f => f.BlobObjectId == blobObjectId && f.DeletedAt == null)
            .ToListAsync();
        foreach (var f in files)
        {
            var pinned = await db.FileItemUserMetadata
                .AnyAsync(u => u.FileItemId == f.Id && u.DateTakenOverride != null);
            if (pinned) continue;
            var (eff, src) = EffectiveDateTakenSources.Compute(null, dateTaken, f.CreatedAt);
            f.EffectiveDateTaken = eff;
            f.EffectiveDateTakenSource = src;
        }
        await db.SaveChangesAsync();
    }

    private async Task<FileMetadataResponse> GetMetadataAsync(HttpClient client, Guid fileId)
    {
        var response = await client.GetAsync($"/api/files/{fileId}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileMetadataResponse>())!;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // ---- effective DateTaken precedence ------------------------------------

    [Fact]
    public async Task Effective_DateTaken_Uses_Upload_Time_When_No_Override_And_No_Embedded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Equal(EffectiveDateTakenSources.Uploaded, meta.Effective.DateTakenSource);
        Assert.Equal(file.CreatedAt, meta.Effective.DateTaken);
    }

    [Fact]
    public async Task Effective_DateTaken_Prefers_Embedded_Over_Uploaded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");
        var embedded = Utc(2023, 6, 15);
        await SetEmbeddedDateAsync(file.BlobObjectId, embedded);

        var meta = await GetMetadataAsync(client, file.Id);

        Assert.Equal(EffectiveDateTakenSources.Embedded, meta.Effective.DateTakenSource);
        Assert.Equal(embedded, meta.Effective.DateTaken);
    }

    [Fact]
    public async Task Effective_DateTaken_Prefers_User_Override_Over_Embedded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");
        await SetEmbeddedDateAsync(file.BlobObjectId, Utc(2023, 6, 15));

        var userOverride = Utc(2019, 1, 1);
        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new { dateTakenOverride = userOverride });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var meta = await GetMetadataAsync(client, file.Id);
        Assert.Equal(EffectiveDateTakenSources.User, meta.Effective.DateTakenSource);
        Assert.Equal(userOverride, meta.Effective.DateTaken);
    }

    // ---- effective DisplayName + Location ----------------------------------

    [Fact]
    public async Task Effective_DisplayName_Uses_User_Title_When_Set_Else_File_Name()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(await _factory.SeedUserAsync("u@example.com") , "doc.png");

        // No title yet → DisplayName falls back to the file name.
        // (Use the same owner we logged in with via the helper.)
        var (owner2, client2) = await _factory.CreateAuthenticatedClientAsync("o2@example.com");
        var file2 = await UploadAsync(owner2, "raw-name.png");

        var before = await GetMetadataAsync(client2, file2.Id);
        Assert.Equal("raw-name.png", before.Effective.DisplayName);

        var patch = await client2.PatchAsJsonAsync(
            $"/api/files/{file2.Id}/metadata",
            new { title = "Friendly title" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var after = await GetMetadataAsync(client2, file2.Id);
        Assert.Equal("Friendly title", after.Effective.DisplayName);
    }

    [Fact]
    public async Task Effective_Location_Uses_User_Override_Only_Never_Embedded_GPS()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");

        // Simulate an embedded GPS coordinate on the shared blob — must NOT
        // leak into Effective.Location, no matter what the user does.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobMeta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
            blobMeta.GpsLatitude = 51.5074;
            blobMeta.GpsLongitude = -0.1278;
            await db.SaveChangesAsync();
        }

        var before = await GetMetadataAsync(client, file.Id);
        Assert.Null(before.Effective.Location);
        Assert.True(before.Blob.Embedded?.HasGps ?? false);

        await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new { locationOverride = "Sardinia" });

        var after = await GetMetadataAsync(client, file.Id);
        Assert.Equal("Sardinia", after.Effective.Location);
        Assert.True(after.Blob.Embedded!.HasGps);
    }

    // ---- BlobMetadata + blob identity are untouched by user-metadata edits --

    [Fact]
    public async Task User_Metadata_Edit_Does_Not_Mutate_BlobMetadata_Or_Blob_Identity()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");

        BlobMetadata before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            before = await db.BlobMetadata
                .AsNoTracking()
                .SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        }
        var blobIdBefore = file.BlobObjectId;

        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new
            {
                title = "Edited",
                description = "User note",
                tags = new[] { "trip" },
                rating = 5,
                favorite = true,
                dateTakenOverride = Utc(2019, 1, 1),
                locationOverride = "Anywhere",
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fileRow = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id);
            Assert.Equal(blobIdBefore, fileRow.BlobObjectId);

            var after = await db.BlobMetadata
                .AsNoTracking()
                .SingleAsync(m => m.BlobObjectId == blobIdBefore);
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.ExtractionVersion, after.ExtractionVersion);
            Assert.Equal(before.ExtractedAt, after.ExtractedAt);
            Assert.Equal(before.DateTaken, after.DateTaken);
            Assert.Equal(before.GpsLatitude, after.GpsLatitude);
            Assert.Equal(before.RawMetadataJson, after.RawMetadataJson);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        }
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task User_Metadata_Edit_Writes_File_Metadata_Update_Audit_Row()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");

        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata", new { title = "Audited" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == AuditActions.FileMetadataUpdate)
            .SingleAsync();
        Assert.Equal(owner, entry.UserId);
        Assert.Equal(file.Id, entry.EntityId);
    }

    // ---- override drives the gallery DateTaken sort -------------------------

    [Fact]
    public async Task User_DateTaken_Override_Affects_Gallery_DateTaken_Sort()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(owner, "a.png", 10);
        var b = await UploadAsync(owner, "b.png", 11);

        // Without overrides, the embedded dates give the order B → A on asc.
        await SetEmbeddedDateAsync(a.BlobObjectId, Utc(2020, 6, 1));
        await SetEmbeddedDateAsync(b.BlobObjectId, Utc(2020, 1, 1));

        var ascNoOverride = await GalleryOrderAsync(client, "sort=datetaken&direction=asc");
        Assert.Equal(new[] { b.Id, a.Id }, ascNoOverride);

        // Now override A's date to be earlier than B's: asc order flips to A → B.
        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{a.Id}/metadata",
            new { dateTakenOverride = Utc(2019, 1, 1) });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var ascWithOverride = await GalleryOrderAsync(client, "sort=datetaken&direction=asc");
        Assert.Equal(new[] { a.Id, b.Id }, ascWithOverride);
    }

    private static async Task<List<Guid>> GalleryOrderAsync(HttpClient client, string query)
    {
        var resp = await client.GetAsync($"/api/images?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ImageListResponse>();
        return body!.Items.Select(i => i.Id).ToList();
    }

    // ---- no-leak on effective view -----------------------------------------

    [Fact]
    public async Task Effective_Response_Does_Not_Expose_Gps_Coords_Or_Serials_Even_With_Override()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "a.png");

        // Populate sensitive embedded fields + a user location override.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobMeta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
            blobMeta.GpsLatitude = 51.5074;
            blobMeta.GpsLongitude = -0.1278;
            blobMeta.BodySerialNumber = "BODY-SN-SECRET-XYZ";
            blobMeta.LensSerialNumber = "LENS-SN-SECRET-XYZ";
            await db.SaveChangesAsync();
        }

        await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new { locationOverride = "Anywhere", title = "Anything" });

        var response = await client.GetAsync($"/api/files/{file.Id}/metadata");
        var body = await response.Content.ReadAsStringAsync();

        foreach (var needle in new[]
                 {
                     "BODY-SN-SECRET-XYZ", "LENS-SN-SECRET-XYZ",
                     "gpsLatitude", "GpsLatitude", "gpsLongitude", "GpsLongitude",
                     "51.5074", "-0.1278",
                     "rawMetadataJson", "RawMetadataJson",
                     "BlobObjectId", "blobObjectId",
                     "StorageKey", "storageKey",
                     "Sha256", "sha256",
                     "objects/",
                 })
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
        }
    }
}

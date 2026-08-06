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

// Slice 66 — POST /api/files/{id}/metadata/write-datetaken. Strong mutation
// that bakes the user's DateTaken override into the image bytes (JPEG EXIF).
// Same blob-immutable contract as strip-embedded.
public sealed class WriteDateTakenEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public WriteDateTakenEndpointTests()
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

    private static readonly DateTime Override = new(2021, 7, 15, 10, 30, 0, DateTimeKind.Utc);

    private static async Task SetDateOverrideAsync(HttpClient client, Guid fileId, DateTime utc)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/files/{fileId}/metadata", new { dateTakenOverride = utc });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Write_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync(
            $"/api/files/{Guid.NewGuid()}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Write_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync(
            $"/api/files/{Guid.NewGuid()}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Write_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.PostAsync(
            $"/api/files/{aliceFile.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Write_Without_Override_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Write_NonJpeg_Png_Returns_415()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(), "p.png", "image/png");
        await SetDateOverrideAsync(client, file.Id, Override);

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Write_Creates_New_Blob_And_Bakes_DateTaken()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var oldBlobId = file.BlobObjectId;
        await SetDateOverrideAsync(client, file.Id, Override);

        var response = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fileNow = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id));
        Assert.NotEqual(oldBlobId, fileNow.BlobObjectId);

        // The new blob's regenerated BlobMetadata carries the baked-in date.
        var meta = await InDbAsync(db => db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == fileNow.BlobObjectId));
        Assert.NotNull(meta.DateTaken);
        var d = meta.DateTaken!.Value;
        Assert.Equal(Override.Year, d.Year);
        Assert.Equal(Override.Month, d.Month);
        Assert.Equal(Override.Day, d.Day);
        Assert.Equal(Override.Hour, d.Hour);
        Assert.Equal(Override.Minute, d.Minute);
        Assert.Equal(Override.Second, d.Second);
    }

    [Fact]
    public async Task Write_Only_Repoints_Caller_FileItem_Not_Shared_Blob()
    {
        // Two users upload identical bytes → one deduped blob.
        var bytes = ImageFixtures.JpegWithExif();
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");

        var aliceFile = await UploadAsync(alice, bytes, "a.jpg", "image/jpeg");
        var bobFile = await UploadAsync(bob, bytes, "b.jpg", "image/jpeg");
        Assert.Equal(aliceFile.BlobObjectId, bobFile.BlobObjectId);
        var sharedBlobId = aliceFile.BlobObjectId;

        await SetDateOverrideAsync(aliceClient, aliceFile.Id, Override);
        var resp = await aliceClient.PostAsync(
            $"/api/files/{aliceFile.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var aliceNow = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == aliceFile.Id));
        var bobNow = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == bobFile.Id));
        Assert.NotEqual(sharedBlobId, aliceNow.BlobObjectId); // alice repointed
        Assert.Equal(sharedBlobId, bobNow.BlobObjectId);      // bob untouched
    }

    [Fact]
    public async Task Write_Preserves_User_Metadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        var patch = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/metadata",
            new
            {
                title = "Keeper",
                tags = new[] { "trip" },
                rating = 5,
                favorite = true,
                dateTakenOverride = Override,
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var resp = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var meta = await client.GetFromJsonAsync<FileMetadataResponse>(
            $"/api/files/{file.Id}/metadata");
        Assert.Equal("Keeper", meta!.User.Title);
        Assert.Equal(new[] { "trip" }, meta.User.Tags.ToArray());
        Assert.Equal(5, meta.User.Rating);
        Assert.True(meta.User.Favorite);
    }

    [Fact]
    public async Task Write_Writes_Audit_Row()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        await SetDateOverrideAsync(client, file.Id, Override);

        var resp = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var count = await InDbAsync(db => db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == "file.metadata_write_datetaken" && a.EntityId == file.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Write_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(includeGps: true), "p.jpg", "image/jpeg");
        await SetDateOverrideAsync(client, file.Id, Override);

        var resp = await client.PostAsync(
            $"/api/files/{file.Id}/metadata/write-datetaken", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
        }
    }
}

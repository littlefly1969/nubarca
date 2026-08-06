using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 78 — Kestrel/FormOptions upload limit configuration.
// These tests verify configuration behaviour (defaults + overrides) and that
// existing upload semantics (app-layer 413, quota 413, conflict 409) are
// preserved after the Kestrel + FormOptions wiring was added.
public sealed class UploadSizeLimitTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public UploadSizeLimitTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent Multipart(
        byte[] payload, string filename = "f.txt", string contentType = "text/plain")
    {
        var m = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        m.Add(part, "file", filename);
        return m;
    }

    private async Task<int> FileItemCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.CountAsync();
    }

    // ---- UploadOptions defaults ----

    [Fact]
    public void UploadOptions_Defaults_Are_10_GiB()
    {
        var opts = new UploadOptions();
        Assert.Equal(10L * 1024 * 1024 * 1024, opts.MaxFileSizeBytes);
        Assert.Equal(10L * 1024 * 1024 * 1024, opts.MaxRequestBodySizeBytes);
    }

    [Fact]
    public void UploadOptions_SectionName_Is_Uploads()
    {
        Assert.Equal("Uploads", UploadOptions.SectionName);
    }

    // ---- Small uploads still work ----

    [Fact]
    public async Task Small_Upload_Succeeds_And_Creates_FileItem()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("small-up@example.com");
        var resp = await client.PostAsync("/api/files", Multipart("hello world"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(1, await FileItemCountAsync());
    }

    [Fact]
    public async Task Small_Folder_Upload_With_RelativePath_Succeeds()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("small-folder@example.com");
        var m = Multipart("data"u8.ToArray(), "sub/photo.jpg");
        m.Add(new StringContent("dir/sub/photo.jpg"), "relativePath");
        var resp = await client.PostAsync("/api/files", m);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(1, await FileItemCountAsync());
    }

    // ---- App-layer upload ceiling (Storage:MaxUploadBytes) ----
    // This path is already covered by StorageQuotaTests but we verify the
    // message contract here too: a 413 must carry an {error:...} JSON body
    // so the frontend displays a readable message rather than a raw code.

    [Fact]
    public async Task App_Layer_413_Response_Has_Json_Error_Body()
    {
        var smallLimitFactory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Storage:MaxUploadBytes"] = "10" });
        smallLimitFactory.EnsureDatabaseCreated();
        try
        {
            var userId = await smallLimitFactory.SeedUserAsync("app-413@example.com");
            var client = await smallLimitFactory.LoginAsync("app-413@example.com");

            var resp = await client.PostAsync("/api/files",
                Multipart("this is longer than 10 bytes"u8.ToArray()));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.NotNull(body);
            Assert.True(body!.ContainsKey("error"));
            Assert.False(string.IsNullOrEmpty(body["error"]));

            // No FileItem must have been created.
            using var scope = smallLimitFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.FileItems.CountAsync());
        }
        finally
        {
            smallLimitFactory.Dispose();
        }
    }

    // ---- Conflict still returns 409 ----

    [Fact]
    public async Task Duplicate_Name_Returns_409_Not_413()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("dup-413@example.com");
        await client.PostAsync("/api/files", Multipart("a"u8.ToArray(), "same.txt"));
        var second = await client.PostAsync("/api/files", Multipart("b"u8.ToArray(), "same.txt"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // ---- Blob dedup preserved ----

    [Fact]
    public async Task Identical_Bytes_Still_Dedup_To_One_Blob()
    {
        var bytes = "dedup-content"u8.ToArray();
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("dedup-ul@example.com");
        await client.PostAsync("/api/files", Multipart(bytes, "a.txt"));
        await client.PostAsync("/api/files", Multipart(bytes, "b.txt"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.FileItems.CountAsync());
        Assert.Equal(1, await db.BlobObjects.CountAsync());
    }
}

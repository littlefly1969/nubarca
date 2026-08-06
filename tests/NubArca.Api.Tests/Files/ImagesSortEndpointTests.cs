using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// Tests for the `sort` and `direction` query parameters on GET /api/images.
public sealed class ImagesSortEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImagesSortEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Larger size = more rows of padding. Each call produces a different-sized
    // PNG so we can sort by `SizeBytes` deterministically.
    private static byte[] PngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string filename)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", filename);
        return multipart;
    }

    private async Task<Guid> UploadImageAsync(HttpClient client, string name, int dim, Guid? folderId = null)
    {
        var url = folderId is null ? "/api/files" : $"/api/folders/{folderId}/files";
        var resp = await client.PostAsync(url, Multipart(PngBytes(dim, dim), name));
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task<Folder> SeedFolderAsAsync(Guid ownerId, string name = "Photos")
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, null, name);
    }

    // Three files uploaded in alphabetical order, each with monotonically
    // increasing dimensions so PNG byte-size grows too. CreatedAt order
    // matches upload order; Name order matches upload order; Size order
    // matches upload order. So:
    //   created asc:  a  → b → c
    //   created desc: c  → b → a   (the existing default)
    //   name asc:     a  → b → c
    //   name desc:    c  → b → a
    //   size asc:     a  → b → c
    //   size desc:    c  → b → a
    // Using the same a/b/c shape across the three axes lets each test
    // assert on a small, readable expected sequence.
    private async Task<(Guid a, Guid b, Guid c)> SeedThreeImagesAsync(HttpClient client)
    {
        var a = await UploadImageAsync(client, "a.png", dim: 50);
        var b = await UploadImageAsync(client, "b.png", dim: 100);
        var c = await UploadImageAsync(client, "c.png", dim: 200);
        return (a, b, c);
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.NotNull(body);
        return body!.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task Default_Ordering_Without_Sort_Is_Unchanged()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { c, b, a }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Created_Asc_Returns_Oldest_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=created&direction=asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { a, b, c }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Created_Desc_Returns_Newest_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=created&direction=desc");
        Assert.Equal(new[] { c, b, a }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Name_Asc_Returns_Alphabetical()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=name&direction=asc");
        Assert.Equal(new[] { a, b, c }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Name_Desc_Returns_Reverse_Alphabetical()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=name&direction=desc");
        Assert.Equal(new[] { c, b, a }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Size_Asc_Returns_Smallest_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=size&direction=asc");
        Assert.Equal(new[] { a, b, c }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Size_Desc_Returns_Largest_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=size&direction=desc");
        Assert.Equal(new[] { c, b, a }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Defaults_To_Desc_When_Only_Sort_Provided()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=name");
        Assert.Equal(new[] { c, b, a }, await IdsAsync(response));
    }

    [Fact]
    public async Task Sort_Direction_Is_Case_Insensitive()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var (a, b, c) = await SeedThreeImagesAsync(client);

        var response = await client.GetAsync("/api/images?sort=NAME&direction=ASC");
        Assert.Equal(new[] { a, b, c }, await IdsAsync(response));
    }

    [Fact]
    public async Task Invalid_Sort_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/images?sort=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Direction_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/images?direction=sideways");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sort_Composes_With_Q_Filter()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "alpha-sunset.png", dim: 50);
        await UploadImageAsync(client, "beta-sunset.png", dim: 100);
        await UploadImageAsync(client, "gamma-other.png", dim: 200);

        var response = await client.GetAsync("/api/images?q=sunset&sort=name&direction=asc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        Assert.Equal("alpha-sunset.png", body.Items[0].Name);
        Assert.Equal("beta-sunset.png", body.Items[1].Name);
    }

    [Fact]
    public async Task Sort_Composes_With_FolderId()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, "Photos");

        await UploadImageAsync(client, "a.png", dim: 50, folderId: folder.Id);
        await UploadImageAsync(client, "b.png", dim: 100, folderId: folder.Id);
        await UploadImageAsync(client, "c.png", dim: 200, folderId: folder.Id);
        // Outside the folder, should be ignored.
        await UploadImageAsync(client, "z-outside.png", dim: 300);

        var response = await client.GetAsync(
            $"/api/images?folderId={folder.Id}&sort=size&direction=desc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.NotNull(body);
        Assert.Equal(3, body!.Items.Count);
        Assert.Equal("c.png", body.Items[0].Name);
        Assert.Equal("b.png", body.Items[1].Name);
        Assert.Equal("a.png", body.Items[2].Name);
    }

    [Fact]
    public async Task Sort_Composes_With_Limit_And_Offset_Without_Duplicates()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 6; i++)
        {
            await UploadImageAsync(client, $"img-{i}.png", dim: 50 + i * 10);
        }

        var page1 = await client.GetAsync("/api/images?sort=name&direction=asc&limit=2&offset=0");
        var page2 = await client.GetAsync("/api/images?sort=name&direction=asc&limit=2&offset=2");
        var page3 = await client.GetAsync("/api/images?sort=name&direction=asc&limit=2&offset=4");

        var ids1 = await IdsAsync(page1);
        var ids2 = await IdsAsync(page2);
        var ids3 = await IdsAsync(page3);

        var all = ids1.Concat(ids2).Concat(ids3).ToList();
        Assert.Equal(6, all.Count);
        Assert.Equal(6, all.Distinct().Count());
    }

    [Fact]
    public async Task Sort_By_Name_With_Identical_Names_Uses_Id_Tiebreaker_Deterministically()
    {
        // Same-name conflict can't happen in the active sibling set because of
        // the filtered unique index. We simulate "identical sort key" by
        // sorting on a property (Size) that's identical across two files
        // (same content, same dim). Pagination must still be deterministic.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            // Different name but identical dimension → identical PNG byte size.
            ids.Add(await UploadImageAsync(client, $"same-size-{i}.png", dim: 64));
        }

        var page1 = await client.GetAsync("/api/images?sort=size&direction=asc&limit=2&offset=0");
        var page2 = await client.GetAsync("/api/images?sort=size&direction=asc&limit=2&offset=2");

        var p1 = await IdsAsync(page1);
        var p2 = await IdsAsync(page2);
        var combined = p1.Concat(p2).ToList();
        Assert.Equal(4, combined.Count);
        Assert.Equal(4, combined.Distinct().Count());

        // Repeating the same call produces the same order — proves determinism.
        var repeat = await client.GetAsync("/api/images?sort=size&direction=asc&limit=4&offset=0");
        Assert.Equal(combined, await IdsAsync(repeat));
    }

    [Fact]
    public async Task Sort_Response_Has_No_Storage_Internals_Leak()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "a.png", dim: 50);

        var response = await client.GetAsync("/api/images?sort=name&direction=desc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId",
            "DeletedAt", "deletedAt",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, body);
        }
    }
}

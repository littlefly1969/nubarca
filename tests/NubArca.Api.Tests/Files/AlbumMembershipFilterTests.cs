using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// `albumMembership=any|assigned|unassigned` on the photo and video galleries:
// "is this file in SOME album", as opposed to `albumId=<guid>` which means "is
// it in THIS album". Owner scoping, cursor-fingerprint participation and the
// rejection of the contradictory combination are pinned here.
public sealed class AlbumMembershipFilterTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AlbumMembershipFilterTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] PngBytes(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<Guid> UploadImageAsync(HttpClient client, string name, int dim = 48)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(PngBytes(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", name);
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private async Task<Guid> UploadVideoAsync(Guid ownerId, string name, string signature)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var file = await files.CreateAsync(
            ownerId, null, name, "video/mp4", new MemoryStream(ImageFixtures.MinimalMp4(signature)));
        return file.Id;
    }

    private async Task<Guid> CreateAlbumAsync(Guid ownerId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var albums = scope.ServiceProvider.GetRequiredService<IAlbumService>();
        var album = await albums.CreateAsync(ownerId, name, null);
        return album.Id;
    }

    private async Task AddToAlbumAsync(Guid albumId, params Guid[] fileIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // SHARE-ALBUM-02: album_items carries provenance with an FK to users, so
        // a hand-built row needs a real adder. These fixtures are owner-added
        // by construction, which is what the album's own owner id means here.
        var addedBy = await db.Albums
            .Where(a => a.Id == albumId)
            .Select(a => a.OwnerUserId)
            .FirstAsync();
        foreach (var fileId in fileIds)
        {
            db.AlbumItems.Add(new AlbumItem
            {
                // SHARE-ALBUM-03: Id is an alternate key, so hand-built rows
                // need real values or the second one collides.
                Id = Guid.NewGuid(),
                AlbumId = albumId,
                FileItemId = fileId,
                AddedAt = DateTime.UtcNow,
                AddedByUserId = addedBy,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<List<Guid>> ImageIdsAsync(HttpClient client, string url)
    {
        var body = await client.GetFromJsonAsync<ImageListResponse>(url);
        Assert.NotNull(body);
        return body!.Items.Select(i => i.Id).ToList();
    }

    // ------------------------------------------------------------------ photos

    [Fact]
    public async Task Any_Is_The_Default_And_Returns_Everything()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var inAlbum = await UploadImageAsync(client, "in.png", 40);
        var loose = await UploadImageAsync(client, "loose.png", 41);
        var album = await CreateAlbumAsync(owner, "Trip");
        await AddToAlbumAsync(album, inAlbum);

        var implicitAny = await ImageIdsAsync(client, "/api/images");
        var explicitAny = await ImageIdsAsync(client, "/api/images?albumMembership=any");

        Assert.Equal(2, implicitAny.Count);
        Assert.Equal(implicitAny.OrderBy(x => x), explicitAny.OrderBy(x => x));
        Assert.Contains(inAlbum, implicitAny);
        Assert.Contains(loose, implicitAny);
    }

    [Fact]
    public async Task Assigned_Returns_Only_Files_In_At_Least_One_Album()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var inAlbum = await UploadImageAsync(client, "in.png", 40);
        await UploadImageAsync(client, "loose.png", 41);
        var album = await CreateAlbumAsync(owner, "Trip");
        await AddToAlbumAsync(album, inAlbum);

        var ids = await ImageIdsAsync(client, "/api/images?albumMembership=assigned");
        Assert.Equal(inAlbum, Assert.Single(ids));
    }

    [Fact]
    public async Task Unassigned_Returns_Only_Files_In_No_Album()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var inAlbum = await UploadImageAsync(client, "in.png", 40);
        var loose = await UploadImageAsync(client, "loose.png", 41);
        var album = await CreateAlbumAsync(owner, "Trip");
        await AddToAlbumAsync(album, inAlbum);

        var ids = await ImageIdsAsync(client, "/api/images?albumMembership=unassigned");
        Assert.Equal(loose, Assert.Single(ids));
    }

    [Fact]
    public async Task A_File_In_Several_Albums_Appears_Once_Under_Assigned()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadImageAsync(client, "shared.png", 40);
        var a = await CreateAlbumAsync(owner, "A");
        var b = await CreateAlbumAsync(owner, "B");
        await AddToAlbumAsync(a, file);
        await AddToAlbumAsync(b, file);

        var ids = await ImageIdsAsync(client, "/api/images?albumMembership=assigned");
        // EXISTS, not a join — multiple memberships must not duplicate the row.
        Assert.Equal(file, Assert.Single(ids));
    }

    // Another owner's album cannot pull this owner's file into `assigned`,
    // because album_items only ever reference the album owner's own FileItems
    // and the gallery is owner-scoped before the membership predicate runs.
    [Fact]
    public async Task Another_Owners_Album_Does_Not_Affect_This_Owners_Membership()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var mine = await UploadImageAsync(client, "mine.png", 40);

        var (_, otherClient) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        var theirs = await UploadImageAsync(otherClient, "theirs.png", 41);
        // The other owner puts THEIR OWN file in THEIR album.
        var otherOwner = await OwnerOfAsync(theirs);
        var otherAlbum = await CreateAlbumAsync(otherOwner, "Theirs");
        await AddToAlbumAsync(otherAlbum, theirs);

        Assert.Empty(await ImageIdsAsync(client, "/api/images?albumMembership=assigned"));
        Assert.Equal(mine, Assert.Single(
            await ImageIdsAsync(client, "/api/images?albumMembership=unassigned")));
        Assert.Equal(owner, await OwnerOfAsync(mine));
    }

    private async Task<Guid> OwnerOfAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.Where(f => f.Id == fileId).Select(f => f.OwnerUserId).SingleAsync();
    }

    // ------------------------------------------------------------------ videos

    [Fact]
    public async Task Video_Gallery_Supports_The_Same_Filter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var inAlbum = await UploadVideoAsync(owner, "in.mp4", "va01");
        var loose = await UploadVideoAsync(owner, "loose.mp4", "vb01");
        var album = await CreateAlbumAsync(owner, "Clips");
        await AddToAlbumAsync(album, inAlbum);

        var assigned = await client.GetFromJsonAsync<VideoListResponse>(
            "/api/videos?albumMembership=assigned");
        Assert.Equal(inAlbum, Assert.Single(assigned!.Items).Id);

        var unassigned = await client.GetFromJsonAsync<VideoListResponse>(
            "/api/videos?albumMembership=unassigned");
        Assert.Equal(loose, Assert.Single(unassigned!.Items).Id);
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public async Task AlbumId_Combined_With_Unassigned_Is_A_400_Not_An_Empty_Page()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Trip");

        var response = await client.GetAsync(
            $"/api/images?albumId={album}&albumMembership=unassigned");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("albumMembership", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlbumId_Combined_With_Assigned_Is_Allowed()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadImageAsync(client, "in.png", 40);
        var album = await CreateAlbumAsync(owner, "Trip");
        await AddToAlbumAsync(album, file);

        var ids = await ImageIdsAsync(client, $"/api/images?albumId={album}&albumMembership=assigned");
        Assert.Equal(file, Assert.Single(ids));
    }

    [Theory]
    [InlineData("/api/images")]
    [InlineData("/api/videos")]
    public async Task Unknown_Membership_Value_Is_Rejected(string path)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"{path}?albumMembership=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------- cursor binding

    [Fact]
    public async Task Changing_The_Membership_Filter_Invalidates_An_Existing_Cursor()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Trip");
        for (var i = 0; i < 4; i++)
        {
            var id = await UploadImageAsync(client, $"f-{i}.png", 40 + i);
            await AddToAlbumAsync(album, id);
        }

        var first = await client.GetFromJsonAsync<ImageListResponse>(
            "/api/images?albumMembership=assigned&limit=2");
        Assert.NotNull(first!.NextCursor);

        // Same cursor, different filter set → explicit 400, never stale rows.
        var replayed = await client.GetAsync(
            $"/api/images?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);

        // Replayed under the SAME filter it was issued for, it still works.
        var continued = await client.GetAsync(
            $"/api/images?albumMembership=assigned&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
    }

    [Fact]
    public void Fingerprint_Differs_Per_Membership_Value()
    {
        var any = new ImageFilters { AlbumMembership = AlbumMembershipFilter.Any };
        var assigned = new ImageFilters { AlbumMembership = AlbumMembershipFilter.Assigned };
        var unassigned = new ImageFilters { AlbumMembership = AlbumMembershipFilter.Unassigned };

        // Any alone is still an "empty" filter set (no fingerprint at all).
        Assert.True(any.IsEmpty);
        Assert.Null(any.Fingerprint());

        Assert.False(assigned.IsEmpty);
        Assert.False(unassigned.IsEmpty);
        Assert.NotEqual(assigned.Fingerprint(), unassigned.Fingerprint());
        Assert.NotNull(assigned.Fingerprint());
    }

    [Fact]
    public void WithoutSemantic_Keeps_The_Membership_Filter()
    {
        // The semantic candidate set must be built with the album filter ALREADY
        // applied, so ranking happens inside the filtered set.
        var filters = new ImageFilters
        {
            AlbumMembership = AlbumMembershipFilter.Unassigned,
            SemanticQuery = "a dog on a beach",
            SemanticTopK = 50,
        };

        var physical = filters.WithoutSemantic();

        Assert.Equal(AlbumMembershipFilter.Unassigned, physical.AlbumMembership);
        Assert.Null(physical.SemanticQuery);
    }
}

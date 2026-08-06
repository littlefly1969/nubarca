using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 5 — unified media workspace: GET /api/media and
// GET /api/albums/{albumId}/media. One contract, mixed kinds, owner-scoped,
// kind/filter compatibility enforced, cursor bound to the query identity.
public sealed class MediaCollectionEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MediaCollectionEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var f = await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
        return f.Id;
    }

    private async Task SetExcludedAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        f.MediaLibraryState = MediaLibraryState.Excluded;
        await db.SaveChangesAsync();
    }

    private async Task SetVideoMetadataAsync(Guid fileItemId, int width, int height, int? rotation)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        var meta = await db.BlobMetadata.FirstOrDefaultAsync(m => m.BlobObjectId == f.BlobObjectId);
        if (meta is null)
        {
            meta = new BlobMetadata { BlobObjectId = f.BlobObjectId };
            db.BlobMetadata.Add(meta);
        }
        meta.Width = width;
        meta.Height = height;
        meta.Rotation = rotation;
        await db.SaveChangesAsync();
    }

    private async Task SetImageMetadataAsync(Guid fileItemId, int width, int height, int? orientation)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        f.Width = width;
        f.Height = height;
        var meta = await db.BlobMetadata.FirstOrDefaultAsync(m => m.BlobObjectId == f.BlobObjectId);
        if (meta is null)
        {
            meta = new BlobMetadata { BlobObjectId = f.BlobObjectId };
            db.BlobMetadata.Add(meta);
        }
        meta.Orientation = orientation;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateAlbumWithAsync(Guid ownerId, params Guid[] fileItemIds)
    {
        using var scope = _factory.Services.CreateScope();
        var albums = scope.ServiceProvider.GetRequiredService<IAlbumService>();
        var album = await albums.CreateAsync(ownerId, "Mixed", null);
        await albums.AddItemsAsync(album.Id, ownerId, fileItemIds, default);
        return album.Id;
    }

    [Fact]
    public async Task Media_Unauthenticated_Returns401()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/media");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Media_All_ReturnsMixedKinds_WithCountsAndDiscriminator()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");

        var page = await client.GetFromJsonAsync<MediaListResponse>("/api/media");
        Assert.NotNull(page);
        var byId = page!.Items.ToDictionary(i => i.Id);
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.PhotoCount);
        Assert.Equal(1, page.VideoCount);
        Assert.Equal("image", byId[img].Kind);
        Assert.Equal("video", byId[vid].Kind);
        Assert.Equal($"/api/files/{img}/thumbnail?size=small", byId[img].ThumbnailUrl);
        Assert.Equal($"/api/files/{vid}/poster", byId[vid].PosterUrl);
    }

    [Fact]
    public async Task Media_Video_Exposes_Coded_Dimensions_When_Not_Rotated()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");
        await SetVideoMetadataAsync(vid, 1920, 1080, rotation: 0);

        var videos = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=video");
        var item = videos!.Items.Single(i => i.Id == vid);
        Assert.Equal(1920, item.Width);
        Assert.Equal(1080, item.Height);
    }

    [Theory]
    [InlineData(1, 4000, 3000)]  // normal → unchanged
    [InlineData(6, 3000, 4000)]  // 90° CW → portrait on screen
    [InlineData(8, 3000, 4000)]  // 270° CW → portrait on screen
    public async Task Media_Image_Uses_Exif_Oriented_Display_Dimensions(
        int orientation, int expectedWidth, int expectedHeight)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        // Coded landscape 4000×3000; an EXIF quarter-turn means it DISPLAYS
        // portrait, so the DTO must report the swapped (display) dimensions.
        await SetImageMetadataAsync(img, 4000, 3000, orientation);

        var page = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=image");
        var item = page!.Items.Single(i => i.Id == img);
        Assert.Equal(expectedWidth, item.Width);
        Assert.Equal(expectedHeight, item.Height);
    }

    [Fact]
    public async Task Media_Video_Swaps_Dimensions_For_A_Quarter_Turn_Rotation()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "portrait.mp4", "video/mp4");
        // Coded landscape + 90° display matrix = a portrait video on screen; the
        // DTO must report the DISPLAY dimensions so the tile matches the poster.
        await SetVideoMetadataAsync(vid, 1920, 1080, rotation: 90);

        var videos = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=video");
        var item = videos!.Items.Single(i => i.Id == vid);
        Assert.Equal(1080, item.Width);
        Assert.Equal(1920, item.Height);
    }

    [Fact]
    public async Task Media_KindImage_And_KindVideo_Narrow()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");

        var images = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=image");
        Assert.Single(images!.Items);
        Assert.Equal(img, images.Items[0].Id);
        Assert.Equal(1, images.PhotoCount);
        Assert.Equal(0, images.VideoCount);

        var videos = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=video");
        Assert.Single(videos!.Items);
        Assert.Equal(vid, videos.Items[0].Id);
        Assert.Equal(0, videos.PhotoCount);
        Assert.Equal(1, videos.VideoCount);
    }

    [Fact]
    public async Task Media_IsOwnerScoped()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");
        var mine = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "mine.jpg", "image/jpeg");
        var other = await _factory.SeedUserAsync("other@example.com");
        await UploadAsync(other, ImageFixtures.JpegWithExif(), "theirs.jpg", "image/jpeg");

        var page = await client.GetFromJsonAsync<MediaListResponse>("/api/media");
        Assert.Single(page!.Items);
        Assert.Equal(mine, page.Items[0].Id);
    }

    [Fact]
    public async Task Media_ExcludedScope_ShowsOnlyExcluded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var active = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");
        var excluded = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "b.jpg", "image/jpeg");
        await SetExcludedAsync(excluded);

        var activePage = await client.GetFromJsonAsync<MediaListResponse>("/api/media");
        Assert.Equal(new[] { active }, activePage!.Items.Select(i => i.Id).ToArray());

        var excludedPage = await client.GetFromJsonAsync<MediaListResponse>("/api/media?scope=excluded");
        Assert.Equal(new[] { excluded }, excludedPage!.Items.Select(i => i.Id).ToArray());
    }

    [Theory]
    [InlineData("/api/media?kind=all&hasGps=true")]      // photo filter, kind=all
    [InlineData("/api/media?kind=video&hasGps=true")]    // photo filter, kind=video
    [InlineData("/api/media?kind=all&codec=h264")]       // video filter, kind=all
    [InlineData("/api/media?kind=image&codec=h264")]     // video filter, kind=image
    public async Task Media_IncompatibleFilters_Return400(string url)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/media?limit=0")]
    [InlineData("/api/media?limit=101")]
    [InlineData("/api/media?kind=bogus")]
    [InlineData("/api/media?cursor=not-a-cursor")]
    public async Task Media_InvalidParams_Return400(string url)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Media_Cursor_NotReusableAcrossKind()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(owner, ImageFixtures.JpegWithExif(), $"p{i}.jpg", "image/jpeg");
        }
        var first = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=image&limit=2");
        Assert.NotNull(first!.NextCursor);

        // A cursor issued for kind=image must not replay under kind=video.
        var resp = await client.GetAsync(
            $"/api/media?kind=video&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Media_Paged_Request_Skips_Global_Counts()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(owner, ImageFixtures.JpegWithExif(), $"p{i}.jpg", "image/jpeg");
        }

        // First page carries the real, query-authoritative counts.
        var first = await client.GetFromJsonAsync<MediaListResponse>("/api/media?kind=image&limit=2");
        Assert.Equal(3, first!.Total);
        Assert.Equal(3, first.PhotoCount);
        Assert.NotNull(first.NextCursor);

        // A paged (cursor) request skips the global COUNT(s) and returns the -1
        // sentinel — the client keeps the first page's totals.
        var page2 = await client.GetFromJsonAsync<MediaListResponse>(
            $"/api/media?kind=image&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Single(page2!.Items);
        Assert.Equal(-1, page2.Total);
        Assert.Equal(-1, page2.PhotoCount);
        Assert.Equal(-1, page2.VideoCount);
    }

    [Fact]
    public async Task AlbumMedia_ForeignOrMissingAlbum_Returns404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync($"/api/albums/{Guid.NewGuid()}/media");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AlbumMedia_ReturnsOnlyAlbumMembers_MixedKinds()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");
        await UploadAsync(owner, ImageFixtures.JpegWithExif(), "out.jpg", "image/jpeg"); // not in album
        var albumId = await CreateAlbumWithAsync(owner, img, vid);

        var all = await client.GetFromJsonAsync<MediaListResponse>($"/api/albums/{albumId}/media");
        Assert.Equal(2, all!.Total);
        Assert.Equal(new[] { img, vid }.OrderBy(x => x), all.Items.Select(i => i.Id).OrderBy(x => x));

        var imagesOnly = await client.GetFromJsonAsync<MediaListResponse>($"/api/albums/{albumId}/media?kind=image");
        Assert.Single(imagesOnly!.Items);
        Assert.Equal(img, imagesOnly.Items[0].Id);
    }

    [Fact]
    public async Task AlbumMedia_IgnoresAlbumMembershipParam_NeverHiddenFilter()
    {
        // The album endpoint intentionally exposes no album-membership filter
        // (every album item is a member). An unknown query param is ignored, not
        // applied as an invisible filter — the album's members still come back.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var albumId = await CreateAlbumWithAsync(owner, img);

        var page = await client.GetFromJsonAsync<MediaListResponse>(
            $"/api/albums/{albumId}/media?albumMembership=unassigned");
        Assert.Single(page!.Items);
        Assert.Equal(img, page.Items[0].Id);
    }

    [Fact]
    public async Task Media_DoesNotLeakStorageInternals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");

        var body = await (await client.GetAsync("/api/media?kind=all")).Content.ReadAsStringAsync();
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

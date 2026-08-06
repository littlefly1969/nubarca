using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// Slice 5 — album cards gained per-kind counts + a cover mosaic. GET /api/albums
// projects them without N+1; Excluded/Personal members never appear in the cover.
public sealed class AlbumSummaryProjectionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AlbumSummaryProjectionTests()
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

    private async Task<Guid> CreateAlbumWithAsync(Guid ownerId, params Guid[] fileItemIds)
    {
        using var scope = _factory.Services.CreateScope();
        var albums = scope.ServiceProvider.GetRequiredService<IAlbumService>();
        var album = await albums.CreateAsync(ownerId, "Mixed", null);
        await albums.AddItemsAsync(album.Id, ownerId, fileItemIds, default);
        return album.Id;
    }

    [Fact]
    public async Task Albums_ProjectsPerKindCounts_And_CoverMosaic_ExcludingExcluded()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var img1 = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");
        var img2 = await UploadAsync(owner, ImageFixtures.JpegWithExif(includeGps: true), "b.jpg", "image/jpeg");
        var vid = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "c.mp4", "video/mp4");
        var albumId = await CreateAlbumWithAsync(owner, img1, img2, vid);
        await SetExcludedAsync(img2); // one image moved out of the library

        var albums = await client.GetFromJsonAsync<List<AlbumSummary>>("/api/albums");
        var album = albums!.Single(a => a.Id == albumId);

        Assert.Equal(3, album.ItemCount);       // raw membership unchanged
        Assert.Equal(1, album.PhotoCount);      // one active image (img1)
        Assert.Equal(1, album.VideoCount);      // one active video
        Assert.Equal(1, album.ExcludedCount);   // img2 excluded

        Assert.Equal(2, album.CoverItems.Count); // active only, ≤ 4
        Assert.DoesNotContain(album.CoverItems, c => c.FileItemId == img2);
        var cover = album.CoverItems.ToDictionary(c => c.FileItemId, c => c);
        Assert.Equal("image", cover[img1].Kind);
        Assert.Equal($"/api/files/{img1}/thumbnail?size=small", cover[img1].ThumbnailUrl);
        Assert.Equal("video", cover[vid].Kind);
        Assert.Equal($"/api/files/{vid}/poster", cover[vid].ThumbnailUrl);
    }

    [Fact]
    public async Task Albums_CoverCappedAtFour()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await UploadAsync(owner, ImageFixtures.JpegWithExif(), $"p{i}.jpg", "image/jpeg"));
        }
        var albumId = await CreateAlbumWithAsync(owner, ids.ToArray());

        var albums = await client.GetFromJsonAsync<List<AlbumSummary>>("/api/albums");
        var album = albums!.Single(a => a.Id == albumId);
        Assert.Equal(6, album.PhotoCount);
        Assert.Equal(4, album.CoverItems.Count);
    }
}

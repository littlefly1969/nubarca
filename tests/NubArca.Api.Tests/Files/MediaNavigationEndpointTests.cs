using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Media;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Files;

public sealed class MediaNavigationEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public MediaNavigationEndpointTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedAsync(Guid owner, string name, string month,
        bool video = false, string? title = null, bool favorite = false, bool excluded = false, bool deleted = false)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var file = await files.CreateAsync(owner, null, name, video ? "video/mp4" : "image/jpeg",
            new MemoryStream(video ? ImageFixtures.MinimalMp4() : ImageFixtures.JpegWithExif()));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.FileItems.SingleAsync(f => f.Id == file.Id);
        stored.EffectiveDateTaken = DateTime.SpecifyKind(DateTime.Parse(month + "-15"), DateTimeKind.Utc);
        stored.CreatedAt = DateTime.SpecifyKind(new DateTime(2026, 10, 6), DateTimeKind.Utc);
        stored.MediaLibraryState = excluded ? MediaLibraryState.Excluded : MediaLibraryState.Active;
        if (deleted) stored.DeletedAt = DateTime.UtcNow;
        db.FileItemUserMetadata.Add(new FileItemUserMetadata
        {
            Id = Guid.NewGuid(), FileItemId = file.Id, Title = title, IsFavorite = favorite, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return file.Id;
    }

    private async Task<Guid> AlbumAsync(Guid owner, params Guid[] ids)
    {
        using var scope = _factory.Services.CreateScope();
        var albums = scope.ServiceProvider.GetRequiredService<IAlbumService>();
        var album = await albums.CreateAsync(owner, "Timeline", null);
        await albums.AddItemsAsync(album.Id, owner, ids, default);
        return album.Id;
    }

    [Theory]
    [InlineData("/api/media/navigation")]
    [InlineData("/api/media/window?target=2023-06")]
    public async Task RequiresAuthentication(string path)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task IndexGroupsWholeCollectionAndWindowContinuesBothWays(string direction)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var ids = new List<Guid>();
        foreach (var month in new[] { "2022-01", "2023-06", "2023-06", "2024-12", "2025-02" })
            ids.Add(await SeedAsync(owner, $"photo-{ids.Count}.jpg", month));
        var query = $"sort=datetaken&direction={direction}&limit=2";
        var response = await client.GetAsync("/api/media/navigation?" + query);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var index = await response.Content.ReadFromJsonAsync<MediaNavigationIndex>();
        Assert.Equal(5, index!.Buckets.Sum(b => b.Count));
        Assert.Equal(2, index.Buckets.Single(b => b.Key == "2023-06").Count);
        Assert.Equal(direction == "asc" ? "2022-01" : "2025-02", index.Buckets[0].Key);
        var window = await client.GetFromJsonAsync<MediaNavigationWindow>("/api/media/window?" + query + "&target=2023-06");
        Assert.Equal(2, window!.Items.Count);
        Assert.All(window.Items, item => Assert.Contains(item.Id, ids.Skip(1).Take(2)));
        Assert.NotNull(window.PreviousCursor);
        Assert.NotNull(window.NextCursor);

        var previous = await client.GetFromJsonAsync<MediaNavigationWindow>("/api/media/window?" + query
            + "&before=true&cursor=" + Uri.EscapeDataString(window.PreviousCursor!));
        Assert.NotEmpty(previous!.Items);
        Assert.Empty(previous.Items.Select(i => i.Id).Intersect(window.Items.Select(i => i.Id)));
        Assert.Null(previous.PreviousCursor);
        var next = await client.GetFromJsonAsync<MediaListResponse>("/api/media?" + query
            + "&cursor=" + Uri.EscapeDataString(window.NextCursor!));
        Assert.NotEmpty(next!.Items);
        Assert.Empty(next.Items.Select(i => i.Id).Intersect(window.Items.Select(i => i.Id)));
        Assert.Equal(5, previous.Items.Count + window.Items.Count + next.Items.Count);
    }

    [Fact]
    public async Task IndexAndWindowRespectOwnerAlbumKindScopeFiltersAndDeletedItems()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var chosen = await SeedAsync(owner, "chosen.jpg", "2023-06", favorite: true);
        await SeedAsync(owner, "ordinary.jpg", "2024-01");
        var clip = await SeedAsync(owner, "clip.mp4", "2022-02", video: true, favorite: true);
        var excluded = await SeedAsync(owner, "excluded.jpg", "2021-03", excluded: true, favorite: true);
        await SeedAsync(owner, "deleted.jpg", "2020-04", deleted: true, favorite: true);
        var other = await _factory.SeedUserAsync("other@example.com");
        await SeedAsync(other, "private.jpg", "2019-05", favorite: true);
        var album = await AlbumAsync(owner, chosen, clip, excluded);
        var path = $"/api/albums/{album}/media";
        var query = "?sort=datetaken&kind=image&favorite=true";
        var index = await client.GetFromJsonAsync<MediaNavigationIndex>(path + "/navigation" + query);
        Assert.Equal(new MediaNavigationBucket("2023-06", 1), Assert.Single(index!.Buckets));
        var window = await client.GetFromJsonAsync<MediaNavigationWindow>(path + "/window" + query + "&target=2023-06");
        Assert.Equal(chosen, Assert.Single(window!.Items).Id);
        Assert.Null(window.NextCursor); Assert.Null(window.PreviousCursor);
        var excludedIndex = await client.GetFromJsonAsync<MediaNavigationIndex>(path + "/navigation" + query + "&scope=excluded");
        Assert.Equal("2021-03", Assert.Single(excludedIndex!.Buckets).Key);
        var videos = await client.GetFromJsonAsync<MediaNavigationIndex>(path + "/navigation?sort=datetaken&kind=video");
        Assert.Equal("2022-02", Assert.Single(videos!.Buckets).Key);
        var otherAlbum = await AlbumAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/albums/{otherAlbum}/media/navigation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/albums/{otherAlbum}/media/window?target=2023-06")).StatusCode);
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task AlphabetUsesDisplayTitleCaseInsensitiveAndKeepsUnicodeAndDigits(string direction)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await SeedAsync(owner, "z.jpg", "2023-06", title: "Alba");
        var b = await SeedAsync(owner, "a.jpg", "2023-06", title: "albero");
        await SeedAsync(owner, "7.jpg", "2023-06");
        await SeedAsync(owner, "Été.jpg", "2023-06");
        var query = $"sort=name&direction={direction}";
        var index = await client.GetFromJsonAsync<MediaNavigationIndex>("/api/media/navigation?" + query);
        Assert.Equal(4, index!.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(2, index.Buckets.Single(bucket => bucket.Key == "n:a").Count);
        Assert.Contains(index.Buckets, bucket => bucket.Key == "n:7");
        var window = await client.GetFromJsonAsync<MediaNavigationWindow>("/api/media/window?" + query + "&limit=2&target=n%3Aa");
        Assert.Equal(direction == "asc" ? new[] { a, b } : new[] { b, a }, window!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task InsertionDateIsDistinctFromAcquisitionDateAndCollapsingAffectsCounts()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedAsync(owner, "a.jpg", "2023-06");
        await SeedAsync(owner, "b.jpg", "2024-02");
        var index = await client.GetFromJsonAsync<MediaNavigationIndex>("/api/media/navigation?sort=created");
        Assert.Equal(new MediaNavigationBucket("2026-10", 2), Assert.Single(index!.Buckets));
        var collapsed = await client.GetFromJsonAsync<MediaNavigationIndex>("/api/media/navigation?sort=created&kind=image&collapseDuplicates=true");
        Assert.Equal(1, Assert.Single(collapsed!.Buckets).Count);
    }

    [Theory]
    [InlineData("/navigation?sort=size")]
    [InlineData("/navigation?kind=all&hasGps=true")]
    [InlineData("/window?sort=datetaken&target=2023-99")]
    [InlineData("/window?sort=name&target=not-a-key")]
    [InlineData("/window?target=2023-06&before=true")]
    [InlineData("/window?cursor=invalid")]
    [InlineData("/window?target=2023-06&limit=101")]
    public async Task RejectsInvalidRequests(string suffix)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/media" + suffix)).StatusCode);
    }

    [Fact]
    public async Task WindowCursorCannotCrossFilterOrMediaKindAndMissingBucketReturns404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedAsync(owner, "a.jpg", "2023-06");
        await SeedAsync(owner, "b.jpg", "2024-02");
        var page = await client.GetFromJsonAsync<MediaNavigationWindow>("/api/media/window?sort=datetaken&direction=asc&limit=1&target=2023-06");
        var cursor = Uri.EscapeDataString(page!.NextCursor!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/media/window?sort=datetaken&direction=asc&favorite=true&cursor=" + cursor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/media/window?sort=datetaken&direction=asc&kind=video&cursor=" + cursor)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/media/window?sort=datetaken&target=1999-01")).StatusCode);
    }
}

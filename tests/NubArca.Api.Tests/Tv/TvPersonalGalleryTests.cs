using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Tv;

// TV Personal Gallery: the grant-gated projection of the owner image gallery.
// Covers (1) authorization — every endpoint requires the TV session cookie AND
// a live unlock grant, with cross-session/cross-owner/expired/revoked/stale
// grants all denied; (2) QUERY EQUIVALENCE — for the same seeded library the
// TV endpoint returns exactly the ordered ids /api/images returns, for every
// supported filter/sort/paging combination (both parse through the shared
// GalleryQueryParser, this pins the contract); (3) paging safety; (4) the
// favorite / album-add mutations; (5) media-byte rules (derived only).
public sealed class TvPersonalGalleryTests : IDisposable
{
    private const string Pin = "123456";

    // The standard pooled factory raises non-dedicated rate-limit buckets.
    // Dedicated rate-limit tests retain explicitly configured factories.
    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPersonalGalleryTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── authorization ───────────────────────────────────────────────────────

    [Fact]
    public async Task Every_Personal_Gallery_Endpoint_Requires_A_Tv_Session()
    {
        var anonymous = _factory.CreateClient();
        var someId = Guid.NewGuid();
        foreach (var url in new[]
        {
            "/api/tv/personal/gallery",
            "/api/tv/personal/gallery/people",
            "/api/tv/personal/gallery/albums",
            "/api/tv/personal/videos",
            $"/api/tv/personal/media/{someId}/thumbnail",
            $"/api/tv/personal/media/{someId}/preview",
            $"/api/tv/personal/media/{someId}/poster",
            $"/api/tv/personal/media/{someId}/video-preview-strip",
            $"/api/tv/personal/media/{someId}/video",
            $"/api/tv/personal/media/{someId}/info",
        })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var favorite = await anonymous.PutAsJsonAsync(
            $"/api/tv/personal/media/{someId}/favorite", new { favorite = true });
        Assert.Equal(HttpStatusCode.Unauthorized, favorite.StatusCode);
        var bulk = await anonymous.PostAsJsonAsync(
            $"/api/tv/personal/gallery/albums/{someId}/items", new { fileItemIds = new[] { someId } });
        Assert.Equal(HttpStatusCode.Unauthorized, bulk.StatusCode);
        foreach (var url in new[]
        {
            "/api/tv/personal/gallery/add-to-beauty-lab",
            "/api/tv/personal/gallery/add-to-plates",
            "/api/tv/personal/gallery/trash",
        })
        {
            var response = await anonymous.PostAsJsonAsync(url, new { fileItemIds = new[] { someId } });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            AssertNoStore(response);
        }
    }

    [Fact]
    public async Task Owner_Web_Auth_Alone_Cannot_Reach_The_Tv_Projection()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        // The owner cookie is NOT a TV session: 401, not an owner-authorized 200.
        var response = await owner.GetAsync("/api/tv/personal/gallery");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var mutation = await owner.PostAsJsonAsync(
            "/api/tv/personal/gallery/trash", new { fileItemIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
    }

    [Fact]
    public async Task Paired_Session_Without_A_Grant_Is_Locked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        var missing = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery");
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Contains("locked", await missing.Content.ReadAsStringAsync());
        AssertNoStore(missing);

        var garbage = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/gallery", grant: "not-a-grant");
        Assert.Equal(HttpStatusCode.Forbidden, garbage.StatusCode);

        // Party endpoints keep working — Party state can never substitute for
        // (or be blocked by) Personal Area access.
        var albums = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums");
        Assert.Equal(HttpStatusCode.OK, albums.StatusCode);
    }

    [Fact]
    public async Task A_Grant_From_Another_Tv_Session_Is_Denied()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookieA = await PairTvAsync(owner);
        var cookieB = await PairTvAsync(owner);
        var tokenA = await UnlockTokenAsync(cookieA);

        var crossed = await TvSendAsync(cookieB, HttpMethod.Get, "/api/tv/personal/gallery", tokenA);
        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);

        var own = await TvSendAsync(cookieA, HttpMethod.Get, "/api/tv/personal/gallery", tokenA);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Expired_Revoked_And_Stale_Generation_Grants_Are_Denied()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        // Expired.
        var expired = await UnlockTokenAsync(cookie);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TvPersonalUnlockGrants
                .Where(g => g.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        var expiredResp = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery", expired);
        Assert.Equal(HttpStatusCode.Forbidden, expiredResp.StatusCode);

        // Revoked by explicit lock.
        var revoked = await UnlockTokenAsync(cookie);
        (await TvSendAsync(cookie, HttpMethod.Post, "/api/tv/personal/lock")).EnsureSuccessStatusCode();
        var revokedResp = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery", revoked);
        Assert.Equal(HttpStatusCode.Forbidden, revokedResp.StatusCode);

        // Stale PIN generation → the DISTINCT pin_changed reason.
        var stale = await UnlockTokenAsync(cookie);
        (await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = "999999", confirmPin = "999999" }))
            .EnsureSuccessStatusCode();
        var staleResp = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery", stale);
        Assert.Equal(HttpStatusCode.Forbidden, staleResp.StatusCode);
        Assert.Contains("pin_changed", await staleResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cross_Owner_Media_Ids_Are_Generic_404s()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, "alice.png");

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairTvAsync(bob);
        var bobToken = await UnlockTokenAsync(bobCookie);

        foreach (var url in new[]
        {
            $"/api/tv/personal/media/{aliceFile}/thumbnail",
            $"/api/tv/personal/media/{aliceFile}/preview",
            $"/api/tv/personal/media/{aliceFile}/info",
        })
        {
            var response = await TvSendAsync(bobCookie, HttpMethod.Get, url, bobToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var favorite = await TvSendAsync(
            bobCookie, HttpMethod.Put, $"/api/tv/personal/media/{aliceFile}/favorite",
            bobToken, json: new { favorite = true });
        Assert.Equal(HttpStatusCode.NotFound, favorite.StatusCode);

        // And Alice's gallery never shows in Bob's projection.
        var page = await TvGalleryAsync(bobCookie, bobToken, "");
        Assert.DoesNotContain(page.Items, i => i.Id == aliceFile);
    }

    [Fact]
    public async Task Personal_Videos_List_And_Stream_The_Owners_Video_Only()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var videoId = await UploadAsync(
            owner, "living-room.mp4", ImageFixtures.MinimalMp4(), "video/mp4");
        var imageId = await UploadAsync(owner, "still.png");

        var list = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/videos?limit=20", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertNoStore(list);
        var page = (await list.Content.ReadFromJsonAsync<TvPersonalVideoPageDto>())!;
        var video = Assert.Single(page.Items, item => item.Id == videoId);
        Assert.DoesNotContain(page.Items, item => item.Id == imageId);
        Assert.Equal($"/api/tv/personal/media/{videoId}/poster", video.PosterUrl);
        Assert.Equal($"/api/tv/personal/media/{videoId}/video", video.VideoUrl);
        Assert.Equal($"/api/tv/personal/media/{videoId}/video-preview-strip", video.PreviewStripUrl);

        var poster = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/media/{videoId}/poster", token);
        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);
        Assert.Equal("image/jpeg", poster.Content.Headers.ContentType?.MediaType);

        var videoRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/personal/media/{videoId}/video");
        videoRequest.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        videoRequest.Headers.Add(TvPersonalAreaService.UnlockHeader, token);
        videoRequest.Headers.Range = new RangeHeaderValue(0, 0);
        var stream = await _factory.CreateClient().SendAsync(videoRequest);
        Assert.Equal(HttpStatusCode.PartialContent, stream.StatusCode);
        Assert.Equal("video/mp4", stream.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Library_Album_Party_And_Personal_Tv_Agree_On_Media_Display_Projection()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var photoId = await UploadAsync(owner, "portrait.png", PngBytes(320, 180));
        var videoId = await UploadAsync(
            owner, "portrait.mp4", ImageFixtures.MinimalMp4(), "video/mp4");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobs = await db.FileItems
                .Where(f => f.Id == photoId || f.Id == videoId)
                .Select(f => new { f.Id, f.BlobObjectId })
                .ToDictionaryAsync(x => x.Id);

            await db.BlobMetadata
                .Where(m => m.BlobObjectId == blobs[photoId].BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Width, 320)
                    .SetProperty(m => m.Height, 180)
                    .SetProperty(m => m.Orientation, 6));
            await db.BlobMetadata
                .Where(m => m.BlobObjectId == blobs[videoId].BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Width, 1920)
                    .SetProperty(m => m.Height, 1080)
                    .SetProperty(m => m.Rotation, 90));
        }

        var created = await owner.PostAsJsonAsync("/api/albums", new { name = "Projection parity" });
        created.EnsureSuccessStatusCode();
        var albumId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/items/bulk",
            new { fileItemIds = new[] { photoId, videoId } })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/tv-settings",
            new { showOnTv = true })).EnsureSuccessStatusCode();

        // Frontend Library and frontend Album are backed by the same media
        // collection service and must expose byte-for-byte-equivalent media facts.
        var library = (await owner.GetFromJsonAsync<MediaListResponse>(
            "/api/media?kind=all&limit=20"))!;
        var album = (await owner.GetFromJsonAsync<MediaListResponse>(
            $"/api/albums/{albumId}/media?kind=all&limit=20"))!;
        var libraryPhoto = Assert.Single(library.Items, item => item.Id == photoId);
        var albumPhoto = Assert.Single(album.Items, item => item.Id == photoId);
        var libraryVideo = Assert.Single(library.Items, item => item.Id == videoId);
        var albumVideo = Assert.Single(album.Items, item => item.Id == videoId);

        Assert.Equal((180, 320, "image"), (libraryPhoto.Width, libraryPhoto.Height, libraryPhoto.Kind));
        Assert.Equal(
            (libraryPhoto.Width, libraryPhoto.Height, libraryPhoto.Kind, libraryPhoto.ThumbnailUrl),
            (albumPhoto.Width, albumPhoto.Height, albumPhoto.Kind, albumPhoto.ThumbnailUrl));
        Assert.Equal((1080, 1920, "video"), (libraryVideo.Width, libraryVideo.Height, libraryVideo.Kind));
        Assert.Equal(
            (libraryVideo.Width, libraryVideo.Height, libraryVideo.Kind, libraryVideo.ThumbnailUrl),
            (albumVideo.Width, albumVideo.Height, albumVideo.Kind, albumVideo.ThumbnailUrl));
        Assert.Equal($"/api/files/{videoId}/poster", libraryVideo.ThumbnailUrl);

        // The legacy web endpoints still back narrower frontend paths (including
        // filtered/semantic photo results) and TV Personal. They must not drift
        // from the unified Library/Album projection.
        var legacyImages = (await owner.GetFromJsonAsync<ImageListResponse>(
            "/api/images?limit=20"))!;
        var legacyVideos = (await owner.GetFromJsonAsync<VideoListResponse>(
            "/api/videos?limit=20"))!;
        var legacyPhoto = Assert.Single(legacyImages.Items, item => item.Id == photoId);
        var legacyVideo = Assert.Single(legacyVideos.Items, item => item.Id == videoId);
        Assert.Equal(
            (libraryPhoto.Width, libraryPhoto.Height, libraryPhoto.ThumbnailUrl),
            (legacyPhoto.Width, legacyPhoto.Height, legacyPhoto.ThumbnailUrl));
        Assert.Equal(
            (libraryVideo.Width, libraryVideo.Height, libraryVideo.ThumbnailUrl),
            (legacyVideo.Width, legacyVideo.Height, legacyVideo.PosterUrl));

        // TV Party applies a different authorization prefix, but classification,
        // display dimensions and derivative KIND must be identical.
        var partyResponse = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/albums/{albumId}/items");
        partyResponse.EnsureSuccessStatusCode();
        var party = (await partyResponse.Content.ReadFromJsonAsync<TvAlbumItemsDto>())!;
        var partyPhoto = Assert.Single(party.Items, item => item.Id == photoId);
        var partyVideo = Assert.Single(party.Items, item => item.Id == videoId);
        Assert.Equal((libraryPhoto.Width, libraryPhoto.Height, "image"),
            (partyPhoto.Width, partyPhoto.Height, partyPhoto.MediaType));
        Assert.Equal((libraryVideo.Width, libraryVideo.Height, "video"),
            (partyVideo.Width, partyVideo.Height, partyVideo.MediaType));
        Assert.Equal($"/api/tv/media/{photoId}/thumbnail", partyPhoto.ThumbnailUrl);
        Assert.Equal($"/api/tv/media/{videoId}/poster", partyVideo.PosterUrl);

        // Personal TV Photo/Video are narrower, grant-gated DTOs over the same
        // projection facts.
        var gallery = await TvGalleryAsync(cookie, token, "");
        var photo = Assert.Single(gallery.Items, item => item.Id == photoId);
        Assert.Equal((libraryPhoto.Width, libraryPhoto.Height, "image"),
            (photo.Width, photo.Height, photo.MediaType));
        Assert.Equal($"/api/tv/personal/media/{photoId}/thumbnail", photo.ThumbnailUrl);

        var videosResponse = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/videos?limit=20", token);
        videosResponse.EnsureSuccessStatusCode();
        var videos = (await videosResponse.Content.ReadFromJsonAsync<TvPersonalVideoPageDto>())!;
        var video = Assert.Single(videos.Items, item => item.Id == videoId);
        Assert.Equal((libraryVideo.Width, libraryVideo.Height), (video.Width, video.Height));
        Assert.Equal($"/api/tv/personal/media/{videoId}/poster", video.PosterUrl);
    }

    [Fact]
    public async Task A_File_Deleted_After_Listing_Stops_Serving_Immediately()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var fileId = await UploadAsync(owner, "gone.png");

        var before = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/media/{fileId}/thumbnail", token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await SoftDeleteAsync(fileId);

        foreach (var url in new[]
        {
            $"/api/tv/personal/media/{fileId}/thumbnail",
            $"/api/tv/personal/media/{fileId}/preview",
            $"/api/tv/personal/media/{fileId}/info",
        })
        {
            var after = await TvSendAsync(cookie, HttpMethod.Get, url, token);
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }
    }

    [Fact]
    public async Task Non_Gallery_Files_Are_Not_Served_As_Personal_Media()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        // A text file is owner-owned + active but NOT gallery-eligible.
        var textId = await UploadAsync(owner, "notes.txt", bytes: [1, 2, 3], mime: "text/plain");

        var thumb = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/media/{textId}/thumbnail", token);
        Assert.Equal(HttpStatusCode.NotFound, thumb.StatusCode);
        var info = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/media/{textId}/info", token);
        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        var favorite = await TvSendAsync(
            cookie, HttpMethod.Put, $"/api/tv/personal/media/{textId}/favorite",
            token, json: new { favorite = true });
        Assert.Equal(HttpStatusCode.NotFound, favorite.StatusCode);
    }

    [Fact]
    public async Task Media_Bytes_Are_Derived_Never_The_Original()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var original = PngBytes(64, 64);
        var fileId = await UploadAsync(owner, "photo.png", original);

        foreach (var variant in new[] { "thumbnail", "preview" })
        {
            var response = await TvSendAsync(
                cookie, HttpMethod.Get, $"/api/tv/personal/media/{fileId}/{variant}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Derived artifacts are re-encoded JPEG — never the original PNG bytes.
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.NotEqual(original, bytes);
            // Immutable derived bytes over an authenticated cookie: private
            // browser cache only, never a shared/proxy-cacheable response.
            Assert.Contains("private", response.Headers.CacheControl?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task Gallery_Json_Responses_Are_No_Store_And_Leak_No_Internals()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        await UploadAsync(owner, "leakcheck.png");

        var response = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blobObjectId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerUserId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/files/", raw, StringComparison.Ordinal);
        // Media URLs stay on the grant-gated TV projection.
        Assert.Contains("/api/tv/personal/media/", raw, StringComparison.Ordinal);
    }

    // ── query equivalence with /api/images ──────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("q=sunset")]
    [InlineData("q=vacanza")]              // matches user title/tags only
    [InlineData("favorite=true")]
    [InlineData("favorite=false")]
    [InlineData("minRating=3")]
    [InlineData("hasGps=true")]
    [InlineData("hasGps=false")]
    [InlineData("dateTakenFrom=2023-06-01T00:00:00Z")]
    [InlineData("dateTakenTo=2023-06-01T00:00:00Z")]
    [InlineData("dateTakenFrom=2023-01-01T00:00:00Z&dateTakenTo=2023-12-31T23:59:59Z")]
    [InlineData("collapseDuplicates=true")]
    [InlineData("sort=name&direction=asc")]
    [InlineData("sort=name&direction=desc")]
    [InlineData("sort=size&direction=asc")]
    [InlineData("sort=size&direction=desc")]
    [InlineData("sort=datetaken&direction=asc")]
    [InlineData("sort=datetaken&direction=desc")]
    [InlineData("sort=created&direction=asc")]
    [InlineData("favorite=true&sort=name&direction=asc")]
    [InlineData("q=photo&minRating=2&sort=size&direction=desc&collapseDuplicates=true")]
    public async Task Tv_Gallery_Returns_Exactly_The_Web_Gallery_Ids_In_Order(string query)
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        await SeedLibraryAsync(owner);

        var webIds = await WebAllIdsAsync(owner, query, pageSize: 3);
        var tvIds = await TvAllIdsAsync(cookie, token, query, pageSize: 3);

        Assert.NotEmpty(webIds);           // the seed must exercise the filter
        Assert.Equal(webIds, tvIds);
    }

    [Fact]
    public async Task Tv_Gallery_Empty_Filters_Return_The_Full_Personal_Gallery()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        await SeedLibraryAsync(owner);

        var webIds = await WebAllIdsAsync(owner, "", pageSize: 200);
        var tvIds = await TvAllIdsAsync(cookie, token, "", pageSize: 200);
        Assert.Equal(webIds, tvIds);
        Assert.Equal(webIds.Count, webIds.Distinct().Count());
    }

    [Fact]
    public async Task People_Filters_Match_The_Web_Gallery_Semantics()
    {
        var (ownerId, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        // Distinct bytes: identical uploads would dedup to ONE blob and the
        // blob-level face detection would (correctly) match both files.
        var withPerson = await UploadAsync(owner, "with-person.png", PngBytes(24, 24));
        var without = await UploadAsync(owner, "without-person.png", PngBytes(40, 40));
        var personId = await SeedPersonOnFileAsync(ownerId, withPerson, "Anna");

        foreach (var query in new[]
        {
            $"includePeople={personId}",
            $"includePeople={personId}&includePeopleMode=any",
            $"excludePeople={personId}",
        })
        {
            var webIds = await WebAllIdsAsync(owner, query, pageSize: 50);
            var tvIds = await TvAllIdsAsync(cookie, token, query, pageSize: 50);
            Assert.Equal(webIds, tvIds);
        }

        var include = await TvAllIdsAsync(cookie, token, $"includePeople={personId}", 50);
        Assert.Contains(withPerson, include);
        Assert.DoesNotContain(without, include);
        var exclude = await TvAllIdsAsync(cookie, token, $"excludePeople={personId}", 50);
        Assert.DoesNotContain(withPerson, exclude);
        Assert.Contains(without, exclude);
    }

    [Fact]
    public async Task People_Options_Match_The_Web_People_List_Without_Face_Internals()
    {
        var (ownerId, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var fileId = await UploadAsync(owner, "person.png");
        var personId = await SeedPersonOnFileAsync(ownerId, fileId, "Anna");

        var response = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery/people", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var raw = await response.Content.ReadAsStringAsync();
        var people = JsonDocument.Parse(raw).RootElement;
        var entry = people.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == personId);
        Assert.Equal("Anna", entry.GetProperty("name").GetString());
        Assert.Equal(1, entry.GetProperty("faceCount").GetInt32());
        // Never face ids, bounding boxes, or representative file references.
        Assert.DoesNotContain("faceId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("box", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("representative", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── paging safety ───────────────────────────────────────────────────────

    [Fact]
    public async Task Paging_Yields_No_Duplicates_And_No_Gaps_Across_Pages()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        for (var i = 0; i < 7; i += 1)
        {
            await UploadAsync(owner, $"page-{i:D2}.png");
        }

        var paged = await TvAllIdsAsync(cookie, token, "sort=name&direction=asc", pageSize: 2);
        var single = await TvAllIdsAsync(cookie, token, "sort=name&direction=asc", pageSize: 200);
        Assert.Equal(single, paged);
        Assert.Equal(paged.Count, paged.Distinct().Count());
    }

    [Fact]
    public async Task Invalid_Or_Mismatched_Cursors_Fail_Safely()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        for (var i = 0; i < 3; i += 1)
        {
            await UploadAsync(owner, $"cur-{i}.png");
        }

        var malformed = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/gallery?cursor=%20garbage%20!!", token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var first = await TvGalleryAsync(cookie, token, "?limit=1");
        Assert.NotNull(first.NextCursor);

        // Same cursor replayed under a different sort → 400.
        var wrongSort = await TvSendAsync(
            cookie, HttpMethod.Get,
            $"/api/tv/personal/gallery?limit=1&sort=name&direction=asc&cursor={Uri.EscapeDataString(first.NextCursor!)}",
            token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongSort.StatusCode);

        // Same cursor replayed under a different filter set → 400.
        var wrongFilter = await TvSendAsync(
            cookie, HttpMethod.Get,
            $"/api/tv/personal/gallery?limit=1&favorite=true&cursor={Uri.EscapeDataString(first.NextCursor!)}",
            token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongFilter.StatusCode);
    }

    [Fact]
    public async Task A_Deletion_Between_Pages_Never_Breaks_The_Cursor()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i += 1)
        {
            ids.Add(await UploadAsync(owner, $"del-{i}.png"));
        }

        var first = await TvGalleryAsync(cookie, token, "?limit=2&sort=name&direction=asc");
        Assert.NotNull(first.NextCursor);

        // Delete an item that would appear on the next page (del-2).
        await SoftDeleteAsync(ids[2]);

        var second = await TvGalleryAsync(
            cookie, token,
            $"?limit=200&sort=name&direction=asc&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        var secondIds = second.Items.Select(i => i.Id).ToList();
        Assert.DoesNotContain(ids[2], secondIds);
        Assert.Contains(ids[3], secondIds);
        Assert.Contains(ids[4], secondIds);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(secondIds));
    }

    [Fact]
    public async Task Invalid_Filter_Values_Return_Safe_Validation_Errors()
    {
        var (_, _, cookie, token) = await PairedUnlockedOwnerAsync();

        var badRating = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/gallery?minRating=9", token);
        Assert.Equal(HttpStatusCode.BadRequest, badRating.StatusCode);

        var badSort = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/gallery?sort=owner", token);
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);

        var badDates = await TvSendAsync(
            cookie, HttpMethod.Get,
            "/api/tv/personal/gallery?dateTakenFrom=2024-01-01T00:00:00Z&dateTakenTo=2023-01-01T00:00:00Z",
            token);
        Assert.Equal(HttpStatusCode.BadRequest, badDates.StatusCode);

        var longQ = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/gallery?q={new string('x', 300)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, longQ.StatusCode);
    }

    // ── mutations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Favorite_Toggle_Is_The_Same_Owner_Mutation_The_Web_Sees()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var fileId = await UploadAsync(owner, "fav.png");

        var on = await TvSendAsync(
            cookie, HttpMethod.Put, $"/api/tv/personal/media/{fileId}/favorite",
            token, json: new { favorite = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        AssertNoStore(on);
        var onDto = JsonDocument.Parse(await on.Content.ReadAsStringAsync()).RootElement;
        Assert.True(onDto.GetProperty("favorite").GetBoolean());

        // The owner web surface reflects the same FileItemUserMetadata state.
        var meta = await owner.GetFromJsonAsync<JsonElement>($"/api/files/{fileId}/metadata");
        Assert.True(meta.GetProperty("user").GetProperty("favorite").GetBoolean());

        // The favorite FILTER now matches it on both surfaces.
        Assert.Contains(fileId, await WebAllIdsAsync(owner, "favorite=true", 50));
        Assert.Contains(fileId, await TvAllIdsAsync(cookie, token, "favorite=true", 50));

        // Idempotent + reversible.
        var again = await TvSendAsync(
            cookie, HttpMethod.Put, $"/api/tv/personal/media/{fileId}/favorite",
            token, json: new { favorite = true });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var off = await TvSendAsync(
            cookie, HttpMethod.Put, $"/api/tv/personal/media/{fileId}/favorite",
            token, json: new { favorite = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.DoesNotContain(fileId, await TvAllIdsAsync(cookie, token, "favorite=true", 50));

        // The gallery item DTO carries the favorite state.
        var page = await TvGalleryAsync(cookie, token, "");
        Assert.False(page.Items.Single(i => i.Id == fileId).Favorite);
    }

    [Fact]
    public async Task Favorite_Does_Not_Clobber_Other_User_Metadata()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var fileId = await UploadAsync(owner, "annotated.png");
        (await owner.PatchAsJsonAsync($"/api/files/{fileId}/metadata", new
        {
            title = "Il mio titolo",
            rating = 4,
            tags = new[] { "mare" },
        })).EnsureSuccessStatusCode();

        (await TvSendAsync(
            cookie, HttpMethod.Put, $"/api/tv/personal/media/{fileId}/favorite",
            token, json: new { favorite = true })).EnsureSuccessStatusCode();

        var meta = await owner.GetFromJsonAsync<JsonElement>($"/api/files/{fileId}/metadata");
        var user = meta.GetProperty("user");
        Assert.Equal("Il mio titolo", user.GetProperty("title").GetString());
        Assert.Equal(4, user.GetProperty("rating").GetInt32());
        Assert.True(user.GetProperty("favorite").GetBoolean());
    }

    [Fact]
    public async Task Album_Bulk_Add_Matches_The_Web_Bulk_Semantics()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var a = await UploadAsync(owner, "alb-a.png");
        var b = await UploadAsync(owner, "alb-b.png");

        var created = await owner.PostAsJsonAsync("/api/albums", new { name = "Dal divano" });
        created.EnsureSuccessStatusCode();
        var albumId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // TV album options list the owner's albums.
        var options = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/gallery/albums", token);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var optionsJson = JsonDocument.Parse(await options.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(optionsJson.EnumerateArray(), e => e.GetProperty("id").GetGuid() == albumId);

        var add = await TvSendAsync(
            cookie, HttpMethod.Post, $"/api/tv/personal/gallery/albums/{albumId}/items",
            token, json: new { fileItemIds = new[] { a, b } });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var result = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, result.GetProperty("succeeded").GetInt32());

        // Re-adding counts as skipped, exactly like the owner bulk endpoint.
        var again = await TvSendAsync(
            cookie, HttpMethod.Post, $"/api/tv/personal/gallery/albums/{albumId}/items",
            token, json: new { fileItemIds = new[] { a } });
        var againResult = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, againResult.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, againResult.GetProperty("skipped").GetInt32());

        // The web album view shows the added items.
        var items = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/items");
        var itemIds = items.EnumerateArray()
            .Select(e => e.GetProperty("fileItemId").GetGuid())
            .ToList();
        Assert.Contains(a, itemIds);
        Assert.Contains(b, itemIds);
    }

    [Fact]
    public async Task Album_Bulk_Add_Denies_Foreign_Albums_And_Skips_Foreign_Files()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, "alice.png");
        var aliceAlbum = await alice.PostAsJsonAsync("/api/albums", new { name = "Alice" });
        aliceAlbum.EnsureSuccessStatusCode();
        var aliceAlbumId = JsonDocument.Parse(await aliceAlbum.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairTvAsync(bob);
        var bobToken = await UnlockTokenAsync(bobCookie);
        var bobFile = await UploadAsync(bob, "bob.png");
        var bobAlbum = await bob.PostAsJsonAsync("/api/albums", new { name = "Bob" });
        bobAlbum.EnsureSuccessStatusCode();
        var bobAlbumId = JsonDocument.Parse(await bobAlbum.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Alice's album through Bob's TV → generic 404.
        var foreignAlbum = await TvSendAsync(
            bobCookie, HttpMethod.Post, $"/api/tv/personal/gallery/albums/{aliceAlbumId}/items",
            bobToken, json: new { fileItemIds = new[] { bobFile } });
        Assert.Equal(HttpStatusCode.NotFound, foreignAlbum.StatusCode);

        // Alice's FILE into Bob's album → silently skipped (no existence leak).
        var foreignFile = await TvSendAsync(
            bobCookie, HttpMethod.Post, $"/api/tv/personal/gallery/albums/{bobAlbumId}/items",
            bobToken, json: new { fileItemIds = new[] { aliceFile, bobFile } });
        Assert.Equal(HttpStatusCode.OK, foreignFile.StatusCode);
        var result = JsonDocument.Parse(await foreignFile.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Bulk_Destinations_Are_Partial_Owner_Scoped_And_Trash_Is_Soft_Delete()
    {
        var (ownerId, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var ownFile = await UploadAsync(owner, "tv-destination.png");
        var (_, foreignOwner) = await _factory.CreateAuthenticatedClientAsync("foreign-destination@example.com");
        var foreignFile = await UploadAsync(foreignOwner, "foreign.png");
        var ids = new[] { ownFile, foreignFile };

        foreach (var url in new[]
        {
            "/api/tv/personal/gallery/add-to-beauty-lab",
            "/api/tv/personal/gallery/add-to-plates",
        })
        {
            var response = await TvSendAsync(
                cookie, HttpMethod.Post, url, token, json: new { fileItemIds = ids });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertNoStore(response);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("blob", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storage", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha", raw, StringComparison.OrdinalIgnoreCase);
            var result = JsonDocument.Parse(raw).RootElement;
            Assert.Equal(1, result.GetProperty("succeeded").GetInt32());
            Assert.Equal(1, result.GetProperty("skipped").GetInt32());
            Assert.Equal(ownFile, result.GetProperty("succeededItemIds")[0].GetGuid());
            Assert.Equal(foreignFile, result.GetProperty("failures")[0].GetProperty("itemId").GetGuid());
        }

        var trash = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/gallery/trash", token,
            json: new { fileItemIds = ids });
        Assert.Equal(HttpStatusCode.OK, trash.StatusCode);
        var trashResult = JsonDocument.Parse(await trash.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, trashResult.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, trashResult.GetProperty("skipped").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await db.FileItems.SingleAsync(f => f.Id == ownFile)).DeletedAt);
        Assert.Null((await db.FileItems.SingleAsync(f => f.Id == foreignFile)).DeletedAt);
        Assert.Single(await db.AestheticLabItems.Where(item => item.OwnerUserId == ownerId).ToListAsync());
        Assert.Single(await db.PlateImages.Where(item => item.OwnerUserId == ownerId).ToListAsync());
        Assert.Empty(await db.AestheticAnalysisRuns.Where(run => run.OwnerUserId == ownerId).ToListAsync());
        Assert.Empty(await db.PlateAnalysisJobs.Where(job => job.OwnerUserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task Tv_Personal_Bulk_Requests_Are_Bounded()
    {
        var (_, _, cookie, token) = await PairedUnlockedOwnerAsync();
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        var response = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/gallery/add-to-plates", token,
            json: new { fileItemIds = ids });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("batch_limit_exceeded", await response.Content.ReadAsStringAsync());
    }

    // ── viewer metadata projection ──────────────────────────────────────────

    [Fact]
    public async Task Media_Info_Is_The_Curated_Owner_View_Without_Sensitive_Fields()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var fileId = await UploadAsync(owner, "info.png");
        (await owner.PatchAsJsonAsync($"/api/files/{fileId}/metadata", new
        {
            title = "Tramonto",
            rating = 5,
            favorite = true,
            tags = new[] { "estate" },
            locationOverride = "Lago di Como",
        })).EnsureSuccessStatusCode();

        var response = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/media/{fileId}/info", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var raw = await response.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Tramonto", dto.GetProperty("title").GetString());
        Assert.Equal(5, dto.GetProperty("rating").GetInt32());
        Assert.True(dto.GetProperty("favorite").GetBoolean());
        Assert.Equal("Lago di Como", dto.GetProperty("location").GetString());
        Assert.False(dto.GetProperty("hasGps").GetBoolean());
        // GPS presence only — never coordinates; no storage/AI internals.
        Assert.DoesNotContain("latitude", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── server-authoritative total count ─────────────────────────────────────

    // The total count must equal the number of ordered ids the SAME filter set
    // yields (web + TV agree, since both go through the shared query). This one
    // assertion covers: unfiltered total, every supported filter, filter
    // combinations, duplicate-collapse totals, and "count and ordered ids agree".
    [Theory]
    [InlineData("")]
    [InlineData("q=sunset")]
    [InlineData("q=vacanza")]
    [InlineData("favorite=true")]
    [InlineData("favorite=false")]
    [InlineData("minRating=3")]
    [InlineData("hasGps=true")]
    [InlineData("hasGps=false")]
    [InlineData("dateTakenFrom=2023-06-01T00:00:00Z")]
    [InlineData("dateTakenTo=2023-06-01T00:00:00Z")]
    [InlineData("dateTakenFrom=2023-01-01T00:00:00Z&dateTakenTo=2023-12-31T23:59:59Z")]
    [InlineData("collapseDuplicates=true")]
    [InlineData("q=photo&minRating=2&collapseDuplicates=true")]
    [InlineData("favorite=true&sort=name&direction=asc")]
    public async Task Total_Count_Equals_The_Ordered_Id_Count_For_Every_Filter(string query)
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        await SeedLibraryAsync(owner);

        var webIds = await WebAllIdsAsync(owner, query, pageSize: 3);
        var tvIds = await TvAllIdsAsync(cookie, token, query, pageSize: 3);
        // Fetch a small first page so the count is clearly independent of paging.
        var firstPage = await TvGalleryAsync(
            cookie, token, "?limit=2" + (query.Length > 0 ? $"&{query}" : ""));

        Assert.Equal(webIds, tvIds);           // ordering parity (guards the fixture)
        Assert.Equal(webIds.Count, firstPage.TotalCount);
        Assert.Equal(tvIds.Count, firstPage.TotalCount);
    }

    [Fact]
    public async Task Total_Count_Is_Independent_Of_Page_Size()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        for (var i = 0; i < 7; i += 1)
        {
            await UploadAsync(owner, $"count-{i:D2}.png");
        }

        var small = await TvGalleryAsync(cookie, token, "?limit=1");
        var large = await TvGalleryAsync(cookie, token, "?limit=200");
        Assert.Equal(7, small.TotalCount);
        Assert.Equal(7, large.TotalCount);
        // The page payloads differ; the total does not.
        Assert.Single(small.Items);
        Assert.Equal(7, large.Items.Count);
    }

    [Fact]
    public async Task Second_Page_Reports_The_Same_Total_As_The_First()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        for (var i = 0; i < 5; i += 1)
        {
            await UploadAsync(owner, $"seq-{i:D2}.png");
        }

        var first = await TvGalleryAsync(cookie, token, "?limit=2&sort=name&direction=asc");
        Assert.Equal(5, first.TotalCount);
        Assert.NotNull(first.NextCursor);

        var second = await TvGalleryAsync(
            cookie, token,
            $"?limit=2&sort=name&direction=asc&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(5, second.TotalCount);
    }

    [Fact]
    public async Task Duplicate_Collapse_Total_Counts_Collapsed_Rows_Not_Raw_Rows()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        // Two distinct blobs + one blob referenced twice (identical bytes dedup
        // to the same content-addressed blob at upload).
        await UploadAsync(owner, "one.png", PngBytes(10, 10));
        await UploadAsync(owner, "two.png", PngBytes(11, 11));
        var dupBytes = PngBytes(12, 12);
        await UploadAsync(owner, "dup-a.png", dupBytes);
        await UploadAsync(owner, "dup-b.png", dupBytes);

        var raw = await TvGalleryAsync(cookie, token, "?limit=200");
        Assert.Equal(4, raw.TotalCount);                 // every FileItem

        var collapsed = await TvGalleryAsync(cookie, token, "?limit=200&collapseDuplicates=true");
        Assert.Equal(3, collapsed.TotalCount);           // the dup pair counts once
        Assert.Equal(collapsed.Items.Count, collapsed.TotalCount);
    }

    [Fact]
    public async Task People_Filter_Totals_Match_The_Ordered_Id_Count_All_And_Any()
    {
        var (ownerId, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var withPerson = await UploadAsync(owner, "with.png", PngBytes(24, 24));
        await UploadAsync(owner, "without.png", PngBytes(40, 40));
        var personId = await SeedPersonOnFileAsync(ownerId, withPerson, "Anna");

        foreach (var query in new[]
        {
            $"includePeople={personId}",
            $"includePeople={personId}&includePeopleMode=any",
            $"excludePeople={personId}",
        })
        {
            var ids = await TvAllIdsAsync(cookie, token, query, pageSize: 50);
            var page = await TvGalleryAsync(cookie, token, $"?limit=50&{query}");
            Assert.Equal(ids.Count, page.TotalCount);
        }
    }

    [Fact]
    public async Task Total_Count_Excludes_Non_Gallery_And_Deleted_Media()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        var img = await UploadAsync(owner, "shown.png");
        await UploadAsync(owner, "notes.txt", bytes: [1, 2, 3], mime: "text/plain");
        var deleted = await UploadAsync(owner, "gone.png");

        var before = await TvGalleryAsync(cookie, token, "?limit=200");
        Assert.Equal(2, before.TotalCount);              // the text file never counts

        await SoftDeleteAsync(deleted);
        var after = await TvGalleryAsync(cookie, token, "?limit=200");
        Assert.Equal(1, after.TotalCount);               // the deleted image drops out
        Assert.Equal(img, Assert.Single(after.Items).Id);
    }

    [Fact]
    public async Task Total_Count_Is_Zero_When_No_Media_Matches()
    {
        var (_, owner, cookie, token) = await PairedUnlockedOwnerAsync();
        await UploadAsync(owner, "only.png");

        var page = await TvGalleryAsync(cookie, token, "?q=nothingmatchesthis");
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Total_Count_Is_Owner_Scoped()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-count@example.com");
        await UploadAsync(alice, "alice-1.png");
        await UploadAsync(alice, "alice-2.png");

        var (_, bob, bobCookie, bobToken) = await PairedUnlockedOwnerAsync();
        await UploadAsync(bob, "bob-1.png");

        var page = await TvGalleryAsync(bobCookie, bobToken, "?limit=200");
        Assert.Equal(1, page.TotalCount);                // never sees Alice's media
    }

    // ── seeding + helpers ───────────────────────────────────────────────────

    // A small but discriminating library: distinct names/sizes for sort
    // tie-breaks, favorites, ratings, capture-date overrides, tags/titles for q,
    // a duplicated blob for collapseDuplicates, and one GPS-tagged blob.
    private async Task SeedLibraryAsync(HttpClient owner)
    {
        var sunset = await UploadAsync(owner, "sunset-photo.png", PngBytes(24, 24));
        var beach = await UploadAsync(owner, "beach-photo.png", PngBytes(32, 32));
        var forest = await UploadAsync(owner, "forest.png", PngBytes(16, 16));
        var duplicateBytes = PngBytes(20, 20);
        await UploadAsync(owner, "dup-one.png", duplicateBytes);
        await UploadAsync(owner, "dup-two.png", duplicateBytes);

        await PatchMetaAsync(owner, sunset, new
        {
            favorite = true,
            rating = 4,
            tags = new[] { "vacanza" },
            dateTakenOverride = "2023-06-15T10:00:00Z",
        });
        await PatchMetaAsync(owner, beach, new
        {
            rating = 2,
            title = "Vacanza al mare",
            dateTakenOverride = "2023-01-05T09:00:00Z",
        });
        await PatchMetaAsync(owner, forest, new
        {
            favorite = true,
            rating = 3,
            dateTakenOverride = "2024-03-20T18:30:00Z",
        });

        // GPS presence is blob-derived — seed it directly (uploads carry none).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var beachBlobId = await db.FileItems
            .Where(f => f.Id == beach)
            .Select(f => f.BlobObjectId)
            .SingleAsync();
        await db.BlobMetadata
            .Where(m => m.BlobObjectId == beachBlobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.GpsLatitude, 45.0)
                .SetProperty(m => m.GpsLongitude, 9.0));
    }

    private async Task<Guid> SeedPersonOnFileAsync(Guid ownerUserId, Guid fileId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var blobId = await db.FileItems
            .Where(f => f.Id == fileId)
            .Select(f => f.BlobObjectId)
            .SingleAsync();

        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Onnx,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face, Version = 1,
            Dimension = 512, DistanceMetric = AiDistanceMetrics.Cosine, Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = 512, DistanceMetric = AiDistanceMetrics.Cosine, IsDefault = false,
            Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);

        var personId = Guid.NewGuid();
        db.People.Add(new Person
        {
            Id = personId, OwnerUserId = ownerUserId, DisplayName = name, CreatedAt = DateTime.UtcNow,
        });
        var detectionId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = detectionId, BlobObjectId = blobId, ProfileId = profile.Id, FaceIndex = 0,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, PersonId = personId,
            FaceDetectionId = detectionId, Source = PersonFaceAssignmentSources.UserConfirmed,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return personId;
    }

    private async Task<(Guid OwnerId, HttpClient Owner, string Cookie, string Token)> PairedUnlockedOwnerAsync()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);
        return (ownerId, owner, cookie, token);
    }

    // Fetches EVERY page (cursor loop) from /api/images and returns the ordered ids.
    private static async Task<List<Guid>> WebAllIdsAsync(HttpClient owner, string query, int pageSize)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard += 1)
        {
            var url = $"/api/images?limit={pageSize}"
                + (query.Length > 0 ? $"&{query}" : "")
                + (cursor is not null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            var response = await owner.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = (await response.Content.ReadFromJsonAsync<ImageListResponse>())!;
            ids.AddRange(page.Items.Select(i => i.Id));
            if (page.NextCursor is null) break;
            cursor = page.NextCursor;
        }
        return ids;
    }

    private async Task<List<Guid>> TvAllIdsAsync(string cookie, string token, string query, int pageSize)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard += 1)
        {
            var suffix = $"?limit={pageSize}"
                + (query.Length > 0 ? $"&{query}" : "")
                + (cursor is not null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            var page = await TvGalleryAsync(cookie, token, suffix);
            ids.AddRange(page.Items.Select(i => i.Id));
            if (page.NextCursor is null) break;
            cursor = page.NextCursor;
        }
        return ids;
    }

    private async Task<TvPersonalGalleryPageDto> TvGalleryAsync(string cookie, string token, string suffix)
    {
        var response = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/gallery{suffix}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TvPersonalGalleryPageDto>())!;
    }

    private async Task SoftDeleteAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.FileItems
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, DateTime.UtcNow));
    }

    private static byte[] PngBytes(int w = 16, int h = 16)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadAsync(
        HttpClient client, string name, byte[]? bytes = null, string mime = "image/png")
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? PngBytes());
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        multipart.Add(part, "file", name);
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private static async Task PatchMetaAsync(HttpClient client, Guid fileId, object body)
    {
        var response = await client.PatchAsJsonAsync($"/api/files/{fileId}/metadata", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = Pin,
                personalPinConfirmation = Pin,
            })).EnsureSuccessStatusCode();

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string setCookie)
    {
        var response = await TvSendAsync(
            setCookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { pin = Pin });
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(
        string setCookie, HttpMethod method, string url, string? grant = null, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        if (grant is not null)
        {
            request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        }
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        return _factory.CreateClient().SendAsync(request);
    }

    private static void AssertNoStore(HttpResponseMessage response)
        => Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}

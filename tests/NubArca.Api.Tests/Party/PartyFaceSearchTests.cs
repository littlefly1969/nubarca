using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Party;

// Album-scoped, owner-scoped anonymous "find your face" party search. Driven
// end-to-end through the real HTTP + job stack with the DETERMINISTIC face
// backend (emits stable landmarked faces + embeddings). Because the deterministic
// face embedding is a function of the whole image bytes, an identical image used
// as BOTH an album photo and the uploaded selfie yields cosine 1.0 → a match,
// while a different image does not. No ONNX weights required.
//
// SAFETY invariants under test: album-scoped, owner-scoped, no cross-owner, no
// vectors/scores/face-ids/person-ids/names, moderation-excluded, capability-aware
// unavailable state, selfie/query-vector never stored.
public sealed class PartyFaceSearchTests
{
    // A high search threshold so only the near-identical selfie (cosine ~1.0)
    // matches; unrelated deterministic 32-dim vectors stay well below it.
    private static SqliteWebApplicationFactory FacesEnabledFactory() => Factory(
        ("Ai:Enabled", "true"),
        ("Ai:FaceDetectionEnabled", "true"),
        ("Ai:FaceEmbeddingsEnabled", "true"),
        ("Ai:Face:SearchDefaultSimilarityThreshold", "0.9"));

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var f = new SqliteWebApplicationFactory(dict, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task SeedProfilesAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>().SeedDeterministicProfilesAsync();
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // ---- required scenarios ---------------------------------------------

    [Fact]
    public async Task Face_Search_Rejects_Invalid_Token()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var anon = f.CreateClient();

        var resp = await FaceSearchAsync(anon, "not-a-real-token", Png(16));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Face_Search_Rejects_Disabled_Party_Mode()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var anon = f.CreateClient();

        Assert.NotEqual(HttpStatusCode.NotFound, (await FaceSearchAsync(anon, viewToken, Png(16))).StatusCode);

        // Disable party → the same token no longer resolves.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await FaceSearchAsync(anon, viewToken, Png(16))).StatusCode);
    }

    [Fact]
    public async Task Face_Search_Returns_Album_Scoped_Matching_Items_Only()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        // The photo the selfie will match (identical bytes) + a different photo.
        var matchId = await AddPngAsync(owner, albumId, "match.png", Png(16));
        await AddPngAsync(owner, albumId, "other.png", Png(24));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(16)));

        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("resultCount").GetInt32());
        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(matchId, items[0].GetProperty("id").GetGuid());
        Assert.Equal("image", items[0].GetProperty("mediaType").GetString());
    }

    [Fact]
    public async Task Face_Search_Does_Not_Cross_Owner_Boundary()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var albumA = await CreateAlbumAsync(owner, "A Party");
        var matchId = await AddPngAsync(owner, albumA, "a.png", Png(16));
        var status = await EnablePartyAsync(owner, albumA);
        var viewToken = ViewTokenFromStatus(status);

        // A DIFFERENT owner has an identical photo in their own (non-shared) album.
        var (_, other) = await f.CreateAuthenticatedClientAsync("b@example.com");
        var albumB = await CreateAlbumAsync(other, "B Album");
        await AddPngAsync(other, albumB, "b.png", Png(16));

        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(16)));

        // Only owner A's album photo is ever returned; owner B's identical photo
        // (different owner + album) is never a candidate.
        Assert.Equal(1, body.GetProperty("resultCount").GetInt32());
        Assert.Equal(matchId, body.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Face_Search_Excludes_Hidden_Guest_Upload_On_Every_Read()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var uploadToken = UploadTokenFromStatus(status);

        // A guest uploads the photo the selfie will match.
        var anon = f.CreateClient();
        await UploadAsync(anon, uploadToken, ("g.png", Png(16), "image/png"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);
        await RunFaceJobsAsync(f);

        // Match found while approved.
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(16)));
        Assert.Equal(1, body.GetProperty("resultCount").GetInt32());
        var searchId = body.GetProperty("searchId").GetGuid();

        // Owner hides it → re-reading the SAME search now excludes it.
        (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/hide", null))
            .EnsureSuccessStatusCode();
        var reread = await ReadJsonAsync(
            await anon.GetAsync($"/api/party/{viewToken}/face-search/{searchId}"));
        Assert.Equal(0, reread.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Face_Search_Public_Get_404s_After_Party_Disabled()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(16)));
        var searchId = body.GetProperty("searchId").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await anon.GetAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);
    }

    [Fact]
    public async Task Face_Search_Unavailable_When_Ai_Disabled()
    {
        // AI substrate OFF → capability unavailable → safe 503 unavailable state.
        using var f = Factory();
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);

        var anon = f.CreateClient();
        var resp = await FaceSearchAsync(anon, viewToken, Png(16));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Face_Search_Unavailable_When_Feature_Switched_Off()
    {
        using var f = Factory(
            ("Ai:Enabled", "true"),
            ("Ai:FaceDetectionEnabled", "true"),
            ("Ai:FaceEmbeddingsEnabled", "true"),
            ("Party:FaceSearch:Enabled", "false"));
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);

        var anon = f.CreateClient();
        var resp = await FaceSearchAsync(anon, viewToken, Png(16));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("unavailable", (await ReadJsonAsync(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Face_Search_Rejects_Non_Image_Upload()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);

        var anon = f.CreateClient();
        // Bytes that lie about being an image → decode-validated as invalid.
        var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var resp = await FaceSearchAsync(anon, viewToken, junk, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_image", (await ReadJsonAsync(resp)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Face_Search_No_Face_Is_Reported_Safely()
    {
        // The deterministic detector always finds a face in a real image, so the
        // no-face branch is exercised at the service level with a stub detector
        // that returns zero faces. The image itself is a valid PNG (passes the
        // decode gate), so we reach detection and get the localized "no_face" code.
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync();
        Guid albumId;
        using (var setup = f.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var album = new NubArca.Api.Domain.Album
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerId,
                Name = "Party",
                ShowOnTv = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Albums.Add(album);
            await db.SaveChangesAsync();
            albumId = album.Id;
        }

        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = new PartyFaceSearchService(
            sp.GetRequiredService<AppDbContext>(),
            new ZeroFaceResolver("det-face-embedding-v1"),
            sp.GetRequiredService<IAiProfileRegistry>(),
            sp.GetRequiredService<NubArca.Api.Ai.Faces.IFaceSettingsProvider>(),
            sp.GetRequiredService<IAiVectorSerializer>(),
            sp.GetRequiredService<NubArca.Api.Storage.IBlobService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

        var outcome = await service.SearchAsync(ownerId, albumId, null, Png(16), "image/png");
        Assert.Equal("no_face", outcome.Status);
        Assert.Null(outcome.SearchId);
        Assert.Empty(outcome.FileItemIds);
    }

    [Fact]
    public async Task Face_Search_Response_Exposes_No_Internals()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var raw = await (await FaceSearchAsync(anon, viewToken, Png(16))).Content.ReadAsStringAsync();
        foreach (var needle in new[]
        {
            "StorageKey", "BlobObjectId", "sha256", "TokenHash", "embedding", "vector",
            "similarity", "score", "faceId", "personId", "person", "cluster", "landmark",
            "PayloadJson", "/storage/objects/", "at NubArca.",
        })
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- TV active face search ------------------------------------------

    [Fact]
    public async Task Tv_Active_Face_Search_Requires_Tv_Session()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await EnablePartyAsync(owner, albumId);

        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/albums/{albumId}/face-search/active")).StatusCode);
    }

    [Fact]
    public async Task Completed_Search_Does_Not_Activate_Tv()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        // A completed search filters ONLY the guest's phone — the TV stays
        // unchanged until an explicit activation.
        var anon = f.CreateClient();
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(16)));
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("resultCount").GetInt32());

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.False(active.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Activation_Sets_Tv_Filter_With_Face_Thumbnail()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var matchId = await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var searchId = await SearchReadyAsync(anon, viewToken, Png(16));

        var activation = await anon.PostAsync(
            $"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null);
        activation.EnsureSuccessStatusCode();
        var actBody = await ReadJsonAsync(activation);
        Assert.Equal(searchId, actBody.GetProperty("searchId").GetGuid());
        Assert.True(actBody.GetProperty("activationVersion").GetInt64() >= 1);

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.True(active.GetProperty("active").GetBoolean());
        Assert.Equal(searchId, active.GetProperty("searchId").GetGuid());
        Assert.True(active.GetProperty("activationVersion").GetInt64() >= 1);
        Assert.NotEqual(default, active.GetProperty("activatedAt").GetDateTime());
        var items = active.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(matchId, items[0].GetProperty("id").GetGuid());
        Assert.StartsWith("/api/tv/media/", items[0].GetProperty("previewUrl").GetString());

        // The indicator face thumbnail is TV-scoped and serves a real image.
        var thumbUrl = active.GetProperty("faceThumbnailUrl").GetString();
        Assert.Equal($"/api/tv/albums/{albumId}/face-search/{searchId}/face-thumbnail", thumbUrl);
        var thumb = await TvSendAsync(f, HttpMethod.Get, cookie, thumbUrl!);
        thumb.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType!.MediaType);
        Assert.True((await thumb.Content.ReadAsByteArrayAsync()).Length > 0);

        // No TV session → 401; a different owner's TV → 404 (no leak).
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(thumbUrl)).StatusCode);
        var (_, other) = await f.CreateAuthenticatedClientAsync("thumb-other@example.com");
        var otherCookie = await PairTvAsync(f, other);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(f, HttpMethod.Get, otherCookie, thumbUrl!)).StatusCode);
    }

    [Fact]
    public async Task Tv_Active_FaceSearch_Items_Carry_Display_Dimensions()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var matchId = await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        // The matched photo's display dimensions must flow through the SAME TV
        // projection the album-items list uses — no divergent face-search shape.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await db.FileItems.FirstAsync(x => x.Id == matchId);
            item.Width = 1200;
            item.Height = 800;
            await db.SaveChangesAsync();
        }

        var anon = f.CreateClient();
        var searchId = await SearchReadyAsync(anon, viewToken, Png(16));
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null))
            .EnsureSuccessStatusCode();

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        var item0 = active.GetProperty("items")[0];
        Assert.Equal(matchId, item0.GetProperty("id").GetGuid());
        Assert.Equal(1200, item0.GetProperty("width").GetInt32());
        Assert.Equal(800, item0.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Empty_Search_Cannot_Be_Activated()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        // A selfie that matches nothing → ready search with zero results.
        var anon = f.CreateClient();
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, viewToken, Png(48)));
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("resultCount").GetInt32());
        var searchId = body.GetProperty("searchId").GetGuid();

        var activation = await anon.PostAsync(
            $"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null);
        Assert.Equal(HttpStatusCode.Conflict, activation.StatusCode);
        Assert.Equal("no_matches",
            (await ReadJsonAsync(activation)).GetProperty("error").GetString());

        // The TV stays unchanged and the empty search remains cancellable.
        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.False(active.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.DeleteAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);
    }

    [Fact]
    public async Task Newer_Activation_Replaces_Previous_And_Stale_Activation_Is_Rejected()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var searchA = await SearchReadyAsync(anon, viewToken, Png(16));
        await Task.Delay(50); // ensure a strictly newer server CreatedAt for B
        var searchB = await SearchReadyAsync(anon, viewToken, Png(16));

        // Activate A, then B → the newest server-accepted activation wins.
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchA}/activate-tv", null))
            .EnsureSuccessStatusCode();
        var activeA = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.Equal(searchA, activeA.GetProperty("searchId").GetGuid());
        var versionA = activeA.GetProperty("activationVersion").GetInt64();

        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchB}/activate-tv", null))
            .EnsureSuccessStatusCode();
        var activeB = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.Equal(searchB, activeB.GetProperty("searchId").GetGuid());
        Assert.True(activeB.GetProperty("activationVersion").GetInt64() > versionA);

        // A stale (older) activation must not overwrite the newer active filter.
        var stale = await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchA}/activate-tv", null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_search", (await ReadJsonAsync(stale)).GetProperty("error").GetString());
        var still = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.Equal(searchB, still.GetProperty("searchId").GetGuid());
    }

    [Fact]
    public async Task Phone_Cancellation_Removes_Tv_State_Search_And_Image()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var searchId = await SearchReadyAsync(anon, viewToken, Png(16));
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null))
            .EnsureSuccessStatusCode();

        var cropBlobId = await FaceCropBlobIdAsync(f, searchId);
        Assert.NotNull(cropBlobId);

        // Cancel from the phone → 204; TV filter gone; session + rank rows gone;
        // the stored face crop's blob reference released.
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.DeleteAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.False(active.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PartyFaceSearchSessions.AnyAsync(s => s.Id == searchId));
        Assert.False(await db.PartyFaceSearchResults.AnyAsync(r => r.PartyFaceSearchSessionId == searchId));
        var crop = await db.BlobObjects.FirstOrDefaultAsync(b => b.Id == cropBlobId.Value);
        Assert.NotNull(crop);
        Assert.Equal(0, crop!.ReferenceCount);

        // Repeated deletion stays a safe no-op.
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.DeleteAsync($"/api/party/{viewToken}/face-search/{searchId}")).StatusCode);
    }

    [Fact]
    public async Task Tv_Back_Deletes_Search_And_Image_And_Restores()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var searchId = await SearchReadyAsync(anon, viewToken, Png(16));
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null))
            .EnsureSuccessStatusCode();
        var cropBlobId = await FaceCropBlobIdAsync(f, searchId);

        // TV BACK → deletes the search (row-scoped) + crop; idempotent on repeat.
        Assert.Equal(HttpStatusCode.NoContent,
            (await TvSendAsync(f, HttpMethod.Delete, cookie,
                $"/api/tv/albums/{albumId}/face-search/active?searchId={searchId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await TvSendAsync(f, HttpMethod.Delete, cookie,
                $"/api/tv/albums/{albumId}/face-search/active?searchId={searchId}")).StatusCode);

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.False(active.GetProperty("active").GetBoolean());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PartyFaceSearchSessions.AnyAsync(s => s.Id == searchId));
        var crop = await db.BlobObjects.FirstOrDefaultAsync(b => b.Id == cropBlobId!.Value);
        Assert.Equal(0, crop!.ReferenceCount);
    }

    [Fact]
    public async Task Stale_Cancellation_Does_Not_Remove_Newer_Active_Filter()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(f, owner);
        await RunFaceJobsAsync(f);

        var anon = f.CreateClient();
        var searchA = await SearchReadyAsync(anon, viewToken, Png(16));
        await Task.Delay(50);
        var searchB = await SearchReadyAsync(anon, viewToken, Png(16));
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchB}/activate-tv", null))
            .EnsureSuccessStatusCode();

        // Cancelling the OLDER search (phone) and a stale TV BACK for it must not
        // touch B's active filter.
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.DeleteAsync($"/api/party/{viewToken}/face-search/{searchA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await TvSendAsync(f, HttpMethod.Delete, cookie,
                $"/api/tv/albums/{albumId}/face-search/active?searchId={searchA}")).StatusCode);

        var active = await TvJsonAsync(f, cookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.True(active.GetProperty("active").GetBoolean());
        Assert.Equal(searchB, active.GetProperty("searchId").GetGuid());
    }

    [Fact]
    public async Task Tv_Active_Face_Search_Does_Not_Leak_Across_Owners()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, owner) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddPngAsync(owner, albumId, "m.png", Png(16));
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        await RunFaceJobsAsync(f);
        var anon = f.CreateClient();
        var searchId = await SearchReadyAsync(anon, viewToken, Png(16));
        (await anon.PostAsync($"/api/party/{viewToken}/face-search/{searchId}/activate-tv", null))
            .EnsureSuccessStatusCode();

        // A DIFFERENT owner's paired TV must not see owner A's active filter.
        var (_, other) = await f.CreateAuthenticatedClientAsync("b@example.com");
        var otherCookie = await PairTvAsync(f, other);
        var seen = await TvJsonAsync(f, otherCookie, $"/api/tv/albums/{albumId}/face-search/active");
        Assert.False(seen.GetProperty("active").GetBoolean());
    }

    // ---- helpers ---------------------------------------------------------

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var resp = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var fileId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static Task<HttpResponseMessage> FaceSearchAsync(
        HttpClient anon, string token, byte[] bytes, string contentType = "image/png")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var multipart = new MultipartFormDataContent { { part, "file", "selfie" } };
        return anon.PostAsync($"/api/party/{token}/face-search", multipart);
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient anon, string uploadToken, params (string Name, byte[] Bytes, string ContentType)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (name, bytes, contentType) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(part, "file", name);
        }
        return anon.PostAsync($"/api/party/{uploadToken}/upload", multipart);
    }

    private static async Task<Guid> FirstUploadFileIdAsync(HttpClient owner, Guid albumId)
    {
        var uploads = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-uploads");
        return uploads.GetProperty("items")[0].GetProperty("fileItemId").GetGuid();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    // Run a face search that must come back "ready" and return its search id.
    private static async Task<Guid> SearchReadyAsync(HttpClient anon, string token, byte[] selfie)
    {
        var body = await ReadJsonAsync(await FaceSearchAsync(anon, token, selfie));
        Assert.Equal("ready", body.GetProperty("status").GetString());
        return body.GetProperty("searchId").GetGuid();
    }

    private static async Task<Guid?> FaceCropBlobIdAsync(SqliteWebApplicationFactory f, Guid searchId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PartyFaceSearchSessions.AsNoTracking()
            .FirstAsync(s => s.Id == searchId)).FaceCropBlobObjectId;
    }

    private static string ViewTokenFromStatus(JsonElement status)
        => status.GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!;
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
    }

    // Run detection + embedding backfill jobs so album photos have face embeddings.
    private static async Task RunFaceJobsAsync(SqliteWebApplicationFactory f)
    {
        await RunJobAsync(f, JobTypes.AiFacesDetectBackfill);
        await RunJobAsync(f, JobTypes.AiFacesEmbeddingsBackfill);
    }

    private static async Task RunJobAsync(SqliteWebApplicationFactory f, string jobType)
    {
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .EnqueueAsync(jobType, new NubArca.Api.Ai.Jobs.AiBackfillJobPayload());
        }
        for (var i = 0; i < 50; i++)
        {
            using var scope = f.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0)
            {
                break;
            }
        }
    }

    private static async Task<string> PairTvAsync(SqliteWebApplicationFactory f, HttpClient owner)
    {
        var tvClient = f.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                // Atomic first pairing: approval creates the owner's PIN when
                // missing; ignored for owners who already have one.
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();
        var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private static async Task<JsonElement> TvJsonAsync(SqliteWebApplicationFactory f, string setCookie, string url)
    {
        var response = await TvSendAsync(f, HttpMethod.Get, setCookie, url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> TvSendAsync(
        SqliteWebApplicationFactory f, HttpMethod method, string setCookie, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return f.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }

    // A resolver that reports a face package available but whose detector finds no
    // faces — used only to exercise the safe "no_face" branch deterministically.
    private sealed class ZeroFaceResolver : IAiBackendResolver
    {
        private readonly string _profileKey;
        public ZeroFaceResolver(string profileKey) => _profileKey = profileKey;

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Build<T>(capability));

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Build<T>(AiCapabilities.FaceEmbedding));

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(Res(capability));

        private AiResolution Res(string capability) => new()
        {
            IsAvailable = true,
            Capability = capability,
            Provider = "deterministic",
            ProfileKey = _profileKey,
            Dimension = 32,
            DistanceMetric = AiDistanceMetrics.Cosine,
        };

        private AiBackendResolution<T> Build<T>(string capability) where T : class, IAiBackend
        {
            IAiBackend backend = new ZeroFaceBackend();
            return AiBackendResolution<T>.Available((T)backend, Res(capability));
        }
    }

    private sealed class ZeroFaceBackend : IFaceDetector, IFaceEmbedder
    {
        public string Provider => "deterministic";
        public bool Supports(string capability) =>
            capability is AiCapabilities.FaceDetection or AiCapabilities.FaceEmbedding;

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiFaceDetectionResult(Array.Empty<DetectedFace>()));

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiEmbeddingResult(new float[32], 32, AiDistanceMetrics.Cosine));
    }
}

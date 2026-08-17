using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

// Mixed photo+video guest upload with per-participant, per-kind quotas.
//
// The two things worth protecting here are that the SERVER decides what a file
// is (so a renamed script cannot become party content) and that the quota is
// decided by the DATABASE in one statement (so two phones racing for the last
// slot cannot both win). Everything else follows from those.
public sealed class PartyMixedMediaTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyMixedMediaTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // --- A. IMAGE (existing behaviour must survive the generalisation) ---

    [Fact]
    public async Task Valid_Image_Is_Accepted_And_Counted_As_A_Photo()
    {
        var (token, _) = await PartyAsync();
        var anon = _factory.CreateClient();

        var body = await UploadJsonAsync(anon, token, ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("acceptedPhotos").GetInt32());
        Assert.Equal(0, body.GetProperty("acceptedVideos").GetInt32());
    }

    [Fact]
    public async Task Fake_Image_Is_Rejected_By_Server_Detection()
    {
        var (token, albumId) = await PartyAsync();
        var anon = _factory.CreateClient();

        var body = await UploadJsonAsync(
            anon, token, ("fake.png", Encoding.UTF8.GetBytes("definitely not an image"), "image/png"));
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
        await AssertAlbumIsEmptyAsync(albumId);
    }

    // --- B. VIDEO ---

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("clip.webm", "video/webm")]
    public async Task Valid_Video_Containers_Are_Accepted_And_Counted_As_Videos(string name, string contentType)
    {
        var (token, albumId) = await PartyAsync();
        var anon = _factory.CreateClient();
        var bytes = contentType switch
        {
            "video/quicktime" => ImageFixtures.MinimalMov(),
            "video/webm" => ImageFixtures.MinimalWebm(),
            _ => ImageFixtures.MinimalMp4(),
        };

        var body = await UploadJsonAsync(anon, token, (name, bytes, contentType));
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("acceptedPhotos").GetInt32());
        Assert.Equal(1, body.GetProperty("acceptedVideos").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId));
    }

    [Fact]
    public async Task Script_Renamed_As_Mp4_Is_Rejected_After_Server_Inspection()
    {
        var (token, albumId) = await PartyAsync();
        var anon = _factory.CreateClient();

        // Declared video/mp4 so it passes the cheap pre-gate; the bytes are not a
        // container, so the authoritative post-ingest check removes it. Nothing
        // reaches the album, the public page or the TV.
        var body = await UploadJsonAsync(
            anon, token, ("evil.mp4", Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "video/mp4"));
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
        await AssertAlbumIsEmptyAsync(albumId);
    }

    [Fact]
    public async Task Unsupported_Declared_Type_Is_Rejected_At_The_Door()
    {
        var (token, albumId) = await PartyAsync();
        var anon = _factory.CreateClient();

        var body = await UploadJsonAsync(
            anon, token, ("notes.txt", Encoding.UTF8.GetBytes("hello"), "text/plain"));
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
        await AssertAlbumIsEmptyAsync(albumId);
    }

    [Fact]
    public async Task Accepted_Video_Runs_The_Existing_Party_Post_Ingest_Pipeline()
    {
        var (token, albumId) = await PartyAsync();
        var anon = _factory.CreateClient();
        await UploadJsonAsync(anon, token, ("clip.mp4", ImageFixtures.MinimalMp4(), "video/mp4"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fileId = await db.AlbumItems.Where(ai => ai.AlbumId == albumId)
            .Select(ai => ai.FileItemId).SingleAsync();
        // The SAME pipeline entry point images use — the proof it ran for a video
        // is that the blob was classified as one, which is what the pipeline
        // branches on. No party-specific video path exists.
        var file = await db.FileItems.SingleAsync(f => f.Id == fileId);
        var category = await db.BlobMetadata
            .Where(m => m.BlobObjectId == file.BlobObjectId)
            .Select(m => m.MediaCategory)
            .SingleAsync();
        Assert.Equal(MediaCategories.Video, category);
    }

    // --- C. PARTICIPANT SESSION ---

    [Fact]
    public async Task Same_Browser_Reuses_One_Participant_Session()
    {
        var (token, _) = await PartyAsync();
        var anon = _factory.CreateClient(); // carries cookies across requests

        await SessionAsync(anon, token);
        await UploadJsonAsync(anon, token, ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        await UploadJsonAsync(anon, token, ("b.png", ImageFixtures.PlainPng(), "image/png"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.PartyParticipants.SingleAsync();
        Assert.Equal(2, participant.AcceptedPhotoCount);
    }

    [Fact]
    public async Task A_Different_Browser_Gets_Its_Own_Session_And_Quota()
    {
        var (token, _) = await PartyAsync(maxPhotos: 1);
        var first = _factory.CreateClient();
        var second = _factory.CreateClient();

        Assert.Equal(1, (await UploadJsonAsync(first, token, ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg")))
            .GetProperty("accepted").GetInt32());
        // The second guest is a different participant, so the first guest's
        // exhausted quota does not apply to them.
        Assert.Equal(1, (await UploadJsonAsync(second, token, ("b.png", ImageFixtures.PlainPng(), "image/png")))
            .GetProperty("accepted").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.PartyParticipants.CountAsync());
    }

    [Fact]
    public async Task A_Session_From_Another_Party_Cannot_Be_Reused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var firstAlbum = await CreateAlbumAsync(owner, "Party one");
        var secondAlbum = await CreateAlbumAsync(owner, "Party two");
        var firstToken = UploadTokenFromStatus(await EnablePartyAsync(owner, firstAlbum));
        var secondToken = UploadTokenFromStatus(await EnablePartyAsync(owner, secondAlbum));

        var guest = _factory.CreateClient();
        await SessionAsync(guest, firstToken);
        await SessionAsync(guest, secondToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Two links, two participant rows: the cookie is path-scoped per link, so
        // one party's allowance can never be spent at another.
        Assert.Equal(2, await db.PartyParticipants.CountAsync());
        Assert.Equal(2, await db.PartyParticipants.Select(p => p.PartyAlbumLinkId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Upload_Session_Is_Rejected_For_A_Revoked_Link()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var token = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.PostAsync($"/api/party/{token}/upload-session", null)).StatusCode);
    }

    [Fact]
    public async Task Participant_Token_Is_Stored_As_Hash_Only_And_Never_Returned()
    {
        var (token, _) = await PartyAsync();
        var anon = _factory.CreateClient();
        var raw = await (await anon.PostAsync($"/api/party/{token}/upload-session", null))
            .Content.ReadAsStringAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.PartyParticipants.SingleAsync();
        Assert.Equal(64, participant.TokenHash.Length); // SHA-256 hex

        // The response carries quota only — no participant id, no token, no hash.
        Assert.DoesNotContain(participant.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(participant.TokenHash, raw, StringComparison.OrdinalIgnoreCase);
        foreach (var needle in new[] { "participant", "token", "hash", "StorageKey", "BlobObjectId", "sha256" })
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- D/E/F. QUOTAS ---

    [Fact]
    public async Task Photo_Quota_Accepts_Up_To_The_Limit_Then_Refuses()
    {
        var (token, _) = await PartyAsync(maxPhotos: 2);
        var anon = _factory.CreateClient();

        Assert.Equal(1, (await UploadJsonAsync(anon, token, ("1.jpg", ImageFixtures.JpegWithExif(), "image/jpeg")))
            .GetProperty("accepted").GetInt32());
        Assert.Equal(1, (await UploadJsonAsync(anon, token, ("2.png", ImageFixtures.PlainPng(), "image/png")))
            .GetProperty("accepted").GetInt32());

        var third = await UploadJsonAsync(anon, token, ("3.png", ImageFixtures.PlainPng(), "image/png"));
        Assert.Equal(0, third.GetProperty("accepted").GetInt32());
        Assert.Equal(1, third.GetProperty("quotaRejectedPhotos").GetInt32());
        Assert.Equal(0, third.GetProperty("remainingPhotos").GetInt32());

        var session = await SessionAsync(anon, token);
        Assert.Equal(2, session.GetProperty("usedPhotos").GetInt32());
        Assert.Equal(0, session.GetProperty("remainingPhotos").GetInt32());
    }

    [Fact]
    public async Task Video_Quota_Is_Independent_Of_The_Photo_Quota()
    {
        var (token, _) = await PartyAsync(maxPhotos: 1, maxVideos: 1);
        var anon = _factory.CreateClient();

        await UploadJsonAsync(anon, token, ("1.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        // Photo quota is now full; the video quota is untouched.
        var video = await UploadJsonAsync(anon, token, ("v.mp4", ImageFixtures.MinimalMp4(), "video/mp4"));
        Assert.Equal(1, video.GetProperty("acceptedVideos").GetInt32());

        var session = await SessionAsync(anon, token);
        Assert.Equal(0, session.GetProperty("remainingPhotos").GetInt32());
        Assert.Equal(0, session.GetProperty("remainingVideos").GetInt32());
    }

    [Fact]
    public async Task An_Exhausted_Kind_Never_Blocks_The_Other_Kind()
    {
        var (token, _) = await PartyAsync(maxPhotos: 1, maxVideos: 1);
        var anon = _factory.CreateClient();
        await UploadJsonAsync(anon, token, ("1.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        await UploadJsonAsync(anon, token, ("v.mp4", ImageFixtures.MinimalMp4(), "video/mp4"));

        // Both full: each kind refuses only its own.
        var photo = await UploadJsonAsync(anon, token, ("2.png", ImageFixtures.PlainPng(), "image/png"));
        Assert.Equal(1, photo.GetProperty("quotaRejectedPhotos").GetInt32());
        Assert.Equal(0, photo.GetProperty("quotaRejectedVideos").GetInt32());

        var video = await UploadJsonAsync(anon, token, ("w.mp4", ImageFixtures.MinimalMp4("mp42"), "video/mp4"));
        Assert.Equal(0, video.GetProperty("quotaRejectedPhotos").GetInt32());
        Assert.Equal(1, video.GetProperty("quotaRejectedVideos").GetInt32());
    }

    [Fact]
    public async Task Unlimited_Is_Reported_As_Null_Not_Zero()
    {
        var (token, _) = await PartyAsync(); // both quotas 0 = unlimited
        var anon = _factory.CreateClient();
        var session = await SessionAsync(anon, token);

        Assert.Equal(JsonValueKind.Null, session.GetProperty("maxPhotos").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("remainingPhotos").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("maxVideos").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("remainingVideos").ValueKind);
    }

    // --- G. INVALID MEDIA CONSUMES NOTHING ---

    [Fact]
    public async Task Invalid_And_Oversized_Media_Consume_No_Quota()
    {
        var (token, _) = await PartyAsync(maxPhotos: 2, maxVideos: 2);
        var anon = _factory.CreateClient();

        await UploadJsonAsync(anon, token, ("fake.png", Encoding.UTF8.GetBytes("nope"), "image/png"));
        await UploadJsonAsync(anon, token, ("fake.mp4", Encoding.UTF8.GetBytes("nope"), "video/mp4"));
        await UploadJsonAsync(anon, token, ("x.txt", Encoding.UTF8.GetBytes("nope"), "text/plain"));

        var session = await SessionAsync(anon, token);
        Assert.Equal(0, session.GetProperty("usedPhotos").GetInt32());
        Assert.Equal(0, session.GetProperty("usedVideos").GetInt32());
        Assert.Equal(2, session.GetProperty("remainingPhotos").GetInt32());
        Assert.Equal(2, session.GetProperty("remainingVideos").GetInt32());
    }

    // --- H. MODERATION NEVER REFUNDS ---

    [Fact]
    public async Task Hiding_An_Accepted_Upload_Does_Not_Give_The_Slot_Back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var token = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        await SetSlideshowSettingsAsync(owner, albumId, maxPhotos: 1);

        var anon = _factory.CreateClient();
        await UploadJsonAsync(anon, token, ("1.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));

        Guid fileId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            fileId = await db.AlbumItems.Where(ai => ai.AlbumId == albumId)
                .Select(ai => ai.FileItemId).SingleAsync();
        }

        (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileId}/hide", null))
            .EnsureSuccessStatusCode();

        // Hiding is a VISIBILITY decision. Refunding the slot would let a guest
        // re-upload the very thing the owner just hid, indefinitely.
        var session = await SessionAsync(anon, token);
        Assert.Equal(1, session.GetProperty("usedPhotos").GetInt32());
        Assert.Equal(0, session.GetProperty("remainingPhotos").GetInt32());
        Assert.Equal(0, (await UploadJsonAsync(anon, token, ("2.png", ImageFixtures.PlainPng(), "image/png")))
            .GetProperty("accepted").GetInt32());
    }

    // --- I. SETTINGS CHANGES ---

    [Fact]
    public async Task Lowering_A_Quota_Below_Used_Leaves_Zero_Remaining_And_Deletes_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var token = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        await SetSlideshowSettingsAsync(owner, albumId, maxPhotos: 5);

        var anon = _factory.CreateClient();
        await UploadJsonAsync(anon, token, ("1.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        await UploadJsonAsync(anon, token, ("2.png", ImageFixtures.PlainPng(), "image/png"));

        await SetSlideshowSettingsAsync(owner, albumId, maxPhotos: 1);

        var session = await SessionAsync(anon, token);
        Assert.Equal(2, session.GetProperty("usedPhotos").GetInt32());
        Assert.Equal(0, session.GetProperty("remainingPhotos").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId)); // nothing removed

        // Raising it again makes slots available immediately.
        await SetSlideshowSettingsAsync(owner, albumId, maxPhotos: 3);
        Assert.Equal(1, (await UploadJsonAsync(anon, token, ("3.png", ImageFixtures.PlainPng(), "image/png")))
            .GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Changing_Settings_Does_Not_Rotate_Tokens_Or_Touch_Switches()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var before = await EnablePartyAsync(owner, albumId);
        var viewUrl = before.GetProperty("partyUrl").GetString();
        var uploadUrl = before.GetProperty("uploadUrl").GetString();

        var after = await SetSlideshowSettingsAsync(
            owner, albumId, photoSeconds: 20, maxVideoSeconds: 90, maxPhotos: 7, maxVideos: 3);

        Assert.Equal(viewUrl, after.GetProperty("partyUrl").GetString());
        Assert.Equal(uploadUrl, after.GetProperty("uploadUrl").GetString());
        Assert.True(after.GetProperty("partyMode").GetBoolean());
        Assert.True(after.GetProperty("uploadEnabled").GetBoolean());
        Assert.False(after.GetProperty("requireUploadApproval").GetBoolean());
        Assert.Equal(20, after.GetProperty("photoSlideSeconds").GetInt32());
        Assert.Equal(90, after.GetProperty("maxVideoSlideSeconds").GetInt32());
        Assert.Equal(7, after.GetProperty("maxPhotoUploadsPerParticipant").GetInt32());
        Assert.Equal(3, after.GetProperty("maxVideoUploadsPerParticipant").GetInt32());
    }

    [Fact]
    public async Task Defaults_Are_Nine_Seconds_Sixty_Seconds_And_Unlimited()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);

        Assert.Equal(PartySlideshowDefaults.PhotoSeconds, status.GetProperty("photoSlideSeconds").GetInt32());
        Assert.Equal(PartySlideshowDefaults.MaxVideoSeconds, status.GetProperty("maxVideoSlideSeconds").GetInt32());
        Assert.Equal(0, status.GetProperty("maxPhotoUploadsPerParticipant").GetInt32());
        Assert.Equal(0, status.GetProperty("maxVideoUploadsPerParticipant").GetInt32());
    }

    [Theory]
    [InlineData(2, null, null, null)]   // photoSeconds below min
    [InlineData(61, null, null, null)]  // photoSeconds above max
    [InlineData(null, 4, null, null)]   // maxVideoSeconds below min
    [InlineData(null, 601, null, null)] // maxVideoSeconds above max
    [InlineData(null, null, -1, null)]  // negative quota
    [InlineData(null, null, null, 10001)]
    public async Task Out_Of_Range_Settings_Are_Rejected(
        int? photoSeconds, int? maxVideoSeconds, int? maxPhotos, int? maxVideos)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await EnablePartyAsync(owner, albumId);

        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-slideshow-settings",
            new { photoSlideSeconds = photoSeconds, maxVideoSlideSeconds = maxVideoSeconds, maxPhotoUploadsPerParticipant = maxPhotos, maxVideoUploadsPerParticipant = maxVideos });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- J. CONCURRENCY ---

    [Fact]
    public async Task Concurrent_Claims_For_The_Last_Photo_Slot_Admit_Exactly_One()
    {
        await AssertLastSlotIsClaimedOnceAsync(isVideo: false);
    }

    [Fact]
    public async Task Concurrent_Claims_For_The_Last_Video_Slot_Admit_Exactly_One()
    {
        await AssertLastSlotIsClaimedOnceAsync(isVideo: true);
    }

    private async Task AssertLastSlotIsClaimedOnceAsync(bool isVideo)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await EnablePartyAsync(owner, albumId);

        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            linkId = await db.PartyAlbumLinks.Select(p => p.Id).SingleAsync();
        }

        using var setupScope = _factory.Services.CreateScope();
        var setupParticipants = setupScope.ServiceProvider
            .GetRequiredService<IPartyParticipantService>();
        var participantId = (await setupParticipants.ResolveOrCreateAsync(linkId, null)).ParticipantId;

        // ONE free slot, eight racers. A COUNT-then-INSERT implementation lets
        // several of them observe the same free slot; the conditional UPDATE
        // cannot, because the database evaluates the predicate and applies the
        // increment under one row lock.
        const int max = 1;
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            var participants = scope.ServiceProvider.GetRequiredService<IPartyParticipantService>();
            return await participants.TryClaimSlotAsync(participantId, isVideo, max);
        }));

        Assert.Equal(1, attempts.Count(claimed => claimed));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await verifyDb.PartyParticipants.AsNoTracking().SingleAsync(p => p.Id == participantId);
        Assert.Equal(max, isVideo ? row.AcceptedVideoCount : row.AcceptedPhotoCount);
    }

    [Fact]
    public async Task Claims_Stop_Exactly_At_The_Limit_And_Unlimited_Never_Stops()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await EnablePartyAsync(owner, albumId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participants = scope.ServiceProvider.GetRequiredService<IPartyParticipantService>();
        var linkId = await db.PartyAlbumLinks.Select(p => p.Id).SingleAsync();
        var bounded = (await participants.ResolveOrCreateAsync(linkId, null)).ParticipantId;

        Assert.True(await participants.TryClaimSlotAsync(bounded, isVideo: false, max: 2));
        Assert.True(await participants.TryClaimSlotAsync(bounded, isVideo: false, max: 2));
        Assert.False(await participants.TryClaimSlotAsync(bounded, isVideo: false, max: 2));

        // 0 is the domain's "unlimited" and must never refuse.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await participants.TryClaimSlotAsync(bounded, isVideo: true, max: 0));
        }
    }

    // --- K. OWNER SAFETY ---

    [Fact]
    public async Task A_Foreign_Owner_Cannot_Change_Another_Owners_Party_Settings()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, mallory) = await _factory.CreateAuthenticatedClientAsync("mallory@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice party");
        await EnablePartyAsync(alice, aliceAlbum);

        var response = await mallory.PatchAsJsonAsync(
            $"/api/albums/{aliceAlbum}/party-slideshow-settings", new { photoSlideSeconds = 30 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Alice's setting is untouched.
        var status = await alice.GetFromJsonAsync<JsonElement>($"/api/albums/{aliceAlbum}/party-settings");
        Assert.Equal(PartySlideshowDefaults.PhotoSeconds, status.GetProperty("photoSlideSeconds").GetInt32());
    }

    // --- TV context ---

    [Fact]
    public async Task Tv_Album_Items_Carry_Party_Slideshow_Timing_Only_While_Party_Is_On()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "a.png");

        // ShowOnTv without party: the album reaches the TV but carries no
        // slideshow timing, because there is no party link to take it from.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();

        var cookie = await PairTvAsync(owner);
        var before = await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items");
        Assert.False(before.GetProperty("partyEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("partySlideshow").ValueKind);

        await EnablePartyAsync(owner, albumId);
        await SetSlideshowSettingsAsync(owner, albumId, photoSeconds: 15, maxVideoSeconds: 45);

        var after = await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items");
        Assert.True(after.GetProperty("partyEnabled").GetBoolean());
        var slideshow = after.GetProperty("partySlideshow");
        Assert.Equal(15, slideshow.GetProperty("photoSeconds").GetInt32());
        Assert.Equal(45, slideshow.GetProperty("maxVideoSeconds").GetInt32());
    }

    // --- helpers ---

    private async Task<(string UploadToken, Guid AlbumId)> PartyAsync(int maxPhotos = 0, int maxVideos = 0)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var token = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        if (maxPhotos > 0 || maxVideos > 0)
        {
            await SetSlideshowSettingsAsync(owner, albumId, maxPhotos: maxPhotos, maxVideos: maxVideos);
        }
        return (token, albumId);
    }

    private async Task AssertAlbumIsEmptyAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId));
    }

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var resp = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> SetSlideshowSettingsAsync(
        HttpClient owner, Guid albumId,
        int? photoSeconds = null, int? maxVideoSeconds = null, int? maxPhotos = null, int? maxVideos = null)
    {
        var resp = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-slideshow-settings",
            new
            {
                photoSlideSeconds = photoSeconds,
                maxVideoSlideSeconds = maxVideoSeconds,
                maxPhotoUploadsPerParticipant = maxPhotos,
                maxVideoUploadsPerParticipant = maxVideos,
            });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SessionAsync(HttpClient anon, string uploadToken)
    {
        var resp = await anon.PostAsync($"/api/party/{uploadToken}/upload-session", null);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> UploadJsonAsync(
        HttpClient anon, string uploadToken, params (string Name, byte[] Bytes, string ContentType)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (name, bytes, contentType) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(part, "file", name);
        }
        var response = await anon.PostAsync($"/api/party/{uploadToken}/upload", multipart);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!;
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
    }

    private async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var fileId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<NubArca.Api.Tv.TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = "URDLSUDLR",
                personalCodeConfirmation = "URDLSUDLR",
            })).EnsureSuccessStatusCode();
        var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(NubArca.Api.Tv.TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<JsonElement> TvJsonAsync(string setCookie, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var value = setCookie.Split(';', 2)[0];
        request.Headers.Add("Cookie", $"{NubArca.Api.Tv.TvPairingService.CookieName}={value[(value.IndexOf('=') + 1)..]}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}

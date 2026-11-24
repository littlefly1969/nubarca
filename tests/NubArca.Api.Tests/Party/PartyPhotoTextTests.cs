using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Print;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Party;

public sealed partial class PartyUploadTests
{
    [Fact]
    public async Task Photo_text_is_editable_only_by_uploader_and_owner_without_duplicate_album_items()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Fumetti");
        var status = await EnablePartyAsync(owner, album);
        var view = ViewTokenFromStatus(status);
        var uploader = _factory.CreateClient();
        var stranger = _factory.CreateClient();
        (await UploadAsync(uploader, UploadTokenFromStatus(status),
            ("foto.jpg", ImageFixtures.JpegWithExif(includeGps: true), "image/jpeg"))).EnsureSuccessStatusCode();
        var first = await uploader.GetFromJsonAsync<JsonElement>($"/api/party/{view}/items");
        var item = first.GetProperty("items")[0];
        var id = item.GetProperty("id").GetGuid();
        Assert.True(item.GetProperty("isOwnUpload").GetBoolean());
        Assert.True(item.GetProperty("canEditPhotoText").GetBoolean());
        var foreign = (await stranger.GetFromJsonAsync<JsonElement>($"/api/party/{view}/items")).GetProperty("items")[0];
        Assert.False(foreign.GetProperty("isOwnUpload").GetBoolean());
        Assert.False(foreign.GetProperty("canEditPhotoText").GetBoolean());
        var endpoint = $"/api/party/{view}/photos/{id}/text";
        var write = new { text = "Auguri!", layout = new PhotoTextLayout("bubble", .1, .2), expectedVersion = 0 };
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync(endpoint, write)).StatusCode);
        (await uploader.PutAsJsonAsync(endpoint, write)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await uploader.PutAsJsonAsync(endpoint, write)).StatusCode);
        var edited = await uploader.GetFromJsonAsync<JsonElement>($"/api/party/{view}/items");
        Assert.Single(edited.GetProperty("items").EnumerateArray());
        var preview = edited.GetProperty("items")[0].GetProperty("previewUrl").GetString()!;
        Assert.Contains("v=1", preview);
        var decorated = await uploader.GetByteArrayAsync(preview);
        var original = await uploader.GetByteArrayAsync(preview + "&original=true");
        Assert.False(decorated.SequenceEqual(original));
        using (var safe = Image.Load(decorated)) Assert.Null(safe.Metadata.ExifProfile);
        var televisionCookie = await PairTvAsync(owner);
        var privateAlbum = await CreateAlbumAsync(owner, "Album privato");
        (await owner.PostAsJsonAsync($"/api/albums/{privateAlbum}/items", new { fileItemId = id })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/albums/{privateAlbum}/photos/{id}/text",
            new { text = "Solo nell’album privato", layout = new PhotoTextLayout("bubble"), expectedVersion = 0 })).EnsureSuccessStatusCode();
        async Task<HttpResponseMessage> TvPreview(Guid sourceAlbum)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/media/{id}/preview?album={sourceAlbum}&v=1");
            request.Headers.Add("Cookie", $"{NubArca.Api.Tv.TvPairingService.CookieName}={CookieValue(televisionCookie)}");
            return await _factory.CreateClient().SendAsync(request);
        }
        var tvPhoto = await TvPreview(album);
        tvPhoto.EnsureSuccessStatusCode();
        var tvBytes = await tvPhoto.Content.ReadAsByteArrayAsync();
        Assert.True(decorated.SequenceEqual(tvBytes));
        Assert.Equal(HttpStatusCode.NotFound, (await TvPreview(privateAlbum)).StatusCode);
        // The organizer edits the SAME membership, including another guest's photo.
        (await owner.PutAsJsonAsync($"/api/albums/{album}/photos/{id}/text",
            new { text = "Dall’organizzatore", layout = new PhotoTextLayout("overlay"), expectedVersion = 1 })).EnsureSuccessStatusCode();
        var current = await uploader.GetFromJsonAsync<JsonElement>(endpoint);
        Assert.Equal(2, current.GetProperty("version").GetInt64());
        Assert.Equal("Dall’organizzatore", current.GetProperty("text").GetString());
        (await uploader.PutAsJsonAsync(endpoint, new { text = (string?)null, layout = (object?)null, expectedVersion = 2 })).EnsureSuccessStatusCode();
        var restored = await uploader.GetByteArrayAsync(item.GetProperty("previewUrl").GetString()! + "?v=3");
        Assert.True(original.SequenceEqual(restored));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AlbumItems.CountAsync(i => i.AlbumId == album));
        Assert.Equal(0, await db.PartyGuestbookEntries.CountAsync());
    }

    [Fact]
    public async Task Legacy_uploads_owner_photos_videos_and_foreign_albums_never_grant_guest_edit_rights()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Solo mie");
        var status = await EnablePartyAsync(owner, album);
        var view = ViewTokenFromStatus(status);
        var id = await AddPngAsync(owner, album, "host.png");
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PutAsJsonAsync($"/api/party/{view}/photos/{id}/text",
            new { text = "No", layout = new PhotoTextLayout("bubble"), expectedVersion = 0 })).StatusCode);
        var (_, otherOwner) = await _factory.CreateAuthenticatedClientAsync("another@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.PutAsJsonAsync($"/api/albums/{album}/photos/{id}/text",
            new { text = "No", layout = new PhotoTextLayout("bubble"), expectedVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/albums/{album}/photos/{id}/text",
            new { text = "No", layout = new PhotoTextLayout("below"), expectedVersion = 0 })).StatusCode);
        (await owner.PutAsJsonAsync($"/api/albums/{album}/photos/{id}/text",
            new { text = "Sì", layout = new PhotoTextLayout("bubble"), expectedVersion = 0 })).EnsureSuccessStatusCode();
        var mine = (await owner.GetFromJsonAsync<JsonElement>($"/api/party/{view}/items")).GetProperty("items")[0];
        Assert.True(mine.GetProperty("isOwnUpload").GetBoolean());
        // Owner privilege is not attribution: a guest upload with no recorded
        // uploader is not labelled as the organizer's photograph.
        (await UploadAsync(guest, UploadTokenFromStatus(status), ("legacy.jpg", ImageFixtures.JpegWithExif(includeGps: false), "image/jpeg"))).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PartyUploadItems.SingleAsync(i => i.AlbumId == album);
            row.PartyParticipantId = null; await db.SaveChangesAsync();
        }
        var rows = (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{view}/items")).GetProperty("items");
        Assert.All(rows.EnumerateArray(), i => Assert.False(i.GetProperty("isOwnUpload").GetBoolean()));
        Assert.All(rows.EnumerateArray(), i => Assert.False(i.GetProperty("canEditPhotoText").GetBoolean()));
    }
}

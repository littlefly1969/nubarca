using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-ALBUM-04: the recipient browses a shared album the way the owner
// browses theirs — kind tabs, paging, sequential playback — WITHOUT gaining a
// single owner capability or one field of owner-private metadata.
//
// The invariant these tests defend: the shared item endpoint may grow a filter
// and a cursor, and it may not grow a vocabulary. `kind` is answered from the
// media category the shape already carried; the cursor is `(SortOrder,
// FileItemId)`, both of which the recipient already holds. Anything that would
// have to consult the owner's library to answer is absent by construction.
public sealed class SharedAlbumBrowsingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public SharedAlbumBrowsingTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string ViewerEmail = "bob@example.com";
    private const string StrangerEmail = "carol@example.com";

    // ── Paging ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Paging_Walks_The_Curated_Order_Exactly_Once()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var added = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            added.Add(await AddPngAsync(owner, albumId, $"p{i}.png"));
        }

        await InviteAndAcceptAsync(owner, viewer, albumId);

        var whole = await PageAsync(viewer, albumId);
        var expected = Ids(whole.GetProperty("items"));
        Assert.Equal(5, expected.Count);
        Assert.Equal(added, expected);

        // Two at a time, following the server's own cursor.
        var walked = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(viewer, albumId, limit: 2, cursor: cursor);
            walked.AddRange(Ids(page.GetProperty("items")));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("nextCursor").GetString();
            pages++;
            Assert.True(pages <= 5, "the cursor did not terminate");
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        // Same order, every item once — no gap, no repeat at a page boundary.
        Assert.Equal(expected, walked);
        Assert.Equal(walked.Count, walked.Distinct().Count());
    }

    [Fact]
    public async Task The_Last_Page_Says_So_Instead_Of_Inviting_Another_Request()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        await AddPngAsync(owner, albumId, "a.png");
        await AddPngAsync(owner, albumId, "b.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // A limit that exactly equals the album size must NOT advertise a next
        // page: whether more exists is read from the data, not guessed from a
        // full page.
        var page = await PageAsync(viewer, albumId, limit: 2);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task An_Empty_Album_Is_An_Empty_Page_Not_An_Error()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Empty");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        var page = await PageAsync(viewer, albumId);
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Limit_Is_Clamped_Rather_Than_Obeyed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        await AddPngAsync(owner, albumId, "a.png");
        await AddPngAsync(owner, albumId, "b.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // Zero and negative are nonsense, not a way to ask for nothing; an
        // enormous limit is not a way to ask for the whole library.
        foreach (var limit in new[] { 0, -5, 100000 })
        {
            var page = await PageAsync(viewer, albumId, limit: limit);
            Assert.InRange(page.GetProperty("items").GetArrayLength(), 1, 2);
        }
    }

    // ── Kind filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Kind_Filters_The_Items_While_The_Counts_Describe_The_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var photo = await AddPngAsync(owner, albumId, "still.png");
        var clip = await AddPngAsync(owner, albumId, "clip.png");
        await MakeConfirmedVideoAsync(clip);
        await InviteAndAcceptAsync(owner, viewer, albumId);

        var all = await PageAsync(viewer, albumId);
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Equal(1, all.GetProperty("photoCount").GetInt32());
        Assert.Equal(1, all.GetProperty("videoCount").GetInt32());

        var photos = await PageAsync(viewer, albumId, kind: "image");
        Assert.Equal([photo], Ids(photos.GetProperty("items")));
        // The counts do not change with the tab — they are the album's, so a
        // "Videos 1" label stays true while Photos is open.
        Assert.Equal(1, photos.GetProperty("videoCount").GetInt32());
        Assert.Equal(2, photos.GetProperty("total").GetInt32());

        var videos = await PageAsync(viewer, albumId, kind: "video");
        Assert.Equal([clip], Ids(videos.GetProperty("items")));
        var video = videos.GetProperty("items")[0];
        Assert.Equal("video", video.GetProperty("kind").GetString());
        // A shared video is PLAYABLE: both the album-scoped player URL and its
        // poster are present, and neither is an /api/files route.
        Assert.Equal($"/api/shared-albums/{albumId}/media/{clip}/video",
            video.GetProperty("videoUrl").GetString());
        Assert.Equal($"/api/shared-albums/{albumId}/media/{clip}/poster",
            video.GetProperty("posterUrl").GetString());
        // The BYTES of that route need a published HLS ladder, which no unit
        // fixture has — GetSharedAlbumVideo is deliberately HLS-only so a share
        // can never hand over the untouched original through a playback URL.
        // What this test owns is that the recipient is TOLD to play from the
        // album-scoped route and never from an owner one.
        Assert.DoesNotContain("/api/files/", video.GetProperty("videoUrl").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Unknown_Kind_Is_Refused_Rather_Than_Widened()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        foreach (var kind in new[] { "photos", "everything", "IMAGES" })
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await viewer.GetAsync($"/api/shared-albums/{albumId}/items?kind={kind}")).StatusCode);
        }

        // An ABSENT kind is the client that has never heard of the parameter.
        (await viewer.GetAsync($"/api/shared-albums/{albumId}/items")).EnsureSuccessStatusCode();
    }

    // ── Cursor integrity ────────────────────────────────────────────────────

    [Fact]
    public async Task A_Cursor_Issued_For_One_Kind_Is_Refused_On_Another()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        for (var i = 0; i < 3; i++)
        {
            await AddPngAsync(owner, albumId, $"p{i}.png");
        }

        await InviteAndAcceptAsync(owner, viewer, albumId);

        var page = await PageAsync(viewer, albumId, kind: "image", limit: 1);
        var cursor = page.GetProperty("nextCursor").GetString()!;

        // Same cursor, same album, different question: refused, because the
        // boundary means something else in another sequence.
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(
            $"/api/shared-albums/{albumId}/items?kind=all&cursor={Uri.EscapeDataString(cursor)}")).StatusCode);
        // And it still works for the sequence it was issued for.
        (await viewer.GetAsync(
            $"/api/shared-albums/{albumId}/items?kind=image&cursor={Uri.EscapeDataString(cursor)}"))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_Malformed_Cursor_Is_One_Refusal_Whatever_Is_Wrong_With_It()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        foreach (var cursor in new[] { "not-base64!!", "eyJ9", "AAAA", "e30" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(
                $"/api/shared-albums/{albumId}/items?cursor={Uri.EscapeDataString(cursor)}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_Non_Member_Is_Refused_Before_The_Cursor_Is_Even_Read()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // 404, never 400: a malformed-cursor answer would confirm that the
        // album exists to somebody with no grant on it.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(
            $"/api/shared-albums/{albumId}/items?cursor=not-base64!!")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(
            $"/api/shared-albums/{albumId}/items?kind=nonsense")).StatusCode);
    }

    [Fact]
    public async Task Revocation_Stops_The_Very_Next_Page()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        for (var i = 0; i < 4; i++)
        {
            await AddPngAsync(owner, albumId, $"p{i}.png");
        }

        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);

        var first = await PageAsync(viewer, albumId, limit: 2);
        var cursor = first.GetProperty("nextCursor").GetString()!;

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();

        // A cursor is not a capability: it survives nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(
            $"/api/shared-albums/{albumId}/items?cursor={Uri.EscapeDataString(cursor)}")).StatusCode);
    }

    // ── Privacy of the serialized page ──────────────────────────────────────

    [Fact]
    public async Task The_Page_Carries_No_Owner_Private_Field_On_The_Wire()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        // A filename that would be a leak all by itself.
        var photo = await AddPngAsync(owner, albumId, "compleanno-di-marco.png");
        var clip = await AddPngAsync(owner, albumId, "vacanza-con-anna.png");
        await MakeConfirmedVideoAsync(clip);
        var facts = await StorageFactsAsync(photo);
        await InviteAndAcceptAsync(owner, viewer, albumId);

        foreach (var query in new[] { "", "?kind=image", "?kind=video", "?limit=1" })
        {
            var body = await (await viewer.GetAsync(
                $"/api/shared-albums/{albumId}/items{query}")).Content.ReadAsStringAsync();

            // The owner's file names.
            Assert.DoesNotContain("compleanno", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vacanza", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".png", body, StringComparison.OrdinalIgnoreCase);

            // Storage internals.
            Assert.DoesNotContain(facts.Sha256, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(facts.StorageKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(facts.BlobObjectId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/storage/", body, StringComparison.OrdinalIgnoreCase);

            // Identity of anybody, and the owner's semantic layer.
            Assert.DoesNotContain(ownerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@example.com", body, StringComparison.OrdinalIgnoreCase);

            // The vocabulary itself. A field named for owner-private data is a
            // leak even when this fixture's value happens to be null.
            foreach (var forbidden in new[]
                     {
                         "name", "fileName", "displayName", "title",
                         "storageKey", "blobId", "blobObjectId", "sha", "sha256", "path",
                         "gps", "latitude", "longitude", "location",
                         "person", "people", "face", "ocr", "caption", "embedding", "vector",
                         "rating", "favorite", "favourite", "tag",
                         "excluded", "vault", "trash", "deleted", "ownerUserId", "contributor",
                     })
            {
                Assert.DoesNotContain($"\"{forbidden}\"", body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task A_Page_Exposes_Exactly_The_Agreed_Item_Fields()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        var page = await PageAsync(viewer, albumId);

        // The envelope, stated positively: a new field cannot appear here
        // without a test saying out loud that it is safe for a recipient.
        Assert.Equal(
            ["items", "nextCursor", "total", "photoCount", "videoCount"],
            page.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(
            [
                "fileItemId", "kind", "thumbnailUrl", "previewUrl", "posterUrl", "videoUrl",
                "downloadUrl", "albumItemId", "width", "height", "addedAt", "canWithdraw",
            ],
            page.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Every_Media_Url_On_A_Page_Is_Album_Scoped()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var photo = await AddPngAsync(owner, albumId, "a.png");
        var clip = await AddPngAsync(owner, albumId, "b.png");
        await MakeConfirmedVideoAsync(clip);
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();

        var body = await (await viewer.GetAsync(
            $"/api/shared-albums/{albumId}/items")).Content.ReadAsStringAsync();

        // Not one owner route, including the download the membership DOES
        // permit — permission widens what the album serves, never where from.
        Assert.DoesNotContain("/api/files/", body, StringComparison.OrdinalIgnoreCase);
        var page = await PageAsync(viewer, albumId);
        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            foreach (var key in new[] { "thumbnailUrl", "previewUrl", "posterUrl", "videoUrl", "downloadUrl" })
            {
                var url = item.GetProperty(key).GetString();
                if (url is null)
                {
                    continue;
                }

                Assert.StartsWith($"/api/shared-albums/{albumId}/media/", url, StringComparison.Ordinal);
                // A route, not a bearer: nothing signed, nothing that expires.
                Assert.DoesNotContain("token", url, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("?", url, StringComparison.Ordinal);
            }
        }

        _ = photo;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<JsonElement> PageAsync(
        HttpClient client, Guid albumId, string? kind = null, int? limit = null, string? cursor = null)
    {
        var query = new List<string>();
        if (kind is not null) query.Add($"kind={Uri.EscapeDataString(kind)}");
        if (limit is not null) query.Add($"limit={limit}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;

        var response = await client.GetAsync($"/api/shared-albums/{albumId}/items{suffix}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<Guid> Ids(JsonElement items) =>
        items.EnumerateArray().Select(i => i.GetProperty("fileItemId").GetGuid()).ToList();

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // Distinct bytes per fixture: storage is content-addressed, so two
    // identically-generated PNGs would share ONE BlobObject and turning one into
    // a video would silently turn the other into one too.
    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> InviteAndAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email = ViewerEmail)
    {
        var invited = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email });
        invited.EnsureSuccessStatusCode();
        var membershipId = (await invited.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();
        return membershipId;
    }

    // A server-confirmed video without a real ffmpeg run — the same fixture
    // shape AlbumSharingTests uses.
    private async Task MakeConfirmedVideoAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileItemId);
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.DetectedContentType = "video/mp4";
        meta.VideoExtractionStatus = "completed";
        meta.VideoCodec = "h264";
        await db.SaveChangesAsync();
    }

    private async Task<(Guid BlobObjectId, string Sha256, string StorageKey)> StorageFactsAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileItemId);
        var blob = await db.BlobObjects.FirstAsync(b => b.Id == file.BlobObjectId);
        return (blob.Id, blob.Sha256, blob.StorageKey);
    }
}
